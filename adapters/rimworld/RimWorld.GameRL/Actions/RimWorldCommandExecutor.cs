// Command executor for RimWorld actions

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Verse;
using Verse.AI;
using RimWorld;
using GameRL.Harmony;
using GameRL.Harmony.RPC;
using RimWorld.GameRL.Patches;
using RimWorld.GameRL.Rewards;
using RimWorld.GameRL.State;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Executes commands in RimWorld using HarmonyRPC
    /// </summary>
    public class RimWorldCommandExecutor : ICommandExecutor
    {
        private readonly Dictionary<string, AgentInfo> _agents = new();

        /// <summary>
        /// The reward function. Stateless, and shared only because it holds
        /// nothing: it is a function from two snapshots to a frame, so there is
        /// no per-agent or per-colony state in it to cross over.
        ///
        /// What is per-episode lives here instead, in the executor:
        /// <see cref="_rewardBaseline"/> is the reading the next delta is taken
        /// against, and <see cref="_episodeRewardTotal"/> is what the colony has
        /// earned since the episode began. Both are cleared by
        /// <see cref="ClearEpisodeState"/>.
        /// </summary>
        private readonly SurvivalReward _rewardCalculator = new();

        /// <summary>
        /// The previous step's reading of the colony, or null before the first
        /// step of an episode.
        ///
        /// This is the accumulator the reward function used to keep in its own
        /// `_last*` fields. It is the reason a second call to Compute in the same
        /// step returned a different number: the first call had already moved it.
        /// </summary>
        private RewardSnapshot? _rewardBaseline;

        /// <summary>Reward accumulated since the episode began.</summary>
        private double _episodeRewardTotal;

        /// <summary>
        /// The frame from the most recent <see cref="ComputeRewardFrame"/>.
        ///
        /// Kept so the interface's scalar accessors answer from the same object
        /// the wire was given rather than recomputing anything. Null before the
        /// first step, which is when those accessors refuse rather than guess.
        /// </summary>
        private RewardFrame? _lastRewardFrame;

        private readonly RimWorldStateExtractor _stateExtractor;
        private readonly HarmonyRPC _rpc;
        private int _episodeStartTick;
        private const int MaxEpisodeTicks = 60000 * 15;  // 15 in-game days

        /// <summary>
        /// What the colony has earned since the episode began.
        ///
        /// Sum of every frame <see cref="ComputeRewardFrame"/> has produced since
        /// the last reset, which is the episode return a trainer needs and the
        /// number that has to be zeroed at the episode boundary.
        /// </summary>
        public double EpisodeRewardTotal => _episodeRewardTotal;

        /// <summary>
        /// Last action result for RL feedback
        /// </summary>
        public ActionResult? LastActionResult { get; private set; }

        /// <summary>
        /// Force full state on next observation (set by RequestFullState action)
        /// </summary>
        public bool ForceFullState { get; set; }

        public RimWorldCommandExecutor(RimWorldStateExtractor stateExtractor)
        {
            _stateExtractor = stateExtractor;

            // Initialize HarmonyRPC with RimWorld logging
            _rpc = new HarmonyRPC(
                log: msg => Log.Message(msg),
                logError: msg => Log.Error(msg)
            );

            // Register type resolvers for automatic ID -> object conversion
            _rpc.RegisterResolver(new PawnResolver());
            _rpc.RegisterResolver(new ThingResolver());
            _rpc.RegisterResolver(new BuildingResolver());

            // Scan for [GameRLAction] methods in this assembly
            _rpc.RegisterAll(Assembly.GetExecutingAssembly());
        }

        /// <summary>
        /// Apply observation settings from a config dictionary.
        ///
        /// Shared by initial registration and re-registration so both paths
        /// interpret the config identically.
        /// </summary>
        private static void ApplyObservationConfig(AgentInfo info, Dictionary<string, object> config)
        {
            // Parse ObservationMode from config
            if (config.TryGetValue("ObservationMode", out var modeObj) && modeObj is string modeStr)
            {
                info.ObservationMode = modeStr.ToLowerInvariant() switch
                {
                    "minimal" => ObservationMode.Minimal,
                    "normal" => ObservationMode.Normal,
                    "full" => ObservationMode.Full,
                    _ => ObservationMode.Minimal
                };
            }

            // Parse DeltaConfig from config
            if (config.TryGetValue("DeltaConfig", out var deltaObj) && deltaObj is Dictionary<string, object> deltaDict)
            {
                var deltaConfig = info.ObservationMode == ObservationMode.Minimal
                    ? DeltaConfig.Minimal
                    : DeltaConfig.Normal;

                if (deltaDict.TryGetValue("MoodThreshold", out var mood))
                    deltaConfig.MoodThreshold = Convert.ToSingle(mood);
                if (deltaDict.TryGetValue("HealthThreshold", out var health))
                    deltaConfig.HealthThreshold = Convert.ToSingle(health);
                if (deltaDict.TryGetValue("HungerThreshold", out var hunger))
                    deltaConfig.HungerThreshold = Convert.ToSingle(hunger);
                if (deltaDict.TryGetValue("RestThreshold", out var rest))
                    deltaConfig.RestThreshold = Convert.ToSingle(rest);
                if (deltaDict.TryGetValue("PositionThreshold", out var pos))
                    deltaConfig.PositionThreshold = Convert.ToInt32(pos);
                if (deltaDict.TryGetValue("PositionOnlyOnJobChange", out var posJob))
                    deltaConfig.PositionOnlyOnJobChange = Convert.ToBoolean(posJob);
                if (deltaDict.TryGetValue("ResourcePercentThreshold", out var res))
                    deltaConfig.ResourcePercentThreshold = Convert.ToSingle(res);

                info.DeltaConfig = deltaConfig;
            }
            else
            {
                // Set default based on observation mode
                info.DeltaConfig = info.ObservationMode == ObservationMode.Minimal
                    ? DeltaConfig.Minimal
                    : DeltaConfig.Normal;
            }
        }

        /// <summary>
        /// The real action contract, from the same reflection the dispatcher uses.
        /// </summary>
        public List<Dictionary<string, object>> DescribeActions() => _rpc.DescribeActions();

        public bool RegisterAgent(string agentId, string agentType, Dictionary<string, object> config)
        {
            // Re-registration applies the new config rather than returning early.
            // Returning early locked in whatever the first caller asked for, so a
            // client could never switch observation mode without restarting the
            // game. Runtime state such as FirstObservation is deliberately kept.
            if (_agents.TryGetValue(agentId, out var existing))
            {
                Log.Message($"[GameRL] Agent already registered: {agentId} ({agentType}), applying new config");
                existing.Config = config;
                ApplyObservationConfig(existing, config);
                return true;
            }

            var info = new AgentInfo
            {
                AgentType = agentType,
                Config = config
            };

            ApplyObservationConfig(info, config);

            _agents[agentId] = info;

            Log.Message($"[GameRL] Agent registered: {agentId} ({agentType}, mode={info.ObservationMode})");
            return true;
        }

        public void DeregisterAgent(string agentId)
        {
            _agents.Remove(agentId);
            Log.Message($"[GameRL] Agent deregistered: {agentId}");
        }

        public void ExecuteAction(string agentId, object action)
        {
            if (action == null)
            {
                LastActionResult = ActionResult.NoOp();
                return;
            }

            // Parse the action dictionary
            var actionDict = action as Dictionary<string, object>;
            if (actionDict == null)
            {
                LastActionResult = ActionResult.Fail("Unknown", ActionErrorCode.InternalError,
                    $"Unknown action format: {action.GetType()}");
                Log.Warning($"[GameRL] Unknown action format: {action.GetType()}");
                return;
            }

            // Extract action type and parameters (Type is PascalCase from Rust)
            var actionType = actionDict.TryGetValue("Type", out var t) ? t?.ToString() : null;
            // Params are flattened into the action dict (not nested)
            var actionParams = actionDict;

            if (string.IsNullOrEmpty(actionType))
            {
                // No action type = no-op (wait)
                LastActionResult = ActionResult.NoOp();
                return;
            }

            // Dispatch via HarmonyRPC - automatic method resolution and parameter binding
            var result = _rpc.Dispatch(actionType!, actionParams);
            if (result.Success)
            {
                LastActionResult = ActionResult.Ok(actionType!, "Action executed successfully");
            }
            else
            {
                // Determine error code based on message
                var errorCode = result.ErrorMessage?.Contains("Unknown action") == true
                    ? ActionErrorCode.UnknownAction
                    : ActionErrorCode.InternalError;
                LastActionResult = ActionResult.Fail(actionType!, errorCode, result.ErrorMessage ?? "Action failed");
                Log.Warning($"[GameRL] {result.ErrorMessage}");
            }
        }

        /// <summary>
        /// Put a fresh world in place for a new episode.
        ///
        /// This is a state machine, not a function that assigns a field. It
        /// does not return until the world has been replaced and the seed has
        /// been applied, and it throws rather than returning in a state the
        /// caller has to second-guess. The previous version queued
        /// RngManager.PendingSeed and returned, which is why every episode was
        /// a replay of one world.
        /// </summary>
        public ResetOutcome Reset(ulong? seed, string? scenario, ResetMode mode)
        {
            Log.Message($"[GameRL] Reset requested (mode: {ResetModes.ToWireString(mode)}, seed: {seed}, scenario: {scenario})");

            return mode == ResetMode.Fresh
                ? ResetFresh(seed, scenario)
                : ResetFast(seed, scenario);
        }

        /// <summary>
        /// Mode=Fast: replace the world with the template save, then seed.
        ///
        /// Reproducible, and it cannot depend on the seed — the loaded world is
        /// the loaded world whichever seed was asked for.
        /// </summary>
        private ResetOutcome ResetFast(ulong? seed, string? scenario)
        {
            LoadResetTemplate(ResetTemplate());
            return CompleteReset(seed, ResetMode.Fast);
        }

        /// <summary>
        /// Mode=Fresh: generate a world, a map and a colonist roster from the
        /// seed, so the seed selects the world.
        ///
        /// Refuses rather than degrading to Mode=Fast. A reset that quietly
        /// fell back to the template would answer "ok" with a colony that has
        /// nothing to do with the seed, which is the one outcome the gate this
        /// mode exists to satisfy cannot be allowed to produce.
        ///
        /// A seed is required here and not in Fast: generating a colony and
        /// ignoring the seed would be Fast wearing Fresh's name.
        /// </summary>
        private ResetOutcome ResetFresh(ulong? seed, string? scenario)
        {
            if (!seed.HasValue)
            {
                throw new InvalidOperationException(
                    "Reset mode \"Fresh\" needs a seed. It generates the world from the seed, " +
                    "so without one there is nothing to generate from, and falling back to " +
                    "Mode=\"Fast\" here would report a seeded reset that never used the seed.");
            }

            FreshResetGenerator.Generate(seed.Value, scenario);
            return CompleteReset(seed, ResetMode.Fresh);
        }

        /// <summary>
        /// The part of a reset that is the same whichever mode produced the
        /// world: seed the live stream, drop per-episode state, then read the
        /// postcondition values back out of the game.
        ///
        /// Shared deliberately. Both modes fill the same ResetOutcome fields, so
        /// the five postconditions Mod.cs builds are identical in name, order and
        /// meaning, and the wire cannot fork on which mode was used.
        /// </summary>
        private ResetOutcome CompleteReset(ulong? seed, ResetMode mode)
        {
            var outcome = new ResetOutcome
            {
                RequestedMode = mode,
                Seed = seed
            };

            // Provision before anything else reports success. A colony whose
            // colonists have no work priorities is not playable, and an agent
            // handed one has nothing to decide about. The outcome is recorded on
            // the wire rather than thrown away, so `playable` is observable.
            var provisioning = ColonyProvisioning.Provision();
            outcome.Provisioning = new ResetProvisioning
            {
                Colonists = provisioning.Colonists,
                WithWork = provisioning.WithWork,
            };
            if (!provisioning.Playable)
            {
                throw new InvalidOperationException(
                    "The colony was replaced but is not playable, so the reset is not a success. " +
                    provisioning.Blocker);
            }

            if (seed.HasValue)
            {
                var applied = RngManager.ApplySeed(seed.Value);
                outcome.SeedConsumed = true;
                outcome.SeedObserved =
                    $"seed {seed.Value} written to Rand.Seed as {applied} (0x{applied:X8}); " +
                    "Rand.Seed is an int with no public getter, so this is the value written " +
                    "rather than one read back, and the high 32 bits of a 64-bit seed cannot be " +
                    "honoured by the API";
            }
            else
            {
                outcome.SeedObserved = "no seed sent, Rand left as the loaded world set it";
            }

            // Before the observation, not after: the delta snapshots the reward
            // reads are per-episode, and a generated world must not start from
            // the previous episode's numbers.
            ClearEpisodeState();

            var map = Find.CurrentMap;
            var timeSpeedObserved = "unknown";

            if (map == null)
            {
                outcome.HasMap = false;
                outcome.MapObserved = $"no current map, ProgramState={Current.ProgramState}";
                outcome.ColonistCount = 0;
                timeSpeedObserved = "no map, time speed untouched";
            }
            else
            {
                // Take the tick pacing away from the frame clock.
                //
                // Both reset paths hand us an un-paused game, and that is what
                // made two identical runs diverge. Loading a save ends in
                // Game.LoadGame, whose post-load lambda ticks once and then sets
                // CurTimeSpeed = TimeSpeed.Normal; Game.InitNewGame reaches the
                // same assignment through Scenario.PostGameStart. TickManager
                // does not persist curTimeSpeed (ExposeData saves only
                // ticksGameInt, gameStartAbsTick, startingYearInt and
                // lastSettleTicksInt), so every reset lands on Normal
                // deterministically -- which is exactly the problem.
                //
                // While it is un-paused, every frame goes Root_Play.Update ->
                // Game.UpdatePlay -> TickManagerUpdate, and TickManagerUpdate
                // decides how many ticks to run from Time.deltaTime and
                // Stopwatch.Elapsed accumulated into realTimeToTickThrough
                // (then feeds the measured tick cost back into
                // SimpleMovingAverage / ExponentialMovingAverage). ForceTicks
                // adds its own fixed count of DoSingleTick on top of whatever
                // that frame-paced path ran, so a step executed a machine- and
                // frame-timing-dependent number of ticks and the worlds drifted
                // apart despite identical seeds.
                //
                // ForcePaused is not the lever: its getter also folds in window
                // stacks, the tile picker and cutscenes. CurTimeSpeed is,
                // because TickManager.Paused is
                // `curTimeSpeed == Paused || ForcePaused`, and
                // TickManagerUpdate branches straight to its return once Paused
                // holds.
                //
                // Ticking itself is unaffected. DoSingleTick never reads Paused
                // -- it goes straight to the MapPreTick sweep, ticksGameInt++
                // and the tick lists -- so ForceTicks keeps advancing exactly
                // the number of ticks it was asked for. Only the frame-paced
                // path stops.
                //
                // Inside the map branch because Find.TickManager dereferences
                // the current map internally and throws without one.
                var tickManager = Find.TickManager;
                var wasTimeSpeed = tickManager.CurTimeSpeed;
                tickManager.CurTimeSpeed = TimeSpeed.Paused;
                timeSpeedObserved = $"{wasTimeSpeed} -> {tickManager.CurTimeSpeed}";

                // intVec3 is (x, height, z) in RimWorld, and a map's height is always 1, so
                // printing .y reports "250x1" for a normal 250x250 map. The
                // playable extent is x by z.
                var size = map.info.Size;
                outcome.HasMap = true;
                outcome.MapObserved = $"map {size.x}x{size.z}";
                outcome.ColonistCount = map.mapPawns.FreeColonistsCount;
            }

            Log.Message(
                $"[GameRL] Reset done (mode={ResetModes.ToWireString(mode)}, " +
                $"{outcome.MapObserved}, {outcome.ColonistCount} free colonists, " +
                $"{outcome.SeedObserved}, timeSpeed {timeSpeedObserved})");

            // Not a postcondition: the wire contract fixes those five, and the
            // environment contract also wants the storyteller and the RNG
            // streams checked. Reported here so a stalled game can be told
            // apart from a world that simply failed to load.
            Log.Message(
                $"[GameRL] Post-reset environment: ProgramState={Current.ProgramState}, " +
                $"Storyteller={Find.Storyteller?.def?.label ?? "none"}, " +
                $"StoryWatcher={Find.StoryWatcher != null}");

            return outcome;
        }

        /// <summary>
        /// The save that Mode=Fast loads.
        ///
        /// Required, with no fallback, for the same reason GAMERL_INSTANCE_ID
        /// is: a template picked from a global default is a template another
        /// colony may have rewritten, and the failure then presents as a broken
        /// episode instead of a broken configuration. Falling back to the world
        /// already in memory is worse than nothing — it is the same colony,
        /// which reads as a successful reset.
        /// </summary>
        internal static string ResetTemplate()
        {
            var template = Environment.GetEnvironmentVariable("GAMERL_RESET_TEMPLATE");
            if (string.IsNullOrWhiteSpace(template))
            {
                throw new InvalidOperationException(
                    "GAMERL_RESET_TEMPLATE is not set. Reset mode \"Fast\" loads a known-good save, " +
                    "and game-rl does not fall back to the colony currently in memory. Set " +
                    "GAMERL_RESET_TEMPLATE to a save name exactly as listed in RimWorld's Saves " +
                    "folder, without the .rws extension.");
            }

            return template.Trim();
        }

        /// <summary>
        /// Replace the world with the template save, then apply the seed.
        ///
        /// Called synchronously from ProcessCommands, which runs from the
        /// TickManager.DoSingleTick postfix or from the frame postfix on
        /// Verse.Root_Entry/Root_Play.Update, and that is a deliberate choice
        /// rather than a convenient one.
        ///
/// WHY THE LOAD IS WRAPPED, which used to be an assumption and is now a
        /// verified fact.
        ///
        /// This is not a defensive wrapper. Vanilla calls
        /// SavedGameLoaderNow.LoadGameFromSaveFileNow from exactly two places,
        /// Verse/Root_Play.Start, and both wrap it:
        ///
        ///     LongEventHandler.QueueLongEvent(
        ///         () => SavedGameLoaderNow.LoadGameFromSaveFileNow(name),
        ///         "LoadingLongEvent", doAsynchronously: true,
        ///         GameAndMapInitExceptionHandlers.ErrorWhileLoadingGame);
        ///
        /// That wrapper is load-bearing, and dropping it corrupts the load.
        /// Verse.LongEventHandler.ExecuteWhenFinished runs its delegate
        /// immediately and synchronously whenever no long event is displayed:
        ///
        ///     if ((currentEvent == null || currentEvent.ShouldWaitUntilDisplayed)
        ///         && !executingToExecuteWhenFinished)
        ///         ExecuteToExecuteWhenFinished();
        ///
        /// Verse.Game.LoadGame sets CurrentMap partway through, and the
        /// CurrentMap setter calls AmbientSoundManager.Notify_SwitchedMap(),
        /// which is LongEventHandler.ExecuteWhenFinished(RecreateMapSustainers).
        /// Crucially Game.LoadGame assigns CurrentMap *before* it calls
        /// Scribe.loader.FinalizeLoading(), which is where cross-references are
        /// resolved, so at that instant Verse.MapInfo.parent is still null.
        /// RecreateMapSustainers reads Find.CurrentMap.Biome, Map.Biome reads
        /// Map.TileInfo, and TileInfo is Find.WorldGrid[Tile] where
        /// Verse.MapInfo.Tile is `parent?.Tile ?? PlanetTile.Invalid`.
        /// PlanetTile.Invalid is new PlanetTile(-1), so the lookup is
        /// Surface.Tiles[-1] and List<T>.get_Item throws
        /// ArgumentOutOfRangeException.
        ///
        /// Vanilla never sees this because QueueLongEvent guarantees
        /// currentEvent != null, so the delegate is deferred to the end of the
        /// load, by which time FinalizeLoading has resolved parent. Called
        /// bare, the exception is thrown inside Verse.Game.LoadGame, caught by
        /// LongEventHandler's own handler and logged as "Could not execute
        /// post-long-event action" — which is exactly what was in the log, once
        /// per reset, on every reset, with the game still running afterwards.
        ///
        /// So it was never fatal, and it was never Mode=Fresh. Wrapping restores
        /// vanilla's ordering while keeping the blocking contract this method
        /// documents: with no long event in flight ExecuteWhenFinished runs the
        /// delegate inline, the load still completes before Reset returns, and
        /// the nested Notify_SwitchedMap call is deferred by the
        /// executingToExecuteWhenFinished guard until the load has finished.
        ///
        /// The load does not require the main menu. Vanilla reaches it from the
        /// load-game UI and from Root_Play, and it refuses to run while a long
        /// event is in flight, which is why that is checked first. The frame
        /// pump means this is reachable from the main menu too, which is where
        /// vanilla's own call site also is.
        /// </summary>
        private static void LoadResetTemplate(string template)
        {
            if (LongEventHandler.ShouldWaitForEvent)
            {
                throw new InvalidOperationException(
                    "Cannot load the reset template while a long event is in progress. RimWorld " +
                    "refuses to load a save at that point, and forcing it would replace the world " +
                    "from inside a half-finished operation.");
            }

            if (!SaveGameFilesUtility.SavedGameNamedExists(template))
            {
                throw new FileNotFoundException(
                    $"GAMERL_RESET_TEMPLATE names save '{template}', and RimWorld does not list it. " +
                    $"game-rl did not fall back to the colony currently loaded. RimWorld resolves save " +
                    $"names under its own save directory " +
                    $"({UnityEngine.Application.persistentDataPath}/Saves); export " +
                    $"GAMERL_RESET_TEMPLATE with the save name exactly as it appears there, without " +
                    $"the .rws extension.");
            }

            Log.Message($"[GameRL] Loading reset template '{template}'");

            // The load draws from the global RNG stream, and everything after it
            // must be seeded from the request rather than from whatever the
            // previous episode left behind. Push/pop gives exactly that: the
            // load sees the old stream and cannot leak into the new episode.
            Rand.PushState();
            Exception? loadFailure = null;
            // Synchronous on purpose, and not because ExecuteWhenFinished is
            // documented to be synchronous: it is, because no long event is in
            // flight (checked above), which is the one branch where
            // ExecuteWhenFinished runs its delegate inline. That inline run is
            // also what sets executingToExecuteWhenFinished, which is the flag
            // that defers the load's own nested ExecuteWhenFinished call until
            // after FinalizeLoading. See the note on this method.
            //
            // The failure has to be caught INSIDE the delegate. An outer
            // try/catch around ExecuteWhenFinished cannot see it: on the inline
            // path LongEventHandler.ExecuteToExecuteWhenFinished runs each
            // delegate inside its own try/catch and logs it, so the exception
            // never propagates out. That was not theoretical — with the capture
            // outside, a load that threw InvalidCastException was reported as
            // "the colony was replaced but is not playable ... 0 free colonists",
            // naming a symptom of a load that never finished. A failure the
            // caller cannot see is a failure it will explain wrongly.
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    SavedGameLoaderNow.LoadGameFromSaveFileNow(template);
                }
                catch (Exception ex)
                {
                    loadFailure = ex;
                }
            });
            Rand.PopState();

            // An unbalanced stack means the loader pushed state it never popped,
            // so the next Rand read would come off the wrong stream. Reported
            // rather than absorbed, and kept alongside the original failure so
            // neither hides the other.
            string? imbalance = null;
            try
            {
                Rand.EnsureStateStackEmpty();
            }
            catch (Exception ex)
            {
                imbalance = ex.Message;
                Log.Error($"[GameRL] Rand state stack not empty after loading '{template}': {ex.Message}");
            }

            if (loadFailure != null)
            {
                throw new InvalidOperationException(
                    $"Loading reset template '{template}' failed: {loadFailure.Message}", loadFailure);
            }

            if (imbalance != null)
            {
                throw new InvalidOperationException(
                    $"Loading reset template '{template}' left the Rand state stack unbalanced: {imbalance}");
            }
        }

        /// <summary>
        /// Drop everything that must not survive the episode boundary.
        /// </summary>
        private void ClearEpisodeState()
        {
            _episodeStartTick = Find.TickManager?.TicksGame ?? 0;

            // The reward baseline is re-read here, after the world has been
            // replaced, so the first delta of the episode is measured against
            // this colony and not against the tail of the previous one. Read
            // through the same capture the steps use: a baseline taken from a
            // different measurement than the step it is compared against
            // manufactures a delta that no colony earned.
            _rewardBaseline = RewardSnapshot.Capture();
            _episodeRewardTotal = 0.0;
            _lastRewardFrame = null;

            ResetAgentStates();  // Reset first observation flag for all agents

            // Delta snapshots, pending events and event rate limits are all
            // per-episode: carrying them over means episode N+1 starts from
            // episode N's state and reads it as new.
            _stateExtractor.ClearEpisodeState(_agents.Keys);
        }

        /// <summary>
        /// Whether the episode is over, and why.
        /// </summary>
        /// <remarks>
        /// Checked before the step's reward is measured, which is why the totals
        /// logged here are the episode return up to the previous step and not
        /// including this one. The wire has no field for an episode return —
        /// Reward and RewardComponents are per step and their names are fixed —
        /// so this log is the only place the accumulated number surfaces.
        /// </remarks>
        public (bool done, bool truncated, string? reason) CheckTermination()
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                LogEpisodeEnd("no_map");
                return (true, false, "no_map");
            }

            // Check if all colonists dead
            if (map.mapPawns.FreeColonistsCount == 0)
            {
                LogEpisodeEnd("colony_destroyed");
                return (true, false, "colony_destroyed");
            }

            // Check episode length
            var ticksElapsed = (Find.TickManager?.TicksGame ?? 0) - _episodeStartTick;
            if (ticksElapsed >= MaxEpisodeTicks)
            {
                LogEpisodeEnd("timeout");
                return (false, true, "timeout");
            }

            return (false, false, null);
        }

        private void LogEpisodeEnd(string reason)
        {
            Log.Message(
                $"[GameRL] Episode ended ({reason}); reward through the previous step " +
                $"{_episodeRewardTotal}");
        }

        /// <summary>
        /// Measure this step's reward, once.
        ///
        /// One capture of the colony, one call into the reward function, one
        /// frame out. The frame carries both numbers the step needs — the total
        /// and the components — so the pair cannot come from two computations.
        ///
        /// No agentId, deliberately. The reward describes the colony, and the
        /// manifest declares a single agent (<c>multi_agent: false</c>,
        /// <c>max_agents: 1</c>), so there is one colony and one measurement per
        /// step however many agents are registered. Handing this an agent id
        /// would invite per-agent reward out of a shared delta baseline, which
        /// is how a second agent used to be paid the first agent's deltas.
        ///
        /// Advances the episode baseline and the episode total as a side effect;
        /// that state lives here, in the executor, and is cleared per episode.
        /// </summary>
        public RewardFrame ComputeRewardFrame()
        {
            var current = RewardSnapshot.Capture();

            // A mapless reading is not a measurement, so it neither rewards nor
            // moves the baseline: the delta after the map comes back spans the
            // whole gap, which is the same thing the old early return did.
            if (!current.HasMap)
            {
                _lastRewardFrame = RewardFrame.Empty();
                return _lastRewardFrame;
            }

            if (!_rewardBaseline.HasValue)
            {
                // Stepping without a reset. There is no honest delta to report,
                // so this step is worth zero and says so, rather than measuring
                // a whole colony against a baseline of zeroes and calling the
                // result a reward.
                Log.Warning(
                    "[GameRL] Reward step before any reset: no episode baseline exists, so this " +
                    "step's reward is 0 and the baseline is taken from this step. Call reset " +
                    "before stepping.");
                _rewardBaseline = current;
                _lastRewardFrame = RewardFrame.Empty();
                return _lastRewardFrame;
            }

            var frame = _rewardCalculator.Compute(_rewardBaseline.Value, current);

            if (frame.DroppedComponents.Count > 0)
            {
                // Named, not counted: a component that could not be measured is
                // information about the game state, and "some component" is not
                // a diagnosis.
                Log.Warning(
                    $"[GameRL] Dropped {frame.DroppedComponents.Count} non-finite reward " +
                    $"component(s): {string.Join(", ", frame.DroppedComponents)}. The game " +
                    "produced NaN or infinity for these.");
            }

            // The next step is measured against this reading, not against
            // whatever the world looks like when it is next asked.
            _rewardBaseline = current;
            _episodeRewardTotal += frame.Total;
            _lastRewardFrame = frame;
            return frame;
        }

        /// <summary>
        /// The components of the frame from the most recent step.
        ///
        /// Reads the frame rather than recomputing: the interface asks for
        /// components and a total as two calls, and recomputing on the second
        /// one is what made them disagree.
        /// </summary>
        public Dictionary<string, double> ComputeReward(string agentId)
        {
            return LastFrame().CopyComponents();
        }

        /// <summary>
        /// The total of the frame from the most recent step.
        ///
        /// The same object <see cref="ComputeReward"/> answered from, so the two
        /// agree by construction rather than by both happening to sum equal
        /// numbers.
        /// </summary>
        public double GetTotalReward(string agentId)
        {
            return LastFrame().Total;
        }

        /// <summary>
        /// The frame for the current step, refusing rather than inventing one.
        ///
        /// A reward computed outside <see cref="ComputeRewardFrame"/> would be a
        /// second measurement of the same step, which is the defect this file
        /// exists to remove; a caller that wants a reward asks for the frame.
        /// </summary>
        private RewardFrame LastFrame()
        {
            if (_lastRewardFrame == null)
            {
                throw new InvalidOperationException(
                    "No reward has been computed for this step. Call ComputeRewardFrame once per " +
                    "step and use the frame it returns: it carries the total and the components " +
                    "together, and computing them separately is how they came to disagree.");
            }

            return _lastRewardFrame;
        }

        private class AgentInfo
        {
            public string AgentType { get; set; } = "";
            public Dictionary<string, object> Config { get; set; } = new();
            public ObservationMode ObservationMode { get; set; } = ObservationMode.Minimal;
            public bool FirstObservation { get; set; } = true;
            public string? LastStateHash { get; set; }
            public DeltaConfig DeltaConfig { get; set; } = DeltaConfig.Minimal;
        }

        /// <summary>
        /// Gets observation mode for an agent
        /// </summary>
        public ObservationMode GetObservationMode(string agentId)
        {
            return _agents.TryGetValue(agentId, out var info) ? info.ObservationMode : ObservationMode.Minimal;
        }

        /// <summary>
        /// Checks if this is the agent's first observation (requires full state)
        /// </summary>
        public bool IsFirstObservation(string agentId)
        {
            return _agents.TryGetValue(agentId, out var info) ? info.FirstObservation : true;
        }

        /// <summary>
        /// Marks first observation as sent for an agent
        /// </summary>
        public void MarkFirstObservationSent(string agentId)
        {
            if (_agents.TryGetValue(agentId, out var info))
            {
                info.FirstObservation = false;
            }
        }

        /// <summary>
        /// Gets the last state hash for an agent
        /// </summary>
        public string? GetLastStateHash(string agentId)
        {
            return _agents.TryGetValue(agentId, out var info) ? info.LastStateHash : null;
        }

        /// <summary>
        /// Updates the last state hash for an agent
        /// </summary>
        public void SetLastStateHash(string agentId, string hash)
        {
            if (_agents.TryGetValue(agentId, out var info))
            {
                info.LastStateHash = hash;
            }
        }

        /// <summary>
        /// Gets delta config for an agent
        /// </summary>
        public DeltaConfig GetDeltaConfig(string agentId)
        {
            return _agents.TryGetValue(agentId, out var info) ? info.DeltaConfig : DeltaConfig.Minimal;
        }

        /// <summary>
        /// Resets first observation flag for all agents (called on Reset)
        /// </summary>
        public void ResetAgentStates()
        {
            foreach (var agent in _agents.Values)
            {
                agent.FirstObservation = true;
                agent.LastStateHash = null;
            }
        }
    }
}
