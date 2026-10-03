// IPC Bridge for communication with Rust harmony-server
// Acts as a SERVER - listens for connection from Rust side
// Protocol: length-prefixed JSON over Unix socket

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using GameRL.Harmony.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameRL.Harmony
{
    /// <summary>
    /// IPC bridge that LISTENS for connections from Rust harmony-server.
    /// Thread-safe: receive runs on background thread, commands queued for main thread.
    /// </summary>
    public class Bridge : IDisposable
    {
        private Socket? _listenSocket;
        private Socket? _clientSocket;
        private NetworkStream? _stream;
        private Thread? _listenThread;
        private Thread? _receiveThread;
        private readonly ConcurrentQueue<GameMessage> _incomingQueue;
        private volatile bool _running;
        private readonly object _sendLock = new();
        private string? _socketPath;

        public event Action<RegisterAgentMessage>? OnRegisterAgent;
        public event Action<DeregisterAgentMessage>? OnDeregisterAgent;
        public event Action<ExecuteActionMessage>? OnExecuteAction;
        public event Action<ConfigureStreamsMessage>? OnConfigureStreams;
        public event Action<ResetMessage>? OnReset;
        public event Action<GetStateHashMessage>? OnGetStateHash;
        public event Action? OnShutdown;
        public event Action? OnClientConnected;

        public bool IsConnected => _running && _clientSocket?.Connected == true;
        public bool IsListening => _running && _listenSocket != null;

        public Bridge()
        {
            _incomingQueue = new ConcurrentQueue<GameMessage>();
        }

        /// <summary>
        /// Start listening for connections from Rust harmony-bridge
        /// </summary>
        public bool Listen(string endpointSpec)
        {
            var endpointPath = endpointSpec;

            // host:port means TCP, anything else is a Unix socket path. The Rust
            // side applies the identical rule in Endpoint::parse, so both ends
            // agree on what the string means without extra configuration.
            if (TryParseTcpEndpoint(endpointSpec, out var host, out var port))
            {
                return ListenTcp(host, port);
            }

            return ListenUnix(endpointSpec);
        }

        /// <summary>
        /// Parse `host:port`, rejecting anything that is not loopback.
        ///
        /// This bridge can drive the colony and read the whole game state. It
        /// listens on loopback only, so a stray configuration cannot expose it
        /// to the network.
        /// </summary>
        private static bool TryParseTcpEndpoint(string spec, out string host, out int port)
        {
            host = "";
            port = 0;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            var idx = spec.LastIndexOf(':');
            if (idx <= 0) return false;

            var h = spec.Substring(0, idx).Trim();
            var p = spec.Substring(idx + 1).Trim();

            if (h.Length == 0 || !int.TryParse(p, out port) || port < 0 || port > 65535) return false;
            if (h.IndexOf('/') >= 0 || h.IndexOf('\\') >= 0) return false;

            if (!IPAddress.TryParse(h, out var addr) && h != "localhost")
            {
                return false;
            }
            if (h != "localhost" && IPAddress.TryParse(h, out var parsed) && !IPAddress.IsLoopback(parsed))
            {
                throw new InvalidOperationException(
                    $"refusing to bind the game bridge to non-loopback address '{h}'. " +
                    "This bridge exposes full colony state and control.");
            }

            host = h;
            return true;
        }

        private bool ListenTcp(string host, int port)
        {
            IPAddress addr;
            if (host == "localhost")
            {
                // Loopback only: bind v4 explicitly rather than resolving, which
                // could pick ::1 and mismatch the client's view.
                addr = IPAddress.Loopback;
            }
            else if (!IPAddress.TryParse(host, out var parsed) || !IPAddress.IsLoopback(parsed))
            {
                LogError($"TCP endpoint '{host}:{port}' is not loopback; refusing");
                return false;
            }
            else
            {
                addr = parsed;
            }

            try
            {
                _listenSocket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                _listenSocket.Bind(new IPEndPoint(addr, port));
            }
            catch (Exception ex)
            {
                LogError($"BIND FAILED on tcp {host}:{port}: {Describe(ex)}");
                return false;
            }

            try
            {
                _listenSocket.Listen(1);
            }
            catch (Exception ex)
            {
                LogError($"LISTEN FAILED on tcp {host}:{port}: {Describe(ex)}");
                return false;
            }

            var actual = ((IPEndPoint)_listenSocket.LocalEndPoint!).Port;
            Log($"[GameRL] endpoint: tcp {addr}:{actual}");
            AnnounceEndpoint($"tcp {addr}:{actual}");
            return StartListener();
        }

        /// <summary>
        /// Publish the resolved endpoint so a client can discover an ephemeral
        /// port instead of guessing it.
        /// </summary>
        private void AnnounceEndpoint(string endpoint)
        {
            var path = Environment.GetEnvironmentVariable("GAMERL_ENDPOINT_FILE");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, endpoint);
            }
            catch (Exception ex)
            {
                LogError($"could not write endpoint file '{path}': {Describe(ex)}");
            }
        }

        private bool StartListener()
        {
            try
            {
                _running = true;
                _listenThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "GameRL-Listen"
                };
                _listenThread.Start();

                Log("Listening");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"LISTEN THREAD FAILED: {Describe(ex)}");
                _running = false;
                return false;
            }
        }

        private bool ListenUnix(string endpointPath)
        {
            try
            {
                _socketPath = endpointPath;

                // Remove existing socket file if present
                if (File.Exists(endpointPath))
                {
                    File.Delete(endpointPath);
                }

                var endpoint = new UnixEndPoint(endpointPath);
                _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                // Bind and Listen are separated because Mono under Wine reports a
                // bare "Error looking up error string" for either, which makes
                // "AF_UNIX unsupported" and "path not writable" indistinguishable.
                _listenSocket.Bind(endpoint);
            }
            catch (Exception ex)
            {
                LogError($"BIND FAILED on '{endpointPath}': {Describe(ex)}");
                return false;
            }

            try
            {
                _listenSocket.Listen(1);
            }
            catch (Exception ex)
            {
                LogError($"LISTEN FAILED on '{endpointPath}': {Describe(ex)}");
                return false;
            }

            Log($"[GameRL] endpoint: unix {endpointPath}");
            AnnounceEndpoint($"unix {endpointPath}");
            return StartListener();
        }

        /// <summary>
        /// Expand an exception into something actionable.
        ///
        /// <see cref="Exception.Message"/> alone is not enough here: Mono under
        /// Wine cannot map the native errno for socket calls and returns
        /// "Error looking up error string", which rules out every diagnosis.
        /// </summary>
        private static string Describe(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);

            if (ex is SocketException se)
            {
                sb.Append(" | SocketErrorCode=").Append(se.SocketErrorCode)
                  .Append(" ErrorCode=").Append(se.ErrorCode)
                  .Append(" NativeErrorCode=").Append(se.NativeErrorCode);
            }
            sb.Append(" | HResult=0x").Append(ex.HResult.ToString("X8"));
            sb.Append(" | errno=").Append(Marshal.GetLastWin32Error());

            var inner = ex.InnerException;
            while (inner != null)
            {
                sb.Append(" || inner ").Append(inner.GetType().FullName)
                  .Append(": ").Append(inner.Message);
                inner = inner.InnerException;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Background thread: wait for client connection
        /// </summary>
        private void ListenLoop()
        {
            while (_running && _listenSocket != null)
            {
                try
                {
                    Log("Waiting for Rust harmony-bridge connection...");
                    _clientSocket = _listenSocket.Accept();
                    _stream = new NetworkStream(_clientSocket, ownsSocket: false);

                    Log("Client connected!");
                    OnClientConnected?.Invoke();

                    // Start receive loop for this client
                    _receiveThread = new Thread(ReceiveLoop)
                    {
                        IsBackground = true,
                        Name = "GameRL-Receive"
                    };
                    _receiveThread.Start();

                    // Wait for receive thread to finish (client disconnect)
                    _receiveThread.Join();

                    // Clean up client connection
                    _stream?.Dispose();
                    _stream = null;
                    _clientSocket?.Dispose();
                    _clientSocket = null;

                    // Clear any stale messages from previous session
                    while (_incomingQueue.TryDequeue(out _)) { }

                    Log("Client disconnected, waiting for new connection...");
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    // Socket was closed, exit gracefully
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        LogError($"Listen error: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Background thread: continuously receive messages from connected client
        /// </summary>
        private void ReceiveLoop()
        {
            var lenBuffer = new byte[4];

            while (_running && _stream != null && _clientSocket?.Connected == true)
            {
                try
                {
                    // Read 4-byte length prefix (little-endian, matching Rust)
                    if (!ReadExact(lenBuffer, 0, 4))
                    {
                        Log("Connection closed by client");
                        break;
                    }

                    int length = BitConverter.ToInt32(lenBuffer, 0);
                    if (length <= 0 || length > 10_000_000)
                    {
                        LogError($"Invalid message length: {length}");
                        break;
                    }

                    // Read message body
                    var data = new byte[length];
                    if (!ReadExact(data, 0, length))
                    {
                        Log("Connection closed while reading message");
                        break;
                    }

                    // Deserialize and queue for main thread
                    var message = DeserializeMessage(data);
                    if (message != null)
                    {
                        _incomingQueue.Enqueue(message);
                    }
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        LogError($"Receive error: {ex.Message}");
                    }
                    break;
                }
            }
        }

        private bool ReadExact(byte[] buffer, int offset, int count)
        {
            if (_stream == null) return false;

            int totalRead = 0;
            while (totalRead < count)
            {
                int read = _stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0) return false;
                totalRead += read;
            }
            return true;
        }

        /// <summary>
        /// Deserialize incoming JSON message based on "Type" field (PascalCase)
        /// </summary>
        private GameMessage? DeserializeMessage(byte[] data)
        {
            try
            {
                var json = Encoding.UTF8.GetString(data);

                var obj = JObject.Parse(json);
                var type = obj["Type"]?.ToString();

                if (string.IsNullOrEmpty(type))
                {
                    LogError("Message missing 'Type' field");
                    return null;
                }

                return type switch
                {
                    "RegisterAgent" => ParseRegisterAgent(obj),
                    "DeregisterAgent" => ParseDeregisterAgent(obj),
                    "ExecuteAction" => ParseExecuteAction(obj),
                    "ConfigureStreams" => ParseConfigureStreams(obj),
                    "Reset" => ParseReset(obj),
                    "GetStateHash" => new GetStateHashMessage(),
                    "Shutdown" => new ShutdownMessage(),
                    _ => null
                };
            }
            catch (Exception ex)
            {
                LogError($"Deserialize error: {ex.Message}");
                return null;
            }
        }

        private RegisterAgentMessage ParseRegisterAgent(JObject obj)
        {
            var msg = new RegisterAgentMessage
            {
                AgentId = obj["AgentId"]?.ToString() ?? "",
                AgentType = obj["AgentType"]?.ToString() ?? ""
            };

            var config = obj["Config"] as JObject;
            if (config != null)
            {
                msg.Config = new AgentConfig
                {
                    EntityId = config["EntityId"]?.ToString(),
                    ObservationProfile = config["ObservationProfile"]?.ToString() ?? "default"
                };
            }

            return msg;
        }

        private DeregisterAgentMessage ParseDeregisterAgent(JObject obj)
        {
            return new DeregisterAgentMessage
            {
                AgentId = obj["AgentId"]?.ToString() ?? ""
            };
        }

        private ExecuteActionMessage ParseExecuteAction(JObject obj)
        {
            // Convert JToken action to Dictionary for HarmonyRPC dispatch
            object? action = null;
            var actionToken = obj["Action"];
            if (actionToken != null && actionToken.Type != JTokenType.Null)
            {
                action = actionToken.ToObject<Dictionary<string, object>>();
            }

            return new ExecuteActionMessage
            {
                AgentId = obj["AgentId"]?.ToString() ?? "",
                Action = action,
                Ticks = obj["Ticks"]?.ToObject<uint>() ?? 1
            };
        }

        private ResetMessage ParseReset(JObject obj)
        {
            return new ResetMessage
            {
                Seed = obj["Seed"]?.ToObject<ulong?>(),
                Scenario = obj["Scenario"]?.ToString()
            };
        }

        private ConfigureStreamsMessage ParseConfigureStreams(JObject obj)
        {
            return new ConfigureStreamsMessage
            {
                AgentId = obj["AgentId"]?.ToString() ?? "",
                Profile = obj["Profile"]?.ToString() ?? "default"
            };
        }

        /// <summary>
        /// Process pending commands from the server. Call from main game thread.
        /// </summary>
        public void ProcessCommands()
        {
            while (_incomingQueue.TryDequeue(out var message))
            {
                try
                {
                    switch (message)
                    {
                        case RegisterAgentMessage m:
                            OnRegisterAgent?.Invoke(m);
                            break;
                        case DeregisterAgentMessage m:
                            OnDeregisterAgent?.Invoke(m);
                            break;
                        case ExecuteActionMessage m:
                            OnExecuteAction?.Invoke(m);
                            break;
                        case ConfigureStreamsMessage m:
                            OnConfigureStreams?.Invoke(m);
                            break;
                        case ResetMessage m:
                            OnReset?.Invoke(m);
                            break;
                        case GetStateHashMessage m:
                            OnGetStateHash?.Invoke(m);
                            break;
                        case ShutdownMessage:
                            OnShutdown?.Invoke();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Handler error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Send Ready message to Rust server
        /// </summary>
        public void SendReady(string name, string version, GameCapabilities capabilities)
        {
            Send(new ReadyMessage
            {
                Name = name,
                Version = version,
                Capabilities = capabilities
            });
        }

        /// <summary>
        /// Send agent registration response
        /// </summary>
        public void SendAgentRegistered(string agentId, object observationSpace, object actionSpace)
        {
            Send(new AgentRegisteredMessage
            {
                AgentId = agentId,
                ObservationSpace = observationSpace,
                ActionSpace = actionSpace
            });
        }

        /// <summary>
        /// Send step result with observation and reward
        /// </summary>
        public void SendStepResult(
            string agentId,
            object observation,
            double reward,
            Dictionary<string, double> rewardComponents,
            bool done,
            bool truncated,
            string? stateHash = null,
            StepMetricsMessage? metrics = null)
        {
            Send(new StepResultMessage
            {
                Metrics = metrics,
                AgentId = agentId,
                Observation = observation,
                Reward = reward,
                RewardComponents = rewardComponents,
                Done = done,
                Truncated = truncated,
                StateHash = stateHash
            });
        }

        /// <summary>
        /// Send step results for multiple agents
        /// </summary>
        public void SendBatchStepResult(List<StepResultMessage> results)
        {
            Send(new BatchStepResultMessage
            {
                Results = results ?? new List<StepResultMessage>()
            });
        }

        /// <summary>
        /// Send reset complete with initial observation
        /// </summary>
        public void SendResetComplete(object observation, string? stateHash = null)
        {
            Send(new ResetCompleteMessage
            {
                Observation = observation,
                StateHash = stateHash
            });
        }

        /// <summary>
        /// Send error response
        /// </summary>
        public void SendError(int code, string message)
        {
            Send(new ErrorMessage
            {
                Code = code,
                Message = message
            });
        }

        /// <summary>
        /// Send state hash response
        /// </summary>
        public void SendStateHash(string hash)
        {
            Send(new StateHashMessage
            {
                Hash = hash
            });
        }

        /// <summary>
        /// Send vision stream configuration response
        /// </summary>
        public void SendStreamsConfigured(string agentId, List<Dictionary<string, object>> descriptors)
        {
            Send(new StreamsConfiguredMessage
            {
                AgentId = agentId,
                Descriptors = descriptors ?? new List<Dictionary<string, object>>()
            });
        }

        /// <summary>
        /// Send state update (async notification)
        /// </summary>
        public void SendStateUpdate(ulong tick, object state, List<GameEvent>? events = null)
        {
            Send(new StateUpdateMessage
            {
                Tick = tick,
                State = state,
                Events = events ?? new List<GameEvent>()
            });
        }

        private void Send(GameMessage message)
        {
            if (!_running || _stream == null || _clientSocket?.Connected != true) return;

            try
            {
                // Serialize message to JSON
                var json = SerializeMessage(message);
                var data = Encoding.UTF8.GetBytes(json);

                // Length prefix (4 bytes, little-endian to match Rust)
                var lenBytes = BitConverter.GetBytes(data.Length);

                lock (_sendLock)
                {
                    _stream.Write(lenBytes, 0, 4);
                    _stream.Write(data, 0, data.Length);
                    _stream.Flush();
                }
            }
            catch (Exception ex)
            {
                LogError($"Send error: {ex.Message}");
            }
        }

        /// <summary>
        /// Serialize message to JSON with "Type" field for Rust serde compatibility (PascalCase)
        /// </summary>
        private string SerializeMessage(GameMessage message)
        {
            var obj = new JObject();
            obj["Type"] = message.Type;

            switch (message)
            {
                case ReadyMessage m:
                    obj["Name"] = m.Name;
                    obj["Version"] = m.Version;
                    obj["Capabilities"] = JObject.FromObject(new
                    {
                        MultiAgent = m.Capabilities.MultiAgent,
                        MaxAgents = m.Capabilities.MaxAgents,
                        Deterministic = m.Capabilities.Deterministic,
                        Headless = m.Capabilities.Headless
                    });
                    break;

                case StepResultMessage m:
                {
                    var stepObj = SerializeStepResult(m);
                    foreach (var prop in stepObj)
                    {
                        obj[prop.Key] = prop.Value;
                    }
                    break;
                }

                case BatchStepResultMessage m:
                {
                    var results = new JArray();
                    foreach (var result in m.Results ?? new List<StepResultMessage>())
                    {
                        results.Add(SerializeStepResult(result));
                    }
                    obj["Results"] = results;
                    break;
                }

                case ResetCompleteMessage m:
                    obj["Observation"] = JToken.FromObject(m.Observation ?? new object());
                    if (m.StateHash != null)
                        obj["StateHash"] = m.StateHash;
                    break;

                case AgentRegisteredMessage m:
                    obj["AgentId"] = m.AgentId;
                    obj["ObservationSpace"] = JToken.FromObject(m.ObservationSpace ?? new object());
                    obj["ActionSpace"] = JToken.FromObject(m.ActionSpace ?? new object());
                    break;

                case StateHashMessage m:
                    obj["Hash"] = m.Hash;
                    break;

                case StreamsConfiguredMessage m:
                    obj["AgentId"] = m.AgentId;
                    obj["Descriptors"] = JToken.FromObject(m.Descriptors ?? new List<Dictionary<string, object>>());
                    break;

                case ErrorMessage m:
                    obj["Code"] = m.Code;
                    obj["Message"] = m.Message;
                    break;

                case StateUpdateMessage m:
                    obj["Tick"] = m.Tick;
                    obj["State"] = JToken.FromObject(m.State ?? new object());
                    obj["Events"] = JToken.FromObject(m.Events ?? new List<GameEvent>());
                    break;
            }

            return obj.ToString(Formatting.None);
        }

        private static JObject SerializeStepResult(StepResultMessage message)
        {
            var obj = new JObject
            {
                ["AgentId"] = message.AgentId,
                ["Observation"] = JToken.FromObject(message.Observation ?? new object()),
                ["Reward"] = message.Reward,
                ["RewardComponents"] = JToken.FromObject(message.RewardComponents ?? new Dictionary<string, double>()),
                ["Done"] = message.Done,
                ["Truncated"] = message.Truncated
            };

            if (message.StateHash != null)
            {
                obj["StateHash"] = message.StateHash;
            }

            if (message.Metrics != null)
            {
                obj["Metrics"] = new JObject
                {
                    ["StepMs"] = message.Metrics.StepMs,
                    ["TickMs"] = message.Metrics.TickMs,
                    ["ExecuteMs"] = message.Metrics.ExecuteMs,
                    ["ExtractMs"] = message.Metrics.ExtractMs,
                    ["RewardMs"] = message.Metrics.RewardMs,
                    ["CaptureMs"] = message.Metrics.CaptureMs
                };
            }

            return obj;
        }

        public void Dispose()
        {
            _running = false;

            // Close sockets to unblock threads
            _stream?.Dispose();
            _clientSocket?.Dispose();
            _listenSocket?.Dispose();

            // Clean up socket file
            if (_socketPath != null && File.Exists(_socketPath))
            {
                try { File.Delete(_socketPath); } catch { }
            }

            _listenThread?.Join(1000);
            _receiveThread?.Join(1000);
        }

        // Logging helpers - override in game-specific implementation
        protected virtual void Log(string message)
        {
            Console.WriteLine($"[GameRL] {message}");
        }

        protected virtual void LogError(string message)
        {
            Console.Error.WriteLine($"[GameRL] ERROR: {message}");
        }
    }
}
