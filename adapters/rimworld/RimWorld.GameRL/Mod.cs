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
using RimWorld.GameRL.Rewards;

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

        // Which pump is draining the command queue, if any. Two pumps run in the
        // same frame — the tick postfix fires inside Root_Play.Update, the frame
        // postfix after it returns — and a command handler can re-enter the pump
        // (an action handler calls ForceTicks, which ticks, which pumps). Both
        // cases would run a queued command twice, so a re-entrant pump is
        // refused and says which pump holds the queue.
        private static bool _commandPumpActive;
        private static string? _activeCommandPump;

        // Per-step phase timings. A slow simulation and a slow bridge look the
        // same from outside the process and call for opposite responses, so the
        // split is reported rather than just the total.
        private static readonly Stopwatch _stepWatch = new();
        /// <summary>
        /// Static because CompleteStep runs from inside the tick loop, via the OnTick
        /// patch, before ForceTicks' finally block would have assigned a value.
        /// </summary>
        private static readonly Stopwatch _tickWatch = new();
        private static double _tickMs;
        private static double _executeMs;
        private static double _captureMs;
        private static string? _stateHashBeforeAction;

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
                _commandExecutor = new RimWorldCommandExecutor(_stateExtractor);

                // Start listening for harmony-server connection
                Environment.SetEnvironmentVariable("GAMERL_ENDPOINT_FILE", EndpointFilePath());

                var endpoint = GetListenEndpoint();
                Log.Message(
                    $"[GameRL] instance={InstanceId()} endpoint={endpoint} " +
                    $"log_dir={InstanceLogDir()}");
                Log.Message($"[GameRL] Starting IPC server on {endpoint}...");

                _bridge = new RimWorldBridge();

                // Wire up event handlers
                _bridge.OnRegisterAgent += HandleRegisterAgent;
                _bridge.OnDeregisterAgent += HandleDeregisterAgent;
                _bridge.OnExecuteAction += HandleExecuteAction;
                _bridge.OnConfigureStreams += HandleConfigureStreams;
                _bridge.OnReset += HandleReset;
                _bridge.OnGetStateHash += HandleGetStateHash;
                _bridge.OnListActions += HandleListActions;
                _bridge.OnShutdown += HandleShutdown;
                _bridge.OnClientConnected += HandleClientConnected;

                if (_bridge.Listen(endpoint))
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

        /// <summary>
        /// Where the bridge should listen.
        ///
        /// Order matters. GAMERL_TCP wins because it is the only transport that
        /// works on Wine and Proton: .NET Framework exposes no AF_UNIX support,
        /// and Wine answers WSAEAFNOSUPPORT for the family, so the Unix path
        /// cannot bind there at all.
        ///
        /// Still no derived default. A shared endpoint would let two colonies
        /// attach to each other's bridge, which is silent corruption rather than
        /// an error.
        /// </summary>
        private static string GetListenEndpoint()
        {
            var tcp = Environment.GetEnvironmentVariable("GAMERL_TCP");
            if (!string.IsNullOrEmpty(tcp))
                return tcp.Trim();

            var unix = Environment.GetEnvironmentVariable("GAMERL_SOCKET");
            if (!string.IsNullOrEmpty(unix))
                return unix.Trim();

            throw new InvalidOperationException(
                "Neither GAMERL_TCP nor GAMERL_SOCKET is set. game-rl does not fall back to a " +
                "shared endpoint. Set GAMERL_TCP=127.0.0.1:<port> (required on Wine/Proton, " +
                "where AF_UNIX is unavailable), or GAMERL_SOCKET to an absolute path on a " +
                "runtime that supports Unix sockets. Port 0 asks the OS for a free port, " +
                "which is then published to GAMERL_ENDPOINT_FILE.");
        }

        /// <summary>
        /// Per-instance endpoint file, so a client can discover an ephemeral port.
        /// </summary>
        internal static string EndpointFilePath()
        {
            var envPath = Environment.GetEnvironmentVariable("GAMERL_ENDPOINT_FILE");
            if (!string.IsNullOrEmpty(envPath))
                return envPath;

            return Path.Combine(InstanceLogDir(), "endpoint");
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

                // Forwarded because the adapter defaults to delta observations,
                // which leaves a client deciding one step at a time with no state.
                if (!string.IsNullOrEmpty(msg.Config.ObservationMode))
                    config["ObservationMode"] = msg.Config.ObservationMode;

                // Extra carries adapter-specific keys such as DeltaConfig.
                if (msg.Config.Extra != null)
                {
                    foreach (var kv in msg.Config.Extra)
                        config[kv.Key] = kv.Value;
                }

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

                // Did the action actually change anything? Measured by the
                // environment rather than reported by the action, so this works
                // for all actions without each one having to describe itself.
                // Without it an action that is well-formed but has no effect
                // looks identical to one that worked.
                _stateHashBeforeAction = _stateExtractor?.ComputeStateHash();

                // Force ticks to advance immediately
                ForceTicks(_ticksRemaining);

                var hashAfter = _stateExtractor?.ComputeStateHash();
                var actionResult = _commandExecutor?.LastActionResult;
                if (actionResult != null && _stateHashBeforeAction != null && hashAfter != null)
                {
                    actionResult.Effect = hashAfter != _stateHashBeforeAction
                        ? "Changed"
                        : "NoEffect";
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Action error: {ex}");
                _stepInProgress = false;
                _bridge?.SendError(-32001, ex.Message);
            }
        }

        /// <summary>
        /// Describe why the game cannot be driven right now, naming the state that
        /// actually blocks it.
        /// </summary>
        /// <remarks>
        /// Every probe here is itself guarded: the whole point is that probing game
        /// state is what threw in the first place, so a diagnostic that can throw is
        /// not a diagnostic. Anything unreachable is reported as "unavailable" rather
        /// than being allowed to take the process down.
        /// </remarks>
        private static string DescribeGameState(Exception? cause)
        {
            string probe(Func<string> read, string label)
            {
                try
                {
                    return $"{label}={read()}";
                }
                catch (Exception ex)
                {
                    return $"{label}=<{ex.GetType().Name}>";
                }
            }

            var parts = new List<string>
            {
                probe(() => Current.ProgramState.ToString(), "ProgramState"),
                probe(() => Find.CurrentMap?.ToString() ?? "null", "CurrentMap"),
                probe(() => LongEventHandler.ShouldWaitForEvent.ToString(), "LongEventBusy"),
                probe(() => Find.TickManager?.TicksGame.ToString() ?? "null", "TickManager"),
            };
            if (cause != null)
            {
                parts.Add($"probe threw {cause.GetType().Name}: {cause.Message}");
            }
            return string.Join(", ", parts);
        }

        private static void ForceTicks(uint count)
        {
            if (_forcingTicks) return;  // Prevent recursion

            // `Verse.Find.TickManager` dereferences the current map *internally* and
            // throws NullReferenceException when there is no map -- main menu, or a
            // save still loading. So the null check below cannot be reached: the
            // property access itself is what blows up. Reading it defensively is the
            // difference between a diagnosable refusal and a NullReferenceException
            // that names nothing.
            TickManager? tickManager;
            try
            {
                tickManager = Find.TickManager;
            }
            catch (Exception ex)
            {
                Log.Warning($"[GameRL] Cannot force {count} ticks: {DescribeGameState(ex)}");
                CompleteStep();
                return;
            }

            if (tickManager == null)
            {
                Log.Warning($"[GameRL] Cannot force {count} ticks: {DescribeGameState(null)}");
                CompleteStep();
                return;
            }

            _forcingTicks = true;
            _tickWatch.Restart();
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
                _tickMs = _tickWatch.Elapsed.TotalMilliseconds;
                _forcingTicks = false;
            }
        }

        /// <summary>
        /// Publish the real action contract.
        ///
        /// The manifest lists 22 actions while the adapter registers more, and a
        /// misspelled parameter used to produce a successful no-op. A client that
        /// has to guess is a client that learns from false positives.
        /// </summary>
        private static void HandleListActions()
        {
            var actions = _commandExecutor?.DescribeActions() ?? new List<Dictionary<string, object>>();
            Log.Message($"[GameRL] Listing {actions.Count} actions");
            _bridge?.SendActionsListed(actions);
        }

        private static void HandleReset(ResetMessage msg)
        {
            Log.Message($"[GameRL] Reset requested (seed: {msg.Seed}, scenario: {msg.Scenario})");

            try
            {
                // Throws on an unknown mode rather than defaulting: a reset
                // quietly downgraded to "load the template again" is the failure
                // this whole path exists to make impossible.
                var mode = ResetModes.Parse(msg.Mode);

                var outcome = _commandExecutor!.Reset(msg.Seed, msg.Scenario, mode);

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
                var stateHash = _stateExtractor.ComputeStateHash();

                var postconditions = BuildResetPostconditions(outcome, observation, stateHash);
                foreach (var postcondition in postconditions)
                {
                    // Reported as an error, not as a ResetComplete with Ok:false.
                    // A reset whose postconditions failed has not happened, and
                    // sending the success type would say it did.
                    if (!postcondition.Ok)
                    {
                        Log.Error(
                            $"[GameRL] Reset postcondition '{postcondition.Name}' failed: " +
                            postcondition.Observed);
                        _bridge?.SendError(-32603,
                            $"reset postcondition '{postcondition.Name}' failed: observed " +
                            postcondition.Observed);
                        return;
                    }
                }

                _bridge?.SendResetComplete(
                    observation,
                    stateHash,
                    ResetModes.ToWireString(outcome.RequestedMode),
                    outcome.Seed,
                    outcome.SeedConsumed,
                    postconditions);
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Reset error: {ex}");
                _bridge?.SendError(-32603, ex.Message);
            }
        }

        /// <summary>
        /// The five reset checks, in the order the wire contract fixes them.
        ///
        /// This list is the contract: nothing may be added, removed or renamed.
        /// Every Ok is derived from a value read back out of the game — the map
        /// and the roster from after the reset, the observation from what the
        /// extractor actually returned, the digest from whether one exists. A
        /// check that reports what it intended to do is worse than no check.
        /// </summary>
        private static List<ResetPostcondition> BuildResetPostconditions(
            ResetOutcome outcome,
            object observation,
            string? stateHash)
        {
            bool observationOk = ObservationExtracted(observation, out var observationObserved);

            return new List<ResetPostcondition>
            {
                new ResetPostcondition
                {
                    Name = "map",
                    Ok = outcome.HasMap,
                    Observed = outcome.MapObserved
                },
                new ResetPostcondition
                {
                    Name = "colonists",
                    Ok = outcome.ColonistCount > 0,
                    Observed = $"colonists {outcome.ColonistCount}"
                },
                new ResetPostcondition
                {
                    Name = "seed_consumed",
                    Ok = outcome.SeedConsumed,
                    Observed = outcome.SeedObserved
                },
                new ResetPostcondition
                {
                    Name = "observation",
                    Ok = observationOk,
                    Observed = observationObserved
                },
                new ResetPostcondition
                {
                    Name = "state_hash",
                    Ok = stateHash != null,
                    Observed = stateHash ?? "no digest produced"
                }
            };
        }

        /// <summary>
        /// Read back what the extractor returned, rather than assuming it worked.
        ///
        /// ExtractForAgent ends in a cast to RimWorldObservation followed by a
        /// null-forgiving operator, so "nothing was extracted" is a reachable
        /// state and not a type error.
        /// </summary>
        private static bool ObservationExtracted(object? observation, out string observed)
        {
            if (observation == null)
            {
                observed = "no observation returned";
                return false;
            }

            if (observation is Dictionary<string, object> perAgent)
            {
                var described = new List<string>();
                var allExtracted = true;
                foreach (var entry in perAgent)
                {
                    if (ObservationExtracted(entry.Value, out var oneObserved))
                    {
                        described.Add($"{entry.Key}={oneObserved}");
                    }
                    else
                    {
                        described.Add($"{entry.Key}={oneObserved}");
                        allExtracted = false;
                    }
                }
                observed = string.Join(", ", described);
                return allExtracted;
            }

            if (observation is DeltaObservation delta)
            {
                if (!string.IsNullOrEmpty(delta.StateHash))
                {
                    observed = $"DeltaObservation(hash={delta.StateHash})";
                    return true;
                }

                observed = "DeltaObservation without a state hash";
                return false;
            }

            if (observation is RimWorldObservation full)
            {
                observed = $"RimWorldObservation(colonists={full.ColonistCount}, tick={full.Tick})";
                return true;
            }

            observed = $"{observation.GetType().Name}, which is not an observation";
            return false;
        }

        private static void HandleGetStateHash(GetStateHashMessage msg)
        {
            // "none" rather than a zero digest with a "sha256:" prefix: a
            // fabricated hash is a state that never existed, and the Rust side
            // types this field as a plain String, so it cannot be null.
            var hash = _stateExtractor?.ComputeStateHash() ?? "none";
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
            PumpCommands("tick");

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
        /// Called once per frame by FramePumpPatch.
        ///
        /// Exists because the tick pump cannot run before a game is ticking: at
        /// the main menu there is no TickManager, so <c>reset</c> and
        /// <c>get_state_hash</c> sat in the queue until a game was loaded. A
        /// reset that is reachable is also one that can fail honestly at the main
        /// menu, which is where a wrong configuration is cheapest to find.
        /// </summary>
        /// <param name="pump">Which postfix is calling, logged with every command.</param>
        internal static void OnFrame(string pump)
        {
            PumpCommands(pump);
        }

        /// <summary>
        /// Drain the command queue, once, from whichever pump got here first.
        ///
        /// The name travels into ProcessCommands so the log says which path
        /// handled a command. Without it, "reset worked" and "reset worked
        /// because the game happened to be ticking" look identical from outside
        /// the process, and the second is the interesting one.
        /// </summary>
        /// <summary>
        /// A pump collision in progress, and how long it has been running.
        /// A collision spans frames, so it is warned about once and totalled at
        /// the end rather than logged per frame.
        /// </summary>
        private static bool _pumpCollisionActive;
        private static int _pumpCollisionCount;

        private static void PumpCommands(string pump)
        {
            var bridge = _bridge;
            if (bridge == null) return;

            if (_commandPumpActive)
            {
                // Silent when it is the same pump re-entering: that is the
                // bounded, expected case — an action handler calls ForceTicks,
                // which ticks, which pumps once per forced tick. Those commands
                // are picked up by the outer drain on its next iteration, so
                // nothing is lost, but logging it would put one line per forced
                // tick into the log of every step.
                //
                // The two-pumps-collide case is warned about, because that is
                // where the frame makes it look like a tick and nobody can tell
                // which one ran. But a collision is not an event, it is a
                // *duration*: a reset drains from frame:root_play while the game
                // ticks, so the tick pump collides on every single tick for the
                // whole reset. One warning per tick meant 15 identical lines per
                // reset here, which is how a log teaches you to skip it.
                //
                // So: one line when the collision starts, one when it ends with
                // the count in between. The onset is what you read live; the
                // total is what tells you it was structural rather than a blip.
                if (!string.Equals(pump, _activeCommandPump, StringComparison.Ordinal))
                {
                    if (!_pumpCollisionActive)
                    {
                        _pumpCollisionActive = true;
                        _pumpCollisionCount = 0;
                        Log.Warning(
                            $"[GameRL] Command pump collision: '{pump}' is being skipped " +
                            $"while '{_activeCommandPump ?? "unknown"}' drains the queue in the " +
                            "same frame. A second pass would run a queued command twice. " +
                            "Warning once per collision; the total is reported when it ends.");
                    }

                    _pumpCollisionCount++;
                }

                return;
            }

            _commandPumpActive = true;
            _activeCommandPump = pump;
            try
            {
                bridge.ProcessCommands(pump);
            }
            finally
            {
                _activeCommandPump = null;
                _commandPumpActive = false;

                if (_pumpCollisionActive)
                {
                    Log.Message(
                        $"[GameRL] Command pump collision ended: " +
                        $"{_pumpCollisionCount} frame(s) skipped. No command was lost; the " +
                        "outer drain picks up anything the inner one did not reach.");
                    _pumpCollisionActive = false;
                    _pumpCollisionCount = 0;
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

                // Once per step, for the colony. The reward is a property of the
                // colony and the manifest declares one agent, so there is one
                // measurement however many agents are registered — and it has to
                // be once, because the reward function keeps a delta baseline
                // that the first call advances.
                var rewardWatch = Stopwatch.StartNew();
                var reward = _commandExecutor.ComputeRewardFrame();
                var rewardMs = rewardWatch.Elapsed.TotalMilliseconds;

                if (_agents.Count <= 1)
                {
                    var observation = ExtractTimed(_currentAgentId ?? "default", out var extractMs);

                    _bridge?.SendStepResult(
                        _currentAgentId ?? "default",
                        observation,
                        reward.Total,
                        reward.CopyComponents(),
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

                    results.Add(BuildStepResult(
                        agentId,
                        observation,
                        reward,
                        done,
                        truncated,
                        stateHash,
                        BuildMetrics(extractMs + hashMs, rewardMs)));
                }

                _bridge?.SendBatchStepResult(results);
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Step complete error: {ex}");
                _bridge?.SendError(-32603, ex.Message);
            }
        }

        /// <summary>
        /// One agent's step message, with the reward taken from a single frame.
        ///
        /// Both reward fields are written from one argument, so they cannot be
        /// filled from two different computations. This is the invariant the
        /// harness checks every step (Reward == sum(RewardComponents)); it is
        /// meant to be impossible to break here rather than checked after the
        /// fact.
        ///
        /// The single-agent path sends through Bridge.SendStepResult, which
        /// takes the two reward numbers as arguments rather than a message, and
        /// Bridge's Send is private so this cannot be used for it. That path
        /// reads the same frame local, one line apart.
        /// </summary>
        private static StepResultMessage BuildStepResult(
            string agentId,
            object observation,
            RewardFrame reward,
            bool done,
            bool truncated,
            string? stateHash,
            StepMetricsMessage? metrics)
        {
            return new StepResultMessage
            {
                AgentId = agentId,
                Observation = observation,
                Reward = reward.Total,
                RewardComponents = reward.CopyComponents(),
                Done = done,
                Truncated = truncated,
                StateHash = stateHash,
                Metrics = metrics
            };
        }

        private static object ExtractTimed(string agentId, out double extractMs)
        {
            var watch = Stopwatch.StartNew();
            var obs = ExtractObservationForAgent(agentId);
            extractMs = watch.Elapsed.TotalMilliseconds;
            return obs;
        }

        private static StepMetricsMessage? BuildMetrics(double extractMs, double rewardMs)
        {
            if (_stepWatch.Elapsed.TotalMilliseconds <= 0)
                return null;

            return new StepMetricsMessage
            {
                StepMs = _stepWatch.Elapsed.TotalMilliseconds,
                TickMs = _tickMs > 0 ? _tickMs : _tickWatch.Elapsed.TotalMilliseconds,
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
