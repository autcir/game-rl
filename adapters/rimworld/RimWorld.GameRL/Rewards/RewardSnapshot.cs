// An immutable reading of the colony state the reward is computed from.
//
// This type is the boundary between the game and the reward. Capture() is the
// only code in the reward path that touches Verse/RimWorld at all, and it only
// reads. Everything downstream sees numbers and cannot tell whether they came
// from a live colony or from a fixture, which is what lets SurvivalReward.Compute
// be a function of its arguments rather than a function of the world.

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using RimWorld;
using RimWorld.GameRL.State;

namespace RimWorld.GameRL.Rewards
{
    /// <summary>
    /// Everything the reward reads out of the colony, frozen at one instant.
    ///
    /// Both endpoints of a delta are snapshots of this shape: the one taken at
    /// the end of the previous step and the one taken now. The reward function
    /// is handed both and reads neither the game nor itself, so calling it twice
    /// with the same pair cannot give two different answers.
    ///
    /// A struct, because a reading is a value rather than a thing with a
    /// lifetime: it is captured, handed to the reward and dropped, twice per
    /// step. Its default value is also a correct reading — no map, every count
    /// zero — so there is no invalid state to construct by accident.
    /// </summary>
    public readonly struct RewardSnapshot
    {
        /// <summary>
        /// False when there was no map to read: main menu, or a save still
        /// loading. Every other member is then zero, and
        /// <see cref="SurvivalReward.Compute"/> treats the pair as nothing to
        /// measure rather than as a colony that lost everything.
        /// </summary>
        public bool HasMap { get; }

        // ═══════════════════════════════════════════════════════════════
        // Counted values the deltas are taken against
        // ═══════════════════════════════════════════════════════════════

        public int ColonistCount { get; }

        public float Wealth { get; }

        public int FoodDays { get; }

        public int HostileCount { get; }

        public int WoodCount { get; }

        public int SteelCount { get; }

        public float ResearchProgress { get; }

        public int BuildingCount { get; }

        // ═══════════════════════════════════════════════════════════════
        // Levels. Not deltas: these are read as absolute conditions, so
        // they are averaged here rather than differenced.
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Mean mood across the roster, 0..1.</summary>
        public float MoodAverage { get; }

        /// <summary>Mean hunger across the roster, 0 (fed) to 1 (starving).</summary>
        public float AverageHunger { get; }

        /// <summary>Colonists below 20% rest.</summary>
        public int ExhaustedCount { get; }

        /// <summary>Mean health summary across the roster, 0..1.</summary>
        public float AverageHealth { get; }

        /// <summary>Colonists wandering or waiting.</summary>
        public int IdleCount { get; }

        private RewardSnapshot(
            bool hasMap,
            int colonistCount,
            float wealth,
            int foodDays,
            int hostileCount,
            int woodCount,
            int steelCount,
            float researchProgress,
            int buildingCount,
            float moodAverage,
            float averageHunger,
            int exhaustedCount,
            float averageHealth,
            int idleCount)
        {
            HasMap = hasMap;
            ColonistCount = colonistCount;
            Wealth = wealth;
            FoodDays = foodDays;
            HostileCount = hostileCount;
            WoodCount = woodCount;
            SteelCount = steelCount;
            ResearchProgress = researchProgress;
            BuildingCount = buildingCount;
            MoodAverage = moodAverage;
            AverageHunger = averageHunger;
            ExhaustedCount = exhaustedCount;
            AverageHealth = averageHealth;
            IdleCount = idleCount;
        }

        /// <summary>
        /// A reading of nothing, for when there is no map.
        ///
        /// Distinct from a reading of an empty colony on purpose: a mapless
        /// snapshot must not be differenced against, or the missing map reads
        /// as the loss of every colonist, every building and all the wealth.
        /// </summary>
        public static RewardSnapshot NoMap()
        {
            return new RewardSnapshot(
                hasMap: false,
                colonistCount: 0,
                wealth: 0f,
                foodDays: 0,
                hostileCount: 0,
                woodCount: 0,
                steelCount: 0,
                researchProgress: 0f,
                buildingCount: 0,
                moodAverage: 0f,
                averageHunger: 0f,
                exhaustedCount: 0,
                averageHealth: 0f,
                idleCount: 0);
        }

        /// <summary>
        /// Read the colony, now. The one impure step in the reward path.
        ///
        /// Every reading comes from the same pass, so the deltas the reward
        /// takes are between two readings taken the same way. That matters more
        /// than it sounds: the episode baseline used to come from
        /// Map.resourceCounter (stockpiles only) while the per-step reading came
        /// from a walk of the map, so the first step of an episode paid out
        /// wood_gathered for wood that was merely lying around.
        ///
        /// Never throws. A colony mid-teardown can fail any of these reads, and
        /// a reward path that takes the process down teaches nothing.
        /// </summary>
        public static RewardSnapshot Capture()
        {
            Map? map;
            try
            {
                map = Find.CurrentMap;
            }
            catch
            {
                return NoMap();
            }

            if (map == null)
            {
                return NoMap();
            }

            var colonists = map.mapPawns.FreeColonists.ToList();
            var colonistCount = colonists.Count;

            // One walk of the map, not three. ResourceExtractor reads counters and
            // lists; calling it again for the same instant returns the same
            // numbers at three times the cost.
            var resources = ResourceExtractor.Extract(map);

            var woodCount = resources.Stockpiles.TryGetValue(ThingDefOf.WoodLog.defName, out var wood)
                ? wood
                : 0;
            var steelCount = resources.Stockpiles.TryGetValue(ThingDefOf.Steel.defName, out var steel)
                ? steel
                : 0;

            // Averages are only read when the roster is non-empty, but they are
            // computed here for every snapshot, so they cannot be allowed to
            // throw on an empty roster: Enumerable.Average does.
            var moodAverage = 0f;
            var averageHunger = 0f;
            var averageHealth = 0f;
            var exhaustedCount = 0;
            var idleCount = 0;

            if (colonistCount > 0)
            {
                moodAverage = colonists.Average(p => p.needs?.mood?.CurLevelPercentage ?? 0.5f);
                averageHunger = colonists.Average(p => 1.0f - (p.needs?.food?.CurLevelPercentage ?? 1f));
                averageHealth = colonists.Average(p => p.health?.summaryHealth?.SummaryHealthPercent ?? 1f);

                foreach (var colonist in colonists)
                {
                    // Lifted comparison: a colonist with no rest need reads as
                    // false here, exactly as the Count() this replaced did.
                    if (colonist.needs?.rest?.CurLevelPercentage < 0.2f)
                    {
                        exhaustedCount++;
                    }

                    var jobDef = colonist.CurJob?.def;
                    if (jobDef == JobDefOf.Wait_Wander || jobDef == JobDefOf.Wait)
                    {
                        idleCount++;
                    }
                }
            }

            return new RewardSnapshot(
                hasMap: true,
                colonistCount: colonistCount,
                wealth: map.wealthWatcher.WealthTotal,
                foodDays: resources.FoodDays,
                hostileCount: CountHostiles(map),
                woodCount: woodCount,
                steelCount: steelCount,
                researchProgress: GetResearchProgress(),
                buildingCount: map.listerBuildings.allBuildingsColonist.Count,
                moodAverage: moodAverage,
                averageHunger: averageHunger,
                exhaustedCount: exhaustedCount,
                averageHealth: averageHealth,
                idleCount: idleCount);
        }

        /// <summary>
        /// Hostile pawns on the map.
        ///
        /// Pawn.HostileTo is an extension method on Thing in RimWorld.GenHostility;
        /// there is no Pawn.HostileTo member. It throws while hostiles are being
        /// de-spawned, which is why this is caught rather than trusted.
        /// </summary>
        private static int CountHostiles(Map map)
        {
            try
            {
                return map.mapPawns.AllPawnsSpawned
                    .Count(p => p != null && !p.Destroyed && p.Spawned && p.HostileTo(Faction.OfPlayer));
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Progress of the current research project, 0..1, or 0 if there is none.
        /// </summary>
        /// <remarks>
        /// Read by reflection because currentProj is private. Against the
        /// reference assembly this builds (Krafs.Rimworld.Ref 1.6) the field is
        /// the only way in: ResearchManager exposes SetCurrentProject and
        /// IsCurrentProject but no CurrentProject property, so the property
        /// fallback below never fires here. It is kept because the field is
        /// private and may be renamed in a later version, where a property is
        /// the likelier shape.
        ///
        /// Note the field's type is Verse.ResearchProjectDef. There is no
        /// RimWorld.ResearchProjectDef; asking for one finds nothing.
        /// </remarks>
        private static float GetResearchProgress()
        {
            try
            {
                var researchManager = Find.ResearchManager;
                if (researchManager == null) return 0f;

                var field = typeof(ResearchManager).GetField("currentProj",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null)
                {
                    var prop = typeof(ResearchManager).GetProperty("CurrentProject");
                    if (prop != null)
                    {
                        var proj = prop.GetValue(researchManager) as ResearchProjectDef;
                        return proj?.ProgressPercent ?? 0f;
                    }
                    return 0f;
                }

                var currentProj = field.GetValue(researchManager) as ResearchProjectDef;
                return currentProj?.ProgressPercent ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }
    }
}