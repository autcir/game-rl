// RimWorld GameRL Mod Entry Point

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using HarmonyLib;
using Verse;
using RimWorld;
using GameRL.Harmony;
using GameRL.Harmony.Protocol;
using RimWorld.GameRL.State;
using RimWorld.GameRL.Actions;

namespace RimWorld.GameRL
{
    /// <summary>
    /// Main mod class - initializes on game startup
    /// </summary>
    [StaticConstructorOnStartup]
    public static class GameRLMod
    {
        private static Bridge? _bridge;
        private static RimWorldStateExtractor? _stateExtractor;
        private static RimWorldCommandExecutor? _commandExecutor;
        private static readonly Dictionary<string, AgentState> _agents = new();
        private static readonly Dictionary<string, VisionStreamManager> _visionStreams = new();

        /// <summary>
        /// Public accessor for state extractor (used by event capture patches)
        /// </summary>
        public static RimWorldStateExtractor? StateExtractor => _stateExtractor;

        /// <summary>
        /// Public accessor for command executor (used for agent configuration)
        /// </summary>
        public static RimWorldCommandExecutor? CommandExecutor => _commandExecutor;

        // Step tracking
        private static ulong _currentStepId;
        private static string? _currentAgentId;
        private static uint _ticksRemaining;
        private static bool _stepInProgress;
        private static bool _forcingTicks;

        // Per-step phase timings. A slow simulation and a slow bridge look the
        // same from outside the process and call for opposite responses, so the
        // split is reported rather than just the total.
        private static readonly Stopwatch _stepWatch = new();
        private static double _tickMs;
        private static double _executeMs;
        private static double _captureMs;

        // Event push tracking
        private static int _ticksSinceLastEventPush;
        private const int EventPushIntervalTicks = 60; // Push events every ~1 second at normal speed

        static GameRLMod()
        {
            // Delay initialization to avoid interfering with def resolution
            LongEventHandler.ExecuteWhenFinished(Initialize);
        }

