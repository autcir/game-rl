// Seed-consuming world generation for ResetMode.Fresh.
//
// This file used to carry a large comment explaining that Verse.Game.InitNewGame
// "has RVA 0 in the reference assembly, so neither what it requires nor whether
// it completes on the calling thread is knowable before running it". That was
// true of Krafs.Rimworld.Ref 1.6.4633, which the csproj compiles against and
// which ships every method body stripped. It was never true of the running
// game: the implementation is on disk, next to the executable, at
// RimWorldWin64_Data/Managed/Assembly-CSharp.dll, and decompiling it gives the
// whole new-game sequence.
//
// Everything below is transcribed from that assembly, RimWorld 1.6.4850 rev646,
// and the source of each requirement is named at the call site. The opt-in gate
// stays, but it now gates an attempt whose steps are known rather than guessed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Verse;
using RimWorld;
using GameRL.Harmony;
using RimWorld.GameRL.Patches;

// Aliased rather than a blanket `using RimWorld.Planet`, which would make bare
// `World` and `Tile` ambiguous against Verse's.
using Settlement = RimWorld.Planet.Settlement;
using PlanetTile = RimWorld.Planet.PlanetTile;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// One precondition of ResetMode.Fresh, with the value actually observed.
    /// </summary>
    internal sealed class FreshCheck
    {
        public FreshCheck(string name, bool satisfied, string observed)
        {
            Name = name;
            Satisfied = satisfied;
            Observed = observed;
        }

        public string Name { get; }

        public bool Satisfied { get; }

        public string Observed { get; }

        public string Describe()
        {
            return (Satisfied ? "[ok]   " : "[FAIL] ") + Name + ": " + Observed;
        }
    }

    /// <summary>
    /// Builds a world, a map and a colonist roster from the reset seed.
    ///
    /// This exists because Mode=Fast cannot satisfy the environment contract's
    /// Gate A: it loads one template save, so reset(A) and reset(B) are the same
    /// colony and the seed selects nothing. Mode=Fresh is the only mode where
    /// the seed decides the world.
    ///
    /// The order below is not invented. It is the order vanilla itself runs, read
    /// out of the shipped Assembly-CSharp.dll:
    ///
    ///   Verse.Root_Play.SetupForQuickTestPlay()   — Game, InitData, Scenario,
    ///                                                 PreConfigure, storyteller,
    ///                                                 GenerateWorld, starting tile,
    ///                                                 map size, PostIdeoChosen
    ///   Verse.Root_Play.Start() branch 3         — PrepForMapGen, PreMapGenerate,
    ///                                                 InitNewGame
    ///
    /// The one requirement that is easy to get wrong: InitNewGame does not
    /// create the player's Settlement, it requires one. It scans
    /// Find.WorldObjects.Settlements for the first Settlement whose Faction is
    /// Faction.OfPlayer, logs "Could not generate starting map because there is
    /// no any player faction base" if it finds none, and then dereferences the
    /// null settlement anyway. The Settlement is created by
    /// Scenario.PreMapGenerate, which reaches
    /// ScenPart_PlayerFaction.PreMapGenerate — Scenario.AllParts yields
    /// playerFaction first, ahead of surfaceLayer and the ordinary parts, so
    /// every stock scenario has one whether or not its def lists it.
    ///
    /// Every step is still followed by an observation, because a step that
    /// "should" work and a step that did are different things, and the log is
    /// the only record of which one happened.
    /// </summary>
    internal static class FreshResetGenerator
    {
        /// <summary>
        /// Explicit opt-in to attempt generation.
        ///
        /// Still required, and for a reason that did not change: generation
        /// abandons the game currently in memory without disposing it, which is
        /// what vanilla's own SavedGameLoaderNow.LoadGameFromSaveFileNow does
        /// when it reassigns Current.Game, but it is still a destructive act on
        /// a live colony and it should be asked for rather than inferred from a
        /// reset request. Setting it does not make the path correct; it makes the
        /// path run, and every step then throws with what it observed instead of
        /// reporting a colony nobody checked.
        /// </summary>
        internal const string OptInEnv = "GAMERL_FRESH_RESET";

        /// <summary>ScenarioDef to start from, when the wire sends none.</summary>
        internal const string ScenarioEnv = "GAMERL_FRESH_SCENARIO";

        /// <summary>
        /// ScenarioDef to start from, when neither the wire nor the environment
        /// names one.
        ///
        /// "Crashlanded", read out of the shipped
        /// Data/Core/Defs/Scenarios/Scenarios_Classic.xml. This used to be
        /// "Crashland", which is not a defName: the ScenarioDefs this install
        /// loads are Crashlanded, Tutorial, LostTribe, TheRichExplorer,
        /// NakedBrutality, TheAnomaly, Mechanitor, Sanguophage and TheGravship.
        /// DefDatabase.GetNamedSilentFail("Crashland") returned null, so the
        /// scenario_def_resolved probe could never pass and Mode=Fresh would
        /// have refused even with the opt-in set. A default that names nothing
        /// is a silent gate, and worse than no default at all.
        /// </summary>
        private const string DefaultScenarioDefName = "Crashlanded";
        private const string ColonistPawnKindDefName = "Colonist";
        private const int GeneratedColonists = 3;

        /// <summary>
        /// How far from the spawn anchor to look for somewhere to stand, in
        /// tiles. Wide enough to walk off a slope the pods landed on and off the
        /// starting structures, narrow enough that the search cannot wander into
        /// the other half of the map.
        /// </summary>
        private const int ColonistSearchRadius = 25;

        /// <summary>
        /// Generate a colony from the seed, or throw.
        ///
        /// Blocking, like Mode=Fast: the five reset postconditions are read out
        /// of the world this produces, so returning before the world exists would
        /// certify an episode that did not happen.
        /// </summary>
        /// <param name="seed">Seed as sent on the wire. Truncated to an int.</param>
        /// <param name="scenario">ScenarioDef name from the wire, or null.</param>
        internal static void Generate(ulong seed, string? scenario)
        {
            // The world generator draws from the global RNG stream, and the
            // episode that was running must not leak into the new one. Push and
            // pop give exactly that: generation sees a stream seeded from the
            // request, and the caller re-seeds the live stream afterwards.
            Rand.PushState();

            Exception? failure = null;
            try
            {
                var applied = RngManager.ApplySeed(seed);
                Log.Message($"[GameRL] Fresh reset: seed {seed} written to Rand.Seed as {applied}");

                RefuseIfLongEventInFlight();

                var checks = Probe(seed, scenario);
                var failed = checks.Where(c => !c.Satisfied).ToList();
                if (failed.Count > 0)
                {
                    // Every check is reported, satisfied ones included: the
                    // useful answer is which one blocked it, and the state of
                    // the ones that did not is what makes that readable.
                    throw new InvalidOperationException(
                        "Reset mode \"Fresh\" cannot run: " + failed.Count + " of " + checks.Count +
                        " preconditions are not satisfied. Observed:\n  " +
                        string.Join("\n  ", checks.Select(c => c.Describe())));
                }

                foreach (var check in checks)
                    Log.Message($"[GameRL] Fresh reset check {check.Describe()}");

                GenerateColony(applied, ScenarioName(scenario));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Rand.PopState();
            }

            // An unbalanced stack means generation pushed state it never popped,
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
                Log.Error($"[GameRL] Rand state stack not empty after Fresh reset: {ex.Message}");
            }

            if (failure != null)
            {
                throw new InvalidOperationException(
                    $"Fresh reset from seed {seed} failed: {failure.Message}", failure);
            }

            if (imbalance != null)
            {
                throw new InvalidOperationException(
                    $"Fresh reset from seed {seed} left the Rand state stack unbalanced: {imbalance}");
            }
        }

        /// <summary>
        /// A long event in flight means the world is mid-change.
        ///
        /// Checked before anything is generated rather than after: the failure
        /// this prevents is replacing the world from inside a half-finished
        /// operation, and by the time a generator has thrown, that damage is
        /// already possible.
        /// </summary>
        private static void RefuseIfLongEventInFlight()
        {
            var shouldWait = LongEventHandler.ShouldWaitForEvent;
            var anyWaiting = LongEventHandler.AnyEventNowOrWaiting;

            if (shouldWait || anyWaiting)
            {
                throw new InvalidOperationException(
                    "Reset mode \"Fresh\" refuses to start while a long event is in flight. " +
                    $"Observed LongEventHandler.ShouldWaitForEvent={shouldWait} and " +
                    $"AnyEventNowOrWaiting={anyWaiting}. Generating a world on top of an " +
                    "unfinished one replaces state that operation still owns.");
            }
        }

        /// <summary>
        /// Everything Mode=Fresh depends on, each with the value read right now.
        ///
        /// Runs before any generation, so a refusal costs nothing and names the
        /// blocker instead of leaving a half-built world behind.
        /// </summary>
        private static List<FreshCheck> Probe(ulong seed, string? scenario)
        {
            var checks = new List<FreshCheck>();

            // Not a pass/fail check: Rand.Seed is a setter with no getter, so
            // there is nothing to read back and nothing to compare. Reported so
            // the value the generator will actually be handed is on the record
            // next to the checks that can fail, and so the truncation of a
            // 64-bit wire seed is visible in the refusal rather than buried in
            // a variable.
            var truncated = (int)(seed & 0x7FFFFFFF);
            var droppedHighBits = seed > 0x7FFFFFFF;
            checks.Add(new FreshCheck(
                "seed_fits_rand_seed",
                true,
                $"wire seed {seed} reaches Rand.Seed as {truncated} (0x{truncated:X8}); " +
                (droppedHighBits
                    ? "bits 31 and above are dropped because Rand.Seed is an int"
                    : "the whole seed fits") +
                ", and Rand.Seed has no public getter so this is the value written " +
                "rather than one read back"));

            var programState = Current.ProgramState;
            checks.Add(new FreshCheck(
                "program_state_is_playing",
                programState == ProgramState.Playing,
                // Playing, not Entry: at the main menu there is no Game, no
                // TickManager and no world to replace, and starting one from a
                // frame handler is a different (and equally unverifiable) path.
                $"Current.ProgramState={programState}"));

            var game = Current.Game;
            var gameStoryteller = game?.storyteller?.def?.label;
            checks.Add(new FreshCheck(
                "game_present",
                game != null,
                $"Current.Game={(game == null ? "null" : "present")}, " +
                $"World={(game?.World == null ? "null" : "present")}, " +
                $"Maps={game?.Maps.Count.ToString() ?? "n/a"}, " +
                $"Storyteller={gameStoryteller ?? "null"}"));

            var tickManager = Find.TickManager;
            checks.Add(new FreshCheck(
                "tick_manager_present",
                tickManager != null,
                $"Find.TickManager={(tickManager == null ? "null" : "TicksGame=" + tickManager.TicksGame)}"));

            var playerFaction = Faction.OfPlayer;
            checks.Add(new FreshCheck(
                "player_faction_present",
                playerFaction != null,
                $"Faction.OfPlayer={(playerFaction == null ? "null" : playerFaction.def?.defName)}, " +
                $"free colonists={Find.CurrentMap?.mapPawns.FreeColonistsCount.ToString() ?? "no map"}"));

            // WorldGenerator.GameSetupStepsInOrder, the list vanilla's own
            // new-game flow walks, has a private getter in 1.6.4633 and cannot
            // be read from a mod. The setup-step defs behind it can, and their
            // absence is what an unloaded DefDatabase looks like.
            var setupStepCount = DefDatabase<GameSetupStepDef>.DefCount;
            checks.Add(new FreshCheck(
                "worldgen_setup_steps_resolved",
                setupStepCount > 0,
                $"DefDatabase<GameSetupStepDef>.DefCount={setupStepCount} " +
                "(WorldGenerator.GameSetupStepsInOrder itself has a private getter)"));

            var worldGenStepCount = DefDatabase<WorldGenStepDef>.DefCount;
            checks.Add(new FreshCheck(
                "worldgen_step_defs_resolved",
                worldGenStepCount > 0,
                $"DefDatabase<WorldGenStepDef>.DefCount={worldGenStepCount}"));

            var scenarioDef = DefDatabase<ScenarioDef>.GetNamedSilentFail(ScenarioName(scenario));
            checks.Add(new FreshCheck(
                "scenario_def_resolved",
                scenarioDef != null,
                $"DefDatabase<ScenarioDef>.GetNamedSilentFail(\"{ScenarioName(scenario)}\")=" +
                $"{(scenarioDef == null ? "null" : scenarioDef.defName)}, " +
                $"{DefDatabase<ScenarioDef>.DefCount} ScenarioDefs loaded"));

            var colonistKind = DefDatabase<PawnKindDef>.GetNamedSilentFail(ColonistPawnKindDefName);
            checks.Add(new FreshCheck(
                "colonist_pawn_kind_resolved",
                colonistKind != null,
                $"DefDatabase<PawnKindDef>.GetNamedSilentFail(\"{ColonistPawnKindDefName}\")=" +
                $"{(colonistKind == null ? "null" : colonistKind.defName)}, " +
                $"{DefDatabase<PawnKindDef>.DefCount} PawnKindDefs loaded"));

            checks.Add(new FreshCheck(
                "new_game_entry_point_verified",
                OptedIn(),
                "SEQUENCE READ FROM THE SHIPPED ASSEMBLY, not assumed. " +
                "Krafs.Rimworld.Ref 1.6.4633 has every body stripped, which is " +
                "where the old \"RVA 0, not knowable\" note came from; the real " +
                "Assembly-CSharp.dll sits next to the executable and decompiles. " +
                "Verse.Root_Play.SetupForQuickTestPlay plus the third branch of " +
                "Verse.Root_Play.Start are the whole new-game path: Game, InitData, " +
                "Scenario, PreConfigure, storyteller, GenerateWorld, starting tile, " +
                "map size, PostIdeoChosen, PrepForMapGen, PreMapGenerate, InitNewGame. " +
                "Verse.QuickTest does not exist in 1.6, and " +
                "RimWorld.Autotests_ColonyMaker.MakeColony requires a Playing game " +
                "that already owns a Settlement, which is what generation produces. " +
                $"Observed {OptInEnv}={Quote(Environment.GetEnvironmentVariable(OptInEnv))}. " +
                "With it unset, Mode=Fast is the only mode that returns."));

            return checks;
        }

        /// <summary>
        /// Vanilla's new-game order, transcribed from the shipped assembly.
        ///
        /// Runs inline, on the calling thread. Both call sites are Unity main
        /// thread — the tick postfix and the frame postfix, both driven from
        /// Verse.Root.Update — so every generator here completes before this
        /// returns and the observations below are of a finished world.
        ///
        /// Inline rather than queued, deliberately. Vanilla wraps its own new
        /// game in LongEventHandler.QueueLongEvent, and a synchronous queued
        /// event is pumped by LongEventHandler.LongEventsUpdate from that same
        /// Root.Update, so queueing one and then blocking here would deadlock.
        /// The consequence of running inline is that LongEventHandler has no
        /// long event in flight, which is why RimWorldCommandExecutor's Fast
        /// path has to wrap its save load in ExecuteWhenFinished: see the note
        /// there. Nothing here queues, and Verse.Game.FinalizeInit's own
        /// ExecuteWhenFinished delegate runs inline with ProgramState already
        /// Playing, so it cannot observe a half-built game.
        /// </summary>
        private static void GenerateColony(int appliedSeed, string scenarioName)
        {
            var scenarioDef = DefDatabase<ScenarioDef>.GetNamedSilentFail(scenarioName);
            if (scenarioDef == null)
            {
                throw new InvalidOperationException(
                    $"ScenarioDef '{scenarioName}' disappeared between the probe and " +
                    $"generation. Observed {DefDatabase<ScenarioDef>.DefCount} ScenarioDefs " +
                    "loaded.");
            }

            var scenario = scenarioDef.scenario;
            if (scenario == null)
            {
                throw new InvalidOperationException(
                    $"ScenarioDef '{scenarioName}' carries no scenario object " +
                    "(ScenarioDef.scenario is null), so there is nothing to run the " +
                    "new-game scenario parts from.");
            }

            var colonistKind = DefDatabase<PawnKindDef>.GetNamedSilentFail(ColonistPawnKindDefName);
            if (colonistKind == null)
            {
                throw new InvalidOperationException(
                    $"PawnKindDef '{ColonistPawnKindDefName}' is not loaded, so there is no " +
                    "kind to generate colonists from. Observed " +
                    $"{DefDatabase<PawnKindDef>.DefCount} PawnKindDefs loaded.");
            }

            // The seed's only route into the world. GenerateWorld derives every
            // draw from GenText.StableStringHash(seedString), and WorldInfo.Seed
            // is that same hash, so this string is the observable link between the
            // wire seed and the world that came out. Culture-invariant because
            // some cultures format integers differently and the hash is on the
            // characters, not the number.
            var seedString = appliedSeed.ToString(CultureInfo.InvariantCulture);

            // Odyssey widens the planet, and WorldGenerator names both defaults
            // itself. Guessing 0.3 for an Odyssey world is how a planet half the
            // intended size gets generated.
            var coverage = ModsConfig.OdysseyActive
                ? RimWorld.Planet.WorldGenerator.DefaultPlanetCoverageOdyssey
                : RimWorld.Planet.WorldGenerator.DefaultPlanetCoverage;

            // ── Verse.Root_Play.SetupForQuickTestPlay, in order ──────────────
            Current.ProgramState = ProgramState.Entry;
            Game.ClearCaches();

            // The game being replaced stops being observable the moment
            // Current.Game is reassigned, and it is abandoned rather than
            // disposed. That is not a shortcut: Verse.Game.Dispose tears down
            // the world's planet-layer native arrays, and Verse.Game's own save
            // load reassigns Current.Game without disposing either, so disposing
            // here would be stricter than vanilla while standing in the middle of
            // building its replacement. The consequence is a retained old Game
            // per reset, which is a Gate F leak, reported rather than hidden.
            var replacedGame = Current.Game;
            Log.Message(
                $"[GameRL] Fresh reset replacing the running game: " +
                $"world={(replacedGame?.World?.info?.name ?? "none")}, " +
                $"maps={replacedGame?.Maps.Count.ToString() ?? "n/a"}, " +
                $"tick={Find.TickManager?.TicksGame}, " +
                $"storyteller={replacedGame?.storyteller?.def?.defName ?? "none"}. " +
                "The old game is abandoned, not disposed, exactly as " +
                "SavedGameLoaderNow.LoadGameFromSaveFileNow leaves it.");

            var newGame = new Game();
            Current.Game = newGame;

            var initData = new GameInitData
            {
                mapSize = GameInitData.DefaultMapSize,
                startingSeason = Season.Spring,
                permadeath = false,
                permadeathChosen = true,
                startedFromEntry = false
            };
            newGame.InitData = initData;

            // Before anything that reads Find.Scenario. Find.Scenario returns
            // Current.Game.Scenario, and WorldGenerator.GenerateWorld needs it
            // twice: the planet layers are created from Scenario.AllParts by
            // WorldGrid.CreateRequiredLayers, and GenerateWorld itself ends by
            // calling Find.Scenario.PostWorldGenerate. Generating a world with no
            // scenario is not a degraded world, it is a null dereference.
            newGame.Scenario = scenario;
            Find.Scenario.PreConfigure();

            // The storyteller and difficulty the colony already had, so a reset
            // does not silently change who narrates the episode. Read from the
            // old game because a fresh Game only has the parameterless
            // Storyteller ctor, which leaves def and difficultyDef null.
            var storytellerDef = replacedGame?.storyteller?.def ?? StorytellerDefOf.Cassandra;
            var difficultyDef = replacedGame?.storyteller?.difficultyDef ?? DifficultyDefOf.Rough;
            newGame.storyteller = new Storyteller(storytellerDef, difficultyDef);

            // ── World ───────────────────────────────────────────────────────
            // GenerateWorld assigns Current.CreatingWorld, runs the setup steps
            // and the world gen steps, calls Find.Scenario.PostWorldGenerate,
            // returns the world and then nulls Current.CreatingWorld in its
            // finally. So nothing may be assigned to Current.CreatingWorld
            // after this, and nothing may read Find.World between the return and
            // the assignment below: with CreatingWorld already null and
            // Current.Game.World still null, Find.World is null and so is
            // everything hanging off it, FactionManager included.
            var world = RimWorld.Planet.WorldGenerator.GenerateWorld(
                coverage,
                seedString,
                RimWorld.Planet.WorldGenerator.DefaultOverallRainfall,
                RimWorld.Planet.WorldGenerator.DefaultOverallTemperature,
                RimWorld.Planet.WorldGenerator.DefaultOverallPopulation,
                RimWorld.Planet.WorldGenerator.DefaultLandmarkDensity);

            if (world == null)
            {
                throw new InvalidOperationException(
                    "RimWorld.Planet.WorldGenerator.GenerateWorld returned null. Observed " +
                    $"Current.CreatingWorld={(Current.CreatingWorld == null ? "null" : "set")}, " +
                    $"ProgramState={Current.ProgramState}, " +
                    $"LongEventHandler.ShouldWaitForEvent={LongEventHandler.ShouldWaitForEvent}.");
            }

            newGame.World = world;

            // WorldInfo.Seed is the first place in the whole path where the seed
            // can be read back rather than assumed: it is a public getter that
            // hashes the seedString generation was given.
            Log.Message(
                $"[GameRL] Fresh world generated: name='{world.info?.name}', " +
                $"seedString='{world.info?.seedString}', WorldInfo.Seed={world.info?.Seed}, " +
                $"requested Rand.Seed={appliedSeed}, coverage={coverage}, " +
                $"surface tiles={world.grid?.Surface?.TilesCount.ToString() ?? "n/a"}, " +
                $"layers={world.grid?.PlanetLayers?.Count.ToString() ?? "n/a"}, " +
                $"factions={world.factionManager?.AllFactions.Count() ?? 0}");

            // ── Starting tile, map size, ideology ───────────────────────────
            // ChooseRandomStartingTile asks TileFinder.RandomSettlementTileFor
            // with Faction.OfPlayer, which now resolves through the new world's
            // faction manager because Current.Game.World is set.
            Find.GameInitData.ChooseRandomStartingTile();
            Find.GameInitData.mapSize = GameInitData.DefaultMapSize;

            // GenerateWorld calls PostIdeoChosen only when Ideology is *absent*,
            // so with the DLC active this is generation's job, not the caller's.
            if (ModsConfig.IdeologyActive)
            {
                Find.Scenario.PostIdeoChosen();
            }

            var startingTile = Find.GameInitData.startingTile;
            RequireTileInGrid(world, startingTile, "GameInitData.startingTile after ChooseRandomStartingTile");

            Log.Message(
                $"[GameRL] Fresh starting tile chosen: {startingTile}, " +
                $"mapSize={Find.GameInitData.mapSize}x{Find.GameInitData.mapSize}, " +
                $"playerFaction={Find.GameInitData.playerFaction?.def?.defName ?? "null"}, " +
                $"IdeologyActive={ModsConfig.IdeologyActive}");

            // ── Pre-map ─────────────────────────────────────────────────────
            // PrepForMapGen reconciles the starting pawn roster, which is empty
            // here because nothing ran the scenario's ConfigureStartingPawns
            // config page. That is fine: colonists are spawned below, observed,
            // and reported. PreMapGenerate is not optional — it is what creates
            // the player Settlement that InitNewGame goes looking for.
            Find.GameInitData.PrepForMapGen();
            Find.Scenario.PreMapGenerate();

            var settlement = RequirePlayerSettlement(world);

            // ── Map ────────────────────────────────────────────────────────
            // InitNewGame generates the map itself: it finds the player
            // Settlement, calls MapGenerator.GenerateMap with
            // new IntVec3(initData.mapSize, 1, initData.mapSize), and
            // GenerateMap calls Current.Game.AddMap(map) before generating
            // contents. Generating one here instead would leave two maps, and
            // mapGeneratorDef is left null on purpose so the settlement's own
            // MapGeneratorDef is used.
            initData.mapGeneratorDef = null;
            newGame.InitNewGame();

            Log.Message(
                $"[GameRL] InitNewGame returned: ProgramState={Current.ProgramState}, " +
                $"world={Current.Game?.World?.info?.name ?? "null"}, " +
                $"playerFaction={Faction.OfPlayer?.def?.defName ?? "null"}, " +
                $"maps={Current.Game?.Maps.Count.ToString() ?? "n/a"}, " +
                $"Storyteller={Find.Storyteller?.def?.label ?? "none"}, " +
                $"TicksGame={Find.TickManager?.TicksGame}");

            if (Current.Game == null || Find.TickManager == null)
            {
                throw new InvalidOperationException(
                    "Verse.Game.InitNewGame did not leave a playable game behind. Observed " +
                    $"Current.Game={(Current.Game == null ? "null" : "present")}, " +
                    $"Current.Game.World={(Current.Game?.World == null ? "null" : "present")}, " +
                    $"Find.TickManager={(Find.TickManager == null ? "null" : "present")}, " +
                    $"Current.ProgramState={Current.ProgramState}, " +
                    $"player faction of the new world=" +
                    $"{world.factionManager?.OfPlayer?.def?.defName ?? "null"}.");
            }

            var map = newGame.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException(
                    "InitNewGame returned without a current map. Observed " +
                    $"maps in game={newGame.Maps.Count}, ProgramState={Current.ProgramState}, " +
                    $"playerFaction={Faction.OfPlayer?.def?.defName ?? "null"}, " +
                    $"player settlement={settlement.ID} at {settlement.Tile}.");
            }

            // The map's parent must be the settlement PreMapGenerate created, and
            // Map.TileInfo must be readable. Map.TileInfo is
            // Find.WorldGrid[MapInfo.Tile] where MapInfo.Tile is
            // `parent?.Tile ?? PlanetTile.Invalid`, and Find.WorldGrid's indexer
            // is tile.Layer.Tiles[tile.tileId] — a raw List index with no bounds
            // check. So an unattached map does not fail loudly, it indexes -1 and
            // throws ArgumentOutOfRangeException from inside vanilla code that has
            // no idea a mod is involved. This is the one read that used to kill a
            // reset, so it is read here, where the failure names itself.
            RequireTileInGrid(world, map.Tile, $"map {map.uniqueID} after InitNewGame");
            if (map.Parent != settlement)
            {
                throw new InvalidOperationException(
                    "The generated map is attached to a different world object than the " +
                    "settlement PreMapGenerate created. Observed map.Parent=" +
                    $"{(map.Parent == null ? "null" : map.Parent.GetType().Name + " " + map.Parent.ID + " at " + map.Parent.Tile)}, " +
                    $"expected Settlement {settlement.ID} at {settlement.Tile}, " +
                    $"map.Tile={map.Tile}, map.TileInfo={map.TileInfo}.");
            }

            Log.Message(
                $"[GameRL] Fresh map generated: {map.info.Size.x}x{map.info.Size.z}, " +
                $"tile={map.Tile}, biome={map.TileInfo.PrimaryBiome.defName}, " +
                $"settlement={settlement.ID}, " +
                $"hasMap={settlement.HasMap}, surfaceTiles={world.grid?.Surface?.TilesCount.ToString() ?? "n/a"}");

            if (Find.Storyteller == null)
            {
                throw new InvalidOperationException(
                    "The generated colony has no storyteller, so incidents would never run " +
                    "and the episode would silently be a static world. Observed " +
                    $"Find.Storyteller=null, " +
                    $"Find.StoryWatcher={(Find.StoryWatcher == null ? "null" : "present")}, " +
                    $"tick={Find.TickManager?.TicksGame}, maps={newGame.Maps.Count}, " +
                    $"free colonists={map.mapPawns.FreeColonistsCount}, " +
                    $"ProgramState={Current.ProgramState}.");
            }

            // ── Colonists ───────────────────────────────────────────────────
            SpawnColonists(map, colonistKind);

            Log.Message(
                $"[GameRL] Fresh colony complete: seed {appliedSeed} (seedString '{seedString}'), " +
                $"world '{world.info?.name}' (WorldInfo.Seed={world.info?.Seed}), map " +
                $"{map.info.Size.x}x{map.info.Size.z} on tile {map.Tile}, " +
                $"{map.mapPawns.FreeColonistsCount} free colonists, " +
                $"Storyteller={Find.Storyteller?.def?.label}, tick={Find.TickManager?.TicksGame}");
        }

        /// <summary>
        /// The player Settlement that Scenario.PreMapGenerate must have created.
        ///
        /// Verse.Game.InitNewGame looks this up itself, by scanning
        /// Find.WorldObjects.Settlements for the first Settlement whose Faction is
        /// Faction.OfPlayer, and when it finds none it only logs an error before
        /// dereferencing the null. Checking here turns that into an exception
        /// that says which of the two things went wrong: no settlement at all,
        /// or settlements that all belong to somebody else.
        /// </summary>
        private static Settlement RequirePlayerSettlement(RimWorld.Planet.World world)
        {
            var settlements = world.worldObjects?.Settlements;
            if (settlements == null)
            {
                throw new InvalidOperationException(
                    "The generated world has no world-object holder, so nothing can own the " +
                    "colony map. Observed " +
                    $"World.worldObjects={(world.worldObjects == null ? "null" : "present")}.");
            }

            var playerFaction = Faction.OfPlayer;
            if (playerFaction == null || playerFaction.def == null)
            {
                throw new InvalidOperationException(
                    "Faction.OfPlayer is null or defless in the generated world, so " +
                    "ScenPart_PlayerFaction.PreMapGenerate had no faction to give the " +
                    "Settlement and InitNewGame would never find its home. Observed " +
                    $"{DefDatabase<ScenarioDef>.DefCount} ScenarioDefs loaded, " +
                    $"{settlements.Count} settlements, " +
                    $"factions in the new world={world.factionManager?.AllFactions.Count() ?? 0}, " +
                    $"ProgramState={Current.ProgramState}.");
            }

            Settlement? chosen = null;
            var described = new List<string>();

            foreach (var settlement in settlements)
            {
                if (settlement == null)
                {
                    described.Add("null entry");
                    continue;
                }

                var mine = settlement.Faction == playerFaction;
                described.Add(
                    $"Settlement {settlement.ID} at {settlement.Tile} -> " +
                    $"{(settlement.Faction?.def?.defName ?? "no faction")}" +
                    (mine ? " [PLAYER]" : "") +
                    (settlement.MapGeneratorDef == null ? ", no MapGeneratorDef" : ""));

                if (chosen == null && mine)
                {
                    chosen = settlement;
                }
            }

            if (chosen == null)
            {
                throw new InvalidOperationException(
                    "Scenario.PreMapGenerate produced no Settlement owned by the player, " +
                    "which is what Verse.Game.InitNewGame requires and dereferences. " +
                    $"Observed Faction.OfPlayer={playerFaction.def.defName} and " +
                    $"{settlements.Count} settlements: " +
                    (described.Count == 0 ? "none" : string.Join("; ", described)) + ".");
            }

            if (chosen.MapGeneratorDef == null)
            {
                throw new InvalidOperationException(
                    "The player Settlement has no MapGeneratorDef, so " +
                    "Verse.MapGenerator.GenerateMap has nothing to generate with. Observed " +
                    $"Settlement {chosen.ID} at {chosen.Tile}, faction " +
                    $"{chosen.Faction?.def?.defName ?? "null"}, " +
                    $"{settlements.Count} settlements in the new world.");
            }

            return chosen;
        }

        /// <summary>
        /// A tile that the world's grid can actually answer for.
        ///
        /// This is the check the previous diagnosis asked for by name, and it is
        /// worth having as a check rather than as a hope because the failure it
        /// prevents is not local. Find.WorldGrid's indexer is
        /// tile.Layer.Tiles[tile.tileId] — a raw List index, no bounds check — so
        /// an out-of-range tile surfaces as ArgumentOutOfRangeException thrown
        /// from inside whatever vanilla code happened to read the biome next.
        /// PlanetTile.Layer resolves through Find.WorldGrid.PlanetLayers for a
        /// non-negative layerId, so a stale or absent layer is a second way to
        /// fail, and is checked here too.
        /// </summary>
        private static void RequireTileInGrid(RimWorld.Planet.World world, PlanetTile tile, string what)
        {
            var grid = world.grid;
            if (grid == null)
            {
                throw new InvalidOperationException(
                    $"The generated world has no grid, so {what} cannot be resolved. " +
                    $"Observed World.grid=null, tile={tile}.");
            }

            if (!tile.Valid)
            {
                throw new InvalidOperationException(
                    $"{what} is not a valid tile, and Verse.MapInfo.Tile falls back to " +
                    "PlanetTile.Invalid for an unattached map, which indexes -1 into the " +
                    $"grid. Observed tile={tile}, tileId={tile.tileId}, " +
                    $"surface tiles={grid.Surface?.TilesCount.ToString() ?? "n/a"}, " +
                    $"layers={grid.PlanetLayers?.Count.ToString() ?? "n/a"}.");
            }

            var layer = tile.Layer;
            if (layer == null)
            {
                throw new InvalidOperationException(
                    $"{what} names planet layer {tile.LayerDef?.defName ?? "?"}, which the " +
                    "generated world does not have. Observed " +
                    $"layers={grid.PlanetLayers?.Count.ToString() ?? "n/a"} " +
                    $"({string.Join(", ", grid.PlanetLayers?.Keys ?? new List<int>())}).");
            }

            if (tile.tileId >= layer.TilesCount)
            {
                throw new InvalidOperationException(
                    $"{what} is outside the grid, so WorldGrid[tile] would index past the end " +
                    $"of the layer. Observed tileId={tile.tileId}, " +
                    $"layer={layer.Def?.defName ?? "?"} with {layer.TilesCount} tiles.");
            }
        }

        /// <summary>
        /// Put colonists on the map, or throw with what the map holds.
        /// </summary>
        private static void SpawnColonists(Map map, PawnKindDef kind)
        {
            // Read back after InitNewGame rather than reused from before it:
            // vanilla's new-game setup rebuilds the player faction, and handing
            // new colonists the pre-generation one would produce pawns that no
            // longer belong to anyone.
            var faction = Faction.OfPlayer;
            if (faction == null || faction.def == null)
            {
                throw new InvalidOperationException(
                    "Faction.OfPlayer is null or defless after world generation, so generated " +
                    "colonists would have no faction and would not count as free colonists. " +
                    "Observed " +
                    $"Faction.OfPlayer={(faction == null ? "null" : "present")}, " +
                    $"faction def={(faction?.def?.defName ?? "null")}, " +
                    $"current world={Current.Game?.World?.info?.name ?? "null"}, " +
                    $"worlds known to FactionManager=" +
                    $"{Find.World?.factionManager?.AllFactions.Count() ?? 0}");
            }

            // Where the colonists go. Vanilla's own new game lands them at
            // MapGenerator.PlayerStartSpot — Verse.Game.InitNewGame passes it to
            // Find.CameraDriver.JumpToCurrentMapLoc, and the scenario's
            // ScenPart_PlayerPawnsArriveMethod drops pods there — so that is
            // the first place asked. It is a static that is only meaningful
            // straight after map generation, hence PlayerStartSpotValid, and
            // CellFinder.StandableCellNear can still walk out from it when the
            // pods landed on a slope. map.Center is the fallback, not the
            // anchor: the centre of a generated 250x250 RimWorld is rock about
            // as often as not, and anchoring there makes spawning depend on the
            // seed.
            var anchor = MapGenerator.PlayerStartSpotValid
                ? MapGenerator.PlayerStartSpot
                : map.Center;

            Log.Message(
                $"[GameRL] Generating {GeneratedColonists} colonists of kind {kind.defName} for " +
                $"faction {faction.def.defName} on " +
                $"{map.info.Size.x}x{map.info.Size.z}, anchored at {anchor} " +
                $"(PlayerStartSpotValid={MapGenerator.PlayerStartSpotValid})");

            for (var i = 0; i < GeneratedColonists; i++)
            {
                Pawn pawn;
                try
                {
                    pawn = PawnGenerator.GeneratePawn(kind, faction);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Verse.PawnGenerator.GeneratePawn threw for colonist {i + 1} of " +
                        $"{GeneratedColonists} (kind {kind.defName}). Observed " +
                        $"{ex.GetType().Name}: {ex.Message}", ex);
                }

                if (pawn == null)
                {
                    throw new InvalidOperationException(
                        $"Verse.PawnGenerator.GeneratePawn returned null for colonist {i + 1} " +
                        $"of {GeneratedColonists} (kind {kind.defName}). Observed " +
                        $"{map.mapPawns.FreeColonistsCount} free colonists on the map.");
                }

                var cell = CellFinder.StandableCellNear(anchor, map, ColonistSearchRadius, _ => true);
                if (cell == IntVec3.Invalid)
                {
                    throw new InvalidOperationException(
                        $"No standable cell within {ColonistSearchRadius} tiles of {anchor} for " +
                        $"colonist {i + 1}, so it cannot be spawned. Observed " +
                        $"{map.mapPawns.FreeColonistsCount} free colonists, " +
                        $"{map.info.Size.x}x{map.info.Size.z} map, " +
                        $"PlayerStartSpot={MapGenerator.PlayerStartSpot} " +
                        $"(valid={MapGenerator.PlayerStartSpotValid}), " +
                        $"map.Center={map.Center}.");
                }

                GenSpawn.Spawn(pawn, cell, map, Rot4.Random);
            }
        }

        private static string ScenarioName(string? scenario)
        {
            if (!string.IsNullOrWhiteSpace(scenario))
            {
                return scenario!.Trim();
            }

            var fromEnv = Environment.GetEnvironmentVariable(ScenarioEnv);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv.Trim();
            }

            return DefaultScenarioDefName;
        }

        /// <summary>
        /// Whether generation was explicitly asked for.
        ///
        /// Truthy is "1" or "true", nothing else. An env var that is set to
        /// something else has not opted in, and saying so beats treating any
        /// non-empty value as consent.
        /// </summary>
        private static bool OptedIn()
        {
            var raw = Environment.GetEnvironmentVariable(OptInEnv);
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string Quote(string? value)
        {
            return value == null ? "<unset>" : $"'{value}'";
        }
    }
}