        private static void Initialize()
        {
            Log.Message("[GameRL] Initializing Arkavo Game-RL adapter...");

            try
            {
                // Apply Harmony patches
                var harmony = new HarmonyLib.Harmony("arkavo.gamerl.rimworld");
                harmony.PatchAll();
                Log.Message("[GameRL] Harmony patches applied");

                // Initialize components
                _stateExtractor = new RimWorldStateExtractor();
                _commandExecutor = new RimWorldCommandExecutor();

                // Start listening for harmony-server connection
                var socketPath = GetSocketPath();
                Log.Message(
                    $"[GameRL] instance={InstanceId()} socket={socketPath} " +
                    $"log_dir={InstanceLogDir()}");
                Log.Message($"[GameRL] Starting IPC server on {socketPath}...");

                _bridge = new RimWorldBridge();

                // Wire up event handlers
                _bridge.OnRegisterAgent += HandleRegisterAgent;
                _bridge.OnDeregisterAgent += HandleDeregisterAgent;
                _bridge.OnExecuteAction += HandleExecuteAction;
                _bridge.OnConfigureStreams += HandleConfigureStreams;
                _bridge.OnReset += HandleReset;
                _bridge.OnGetStateHash += HandleGetStateHash;
                _bridge.OnShutdown += HandleShutdown;
                _bridge.OnClientConnected += HandleClientConnected;

                if (_bridge.Listen(socketPath))
                {
                    Log.Message("[GameRL] IPC server started, waiting for harmony-bridge connection...");
                }
                else
                {
                    Log.Warning("[GameRL] Could not start IPC server. Running in standalone mode.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Initialization failed: {ex}");
            }
        }

        private static string GetSocketPath()
        {
            // Explicit override wins, but it is still per-instance by contract:
            // the launcher is responsible for making it unique.
            var envPath = Environment.GetEnvironmentVariable("GAMERL_SOCKET");
            if (!string.IsNullOrEmpty(envPath))
                return envPath;

            return Path.Combine(Path.GetTempPath(), $"gamerl-rimworld-{InstanceId()}.sock");
        }

        private static string GetCameraShmPath(string streamId)
        {
            var envPath = Environment.GetEnvironmentVariable("GAMERL_CAMERA_SHM");
            if (!string.IsNullOrEmpty(envPath))
                return envPath;

            return Path.Combine(
                Path.GetTempPath(),
                $"gamerl-rimworld-camera-{InstanceId()}-{streamId}.shm");
        }

        /// <summary>
        /// Identity of this game process.
        ///
        /// Required, with no fallback. A shared socket or framebuffer path does
        /// not fail loudly when two colonies run side by side: the second
        /// instance attaches to the first one's resources and the corruption
        /// only shows up later as unexplainable divergence.
        /// </summary>
        internal static string InstanceId()
        {
            var id = Environment.GetEnvironmentVariable("GAMERL_INSTANCE_ID");
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(
                    "GAMERL_INSTANCE_ID is not set. game-rl does not fall back to a shared " +
                    "path. Export GAMERL_INSTANCE_ID to a unique value for each RimWorld " +
                    "process (letters, digits, '-' and '_' only).");

            if (id.Length > 64 || id.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                throw new InvalidOperationException(
                    $"GAMERL_INSTANCE_ID '{id}' is not a valid instance id: at most 64 " +
                    "characters from letters, digits, '-' and '_'.");

            return id;
        }

        /// <summary>
        /// Per-instance directory for this mod's own log. RimWorld's Player.log is
        /// controlled by Unity and cannot be redirected from a mod: launch the game
        /// with -logFile to isolate that one.
        /// </summary>
        internal static string InstanceLogDir()
        {
            var dir = Environment.GetEnvironmentVariable("GAMERL_LOG_DIR");
            if (!string.IsNullOrEmpty(dir))
                return dir;

            return Path.Combine(Path.GetTempPath(), "gamerl-logs", InstanceId());
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Message Handlers
        // ═══════════════════════════════════════════════════════════════════════════

        private static void HandleClientConnected()
        {
            Log.Message("[GameRL] Rust harmony-bridge connected, sending Ready...");

            // Send Ready message when client connects
            _bridge?.SendReady(
                name: "RimWorld",
                version: VersionControl.CurrentVersionString,
                capabilities: new GameCapabilities
                {
                    MultiAgent = true,
                    MaxAgents = 8,
                    Deterministic = true,
                    Headless = false
                });

            Log.Message("[GameRL] Ready message sent");
        }

        private static void HandleRegisterAgent(RegisterAgentMessage msg)
        {
            Log.Message($"[GameRL] Registering agent: {msg.AgentId} ({msg.AgentType})");

            try
            {
                var config = new Dictionary<string, object>();
                if (msg.Config.EntityId != null)
                    config["entity_id"] = msg.Config.EntityId;
                config["observation_profile"] = msg.Config.ObservationProfile;

                if (_commandExecutor!.RegisterAgent(msg.AgentId, msg.AgentType, config))
                {
                    _agents[msg.AgentId] = new AgentState { AgentType = msg.AgentType };

                    _bridge!.SendAgentRegistered(
                        msg.AgentId,
                        _stateExtractor!.GetObservationSpace(msg.AgentType),
                        _stateExtractor!.GetActionSpace(msg.AgentType));
                }
                else
                {
                    _bridge!.SendError(-32000, $"Failed to register agent: {msg.AgentId}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Register error: {ex}");
                _bridge?.SendError(-32603, ex.Message);
            }
        }

        private static void HandleDeregisterAgent(DeregisterAgentMessage msg)
        {
            Log.Message($"[GameRL] Deregistering agent: {msg.AgentId}");
            _commandExecutor?.DeregisterAgent(msg.AgentId);
            _agents.Remove(msg.AgentId);
        }

        private static void HandleExecuteAction(ExecuteActionMessage msg)
        {
            if (_stepInProgress)
            {
                Log.Warning("[GameRL] Step already in progress, ignoring action");
                return;
            }

            _currentStepId++;
            _currentAgentId = msg.AgentId;
            _ticksRemaining = msg.Ticks > 0 ? msg.Ticks : 1;
            _stepInProgress = true;
            _tickMs = 0;
            _executeMs = 0;
            _captureMs = 0;
            _stepWatch.Restart();

            try
            {
                var execWatch = Stopwatch.StartNew();
                _commandExecutor?.ExecuteAction(msg.AgentId, msg.Action!);
                _executeMs = execWatch.Elapsed.TotalMilliseconds;

                // Force ticks to advance immediately
                ForceTicks(_ticksRemaining);
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Action error: {ex}");
                _stepInProgress = false;
                _bridge?.SendError(-32001, ex.Message);
            }
        }

        private static void ForceTicks(uint count)
        {
            if (_forcingTicks) return;  // Prevent recursion

            var tickManager = Find.TickManager;
            if (tickManager == null)
            {
                Log.Warning("[GameRL] No TickManager, cannot force ticks");
                CompleteStep();
                return;
            }

            _forcingTicks = true;
            var watch = Stopwatch.StartNew();
            try
            {
                // Force the game to advance by calling DoSingleTick directly
                // This will trigger OnTick via the Harmony patch, which decrements _ticksRemaining
                for (uint i = 0; i < count && _stepInProgress; i++)
                {
                    tickManager.DoSingleTick();
                }
            }
            finally
            {
                _tickMs = watch.Elapsed.TotalMilliseconds;
                _forcingTicks = false;
            }
        }

        private static void HandleReset(ResetMessage msg)
        {
            Log.Message($"[GameRL] Reset requested (seed: {msg.Seed}, scenario: {msg.Scenario})");

            try
            {
                _commandExecutor?.Reset(msg.Seed, msg.Scenario);

                // Track episode start for observations
                _stateExtractor!.EpisodeStartTick = Find.TickManager?.TicksGame ?? 0;

                // Reset always returns full observation (first observation after reset)
                object observation;
                if (_agents.Count == 0)
                {
                    observation = ExtractObservationForAgent("default");
                }
                else if (_agents.Count == 1)
                {
                    var enumerator = _agents.Keys.GetEnumerator();
                    enumerator.MoveNext();
                    observation = ExtractObservationForAgent(enumerator.Current);
                }
                else
                {
                    var observations = new Dictionary<string, object>();
                    foreach (var agentId in _agents.Keys)
                    {
                        observations[agentId] = ExtractObservationForAgent(agentId);
                    }
                    observation = observations;
                }
                var stateHash = _stateExtractor!.ComputeStateHash();
                _bridge?.SendResetComplete(observation, stateHash);
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Reset error: {ex}");
                _bridge?.SendError(-32603, ex.Message);
            }
        }

        private static void HandleGetStateHash(GetStateHashMessage msg)
        {
            var hash = _stateExtractor?.ComputeStateHash() ?? "sha256:0000000000000000000000000000000000000000000000000000000000000000";
            _bridge?.SendStateHash(hash);
        }

        private static void HandleConfigureStreams(ConfigureStreamsMessage msg)
        {
            Log.Message($"[GameRL] Configure streams for agent: {msg.AgentId} ({msg.Profile})");

            try
            {
                var profile = string.IsNullOrEmpty(msg.Profile) ? "default" : msg.Profile;
                var stream = GetOrCreateVisionStream(profile);
                var descriptors = new List<Dictionary<string, object>>
                {
                    stream.BuildDescriptor()
                };

                _bridge?.SendStreamsConfigured(msg.AgentId, descriptors);
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Configure streams error: {ex}");
                _bridge?.SendError(-32603, ex.Message);
            }
        }

        private static void HandleShutdown()
        {
            Log.Message("[GameRL] Shutdown requested");
            foreach (var stream in _visionStreams.Values)
            {
                stream.Dispose();
            }
            _visionStreams.Clear();
            _bridge?.Dispose();
            _bridge = null;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Game Loop Integration
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called by TickPatch after each game tick
        /// </summary>
        internal static void OnTick()
        {
            // Process any pending commands from server
            _bridge?.ProcessCommands();

            // If we're in a step, decrement counter
            if (_stepInProgress && _ticksRemaining > 0)
            {
                _ticksRemaining--;

                if (_ticksRemaining == 0)
                {
                    CompleteStep();
                }
            }

            // Push events periodically (only when not in a step, to avoid interference)
            if (!_stepInProgress && _stateExtractor != null && _bridge != null)
            {
                _ticksSinceLastEventPush++;
                if (_ticksSinceLastEventPush >= EventPushIntervalTicks)
                {
                    PushPendingEvents();
                    _ticksSinceLastEventPush = 0;
                }
            }
        }

        /// <summary>
        /// Push any pending game events to the connected client
        /// </summary>
        private static void PushPendingEvents()
        {
            if (_stateExtractor == null || _bridge == null) return;

            var events = _stateExtractor.CollectEvents();
            if (events.Count == 0) return;

            var tick = (ulong)(Find.TickManager?.TicksGame ?? 0);

            // Build minimal state snapshot for events
            var state = new Dictionary<string, object>
            {
                ["tick"] = tick,
                ["colony_alive"] = Find.CurrentMap?.mapPawns?.FreeColonistsCount > 0
            };

            _bridge.SendStateUpdate(tick, state, events);
            Log.Message($"[GameRL] Pushed {events.Count} events at tick {tick}");
        }

        private static void CompleteStep()
        {
            _stepInProgress = false;

            if (_currentAgentId == null || _stateExtractor == null || _commandExecutor == null)
                return;

            try
            {
                var (done, truncated, reason) = _commandExecutor.CheckTermination();
                var captureWatch = Stopwatch.StartNew();
                foreach (var stream in _visionStreams.Values)
                {
                    stream.Capture();
                }
                _captureMs = captureWatch.Elapsed.TotalMilliseconds;

                // Pass action result to state extractor for observation
                _stateExtractor.LastActionResult = _commandExecutor.LastActionResult;

                var hashWatch = Stopwatch.StartNew();
                var stateHash = _stateExtractor!.ComputeStateHash();
                var hashMs = hashWatch.Elapsed.TotalMilliseconds;

                if (_agents.Count <= 1)
                {
                    var observation = ExtractTimed(_currentAgentId ?? "default", out var extractMs);
                    var rewardComponents = ComputeRewardTimed(_currentAgentId ?? "default", out var rewardMs);
                    var totalReward = _commandExecutor.GetTotalReward(_currentAgentId ?? "default");

                    _bridge?.SendStepResult(
                        _currentAgentId ?? "default",
                        observation,
                        totalReward,
                        rewardComponents,
                        done,
                        truncated,
                        stateHash,
                        BuildMetrics(extractMs + hashMs, rewardMs));
                    return;
                }

                var results = new List<StepResultMessage>();
                foreach (var agentId in _agents.Keys)
                {
                    var observation = ExtractTimed(agentId, out var extractMs);
                    var rewardComponents = ComputeRewardTimed(agentId, out var rewardMs);
                    var totalReward = _commandExecutor.GetTotalReward(agentId);

                    results.Add(new StepResultMessage
                    {
                        AgentId = agentId,
                        Observation = observation,
                        Reward = totalReward,
                        RewardComponents = rewardComponents,
                        Done = done,
                        Truncated = truncated,
                        StateHash = stateHash,
                        Metrics = BuildMetrics(extractMs + hashMs, rewardMs)
                    });
                }

                _bridge?.SendBatchStepResult(results);
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Step complete error: {ex}");
                _bridge?.SendError(-32603, ex.Message);
            }
        }

        private static object ExtractTimed(string agentId, out double extractMs)
        {
            var watch = Stopwatch.StartNew();
            var obs = ExtractObservationForAgent(agentId);
            extractMs = watch.Elapsed.TotalMilliseconds;
            return obs;
        }

        private static Dictionary<string, double> ComputeRewardTimed(string agentId, out double rewardMs)
        {
            var watch = Stopwatch.StartNew();
            var components = _commandExecutor!.ComputeReward(agentId);
            rewardMs = watch.Elapsed.TotalMilliseconds;
            return components;
        }

        private static StepMetricsMessage? BuildMetrics(double extractMs, double rewardMs)
        {
            if (_stepWatch.Elapsed.TotalMilliseconds <= 0)
                return null;

            return new StepMetricsMessage
            {
                StepMs = _stepWatch.Elapsed.TotalMilliseconds,
                TickMs = _tickMs,
                ExecuteMs = _executeMs,
                ExtractMs = extractMs,
                RewardMs = rewardMs,
                CaptureMs = _captureMs
            };
        }

        /// <summary>
        /// Track agent state
        /// </summary>
        private class AgentState
        {
            public string AgentType { get; set; } = "";
        }

        /// <summary>
        /// Extract observation for an agent using its configured observation mode.
        /// Uses delta observations when appropriate.
        /// </summary>
        private static object ExtractObservationForAgent(string agentId)
        {
            var mode = _commandExecutor?.GetObservationMode(agentId) ?? ObservationMode.Minimal;
            var isFirst = _commandExecutor?.IsFirstObservation(agentId) ?? true;
            var previousHash = _commandExecutor?.GetLastStateHash(agentId);
            var config = _commandExecutor?.GetDeltaConfig(agentId) ?? DeltaConfig.Minimal;

            // Check if RequestFullState was called - treat as first observation
            var forceFullState = _commandExecutor?.ForceFullState ?? false;
            if (forceFullState)
            {
                isFirst = true;
                _commandExecutor!.ForceFullState = false;  // Reset flag
            }

            var obs = _stateExtractor!.ExtractForAgent(agentId, mode, isFirst, previousHash, config);

            // Update state tracking after extraction
            if (isFirst)
            {
                _commandExecutor?.MarkFirstObservationSent(agentId);
            }

            // Get state hash from the observation and update the executor
            string? stateHash = null;
            if (obs is RimWorldObservation fullObs)
            {
                stateHash = _stateExtractor.ComputeStateHash();
            }
            else if (obs is DeltaObservation deltaObs)
            {
                stateHash = deltaObs.StateHash;
            }

            if (stateHash != null)
            {
                _commandExecutor?.SetLastStateHash(agentId, stateHash);
            }

            return obs;
        }

        private static VisionStreamManager GetOrCreateVisionStream(string profile)
        {
            if (_visionStreams.TryGetValue(profile, out var stream))
            {
                return stream;
            }

            var (width, height) = ParseVisionProfile(profile);
            var streamId = $"camera_{profile}";
            var shmPath = GetCameraShmPath(streamId);
            stream = new VisionStreamManager(streamId, width, height, shmPath);
            _visionStreams[profile] = stream;
            return stream;
        }

        /// <summary>
        /// Parse vision profile string (e.g., "256x256", "512x512").
        /// Falls back to 256x256 for "default" or invalid profiles.
        /// </summary>
        private static (int width, int height) ParseVisionProfile(string profile)
        {
            var parts = profile.Split('x');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var width)
                && int.TryParse(parts[1], out var height)
                && width > 0
                && height > 0)
            {
                return (width, height);
            }

            // Default resolution for named profiles like "default"
            return (256, 256);
        }
    }

    /// <summary>
    /// RimWorld-specific bridge with proper logging
    /// </summary>
    internal class RimWorldBridge : Bridge
    {
        protected override void Log(string message)
        {
            Verse.Log.Message($"[GameRL] {message}");
        }

        protected override void LogError(string message)
        {
            Verse.Log.Error($"[GameRL] {message}");
        }
    }
}
