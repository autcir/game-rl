// Extract resource state from RimWorld

using System.Collections.Generic;
using Verse;
using RimWorld;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// Resource state for observations
    ///
    /// Every count here comes from <c>ListerThings.ThingsOfDef(...).Count</c> or
    /// <c>ResourceCounter.GetCount</c>, both of which return a collection size or
    /// a sum of stack counts. Neither can be negative, and there is no code path
    /// that subtracts into them, so no member of this class is ever a sentinel.
    ///
    /// Worth stating because a consumer reading these as scalars sees
    /// <c>Stockpiles=-1</c> and reaches for a fix here. The negative number is
    /// manufactured downstream, by asking <c>as_f64()</c> of what serialises as a
    /// JSON object. Both dictionaries reach the wire as objects and have to be
    /// read as such.
    /// </summary>
    public class ResourceState
    {
        /// <summary>
        /// Counts of resources present on the map.
        ///
        /// These are counted by walking the map, not by reading
        /// <c>Map.resourceCounter</c>, which only counts what has been
        /// stockpiled. Loose items are exactly the ones an agent needs to know
        /// about: five logs lying on the ground read as zero stock, and a
        /// colony eating unpackaged meals read as starving.
        ///
        /// The name is historical, and reads backwards against
        /// <see cref="Stockpiled"/>: the noun holds every instance on the map,
        /// the participle holds only what is inside a stockpile zone. Both are
        /// emitted as JSON objects keyed by defName, never as bare numbers.
        /// </summary>
        public Dictionary<string, int> Stockpiles { get; set; } = new();

        /// <summary>
        /// How much of the above is actually in a stockpile, keyed by defName.
        /// </summary>
        public Dictionary<string, int> Stockpiled { get; set; } = new();

        public int Silver { get; set; }

        public float TotalWealth { get; set; }

        /// <summary>Days of human-edible food present on the map.</summary>
        public int FoodDays { get; set; }

        public int MedicineCount { get; set; }
    }

    /// <summary>
    /// Extracts resource information from the game
    /// </summary>
    public static class ResourceExtractor
    {
        private static readonly ThingDef[] ImportantDefs =
        {
            ThingDefOf.Steel,
            ThingDefOf.WoodLog,
            ThingDefOf.Plasteel,
            ThingDefOf.ComponentIndustrial,
            ThingDefOf.ComponentSpacer,
            ThingDefOf.Gold,
            ThingDefOf.Uranium,
            ThingDefOf.Chemfuel
        };

        private static readonly ThingDef[] MedicineDefs =
        {
            ThingDefOf.MedicineHerbal,
            ThingDefOf.MedicineIndustrial,
            ThingDefOf.MedicineUltratech
        };

        /// <summary>
        /// Count every instance of a def on the map, stockpiled or not.
        /// </summary>
        private static int CountOnMap(Map map, ThingDef def)
        {
            var things = map.listerThings.ThingsOfDef(def);
            return things == null ? 0 : things.Count;
        }

        /// <summary>
        /// Food defs counted for the food-days estimate.
        ///
        /// Spelled out rather than looked up because the reference assembly the
        /// mod builds against does not expose ThingGroupDefOf, StatDefsOf or the
        /// nutrition API. Add a def here when a new food source matters.
        /// </summary>
        private static readonly string[] FoodDefNames =
        {
            "MealSurvivalPack", "MealNutrientPaste", "MealSimple",
            "Pemmican", "Ratatouille", "SalvagedMeal", "Chocolate",
            "Cake", "Pie", "Sushi", "Pizza", "Burger"
        };

        /// <summary>
        /// Days of food on the map, one meal item per colonist per day.
        ///
        /// An approximation, stated rather than hidden: a survival meal is worth
        /// more than a cake and none of the nutrition API needed to express that
        /// is available to this build. A figure that moves with reality is worth
        /// more here than an exact number read through no API.
        /// </summary>
        private static int ComputeFoodDays(Map map)
        {
            int meals = 0;
            foreach (var name in FoodDefNames)
            {
                var def = DefDatabase<ThingDef>.GetNamed(name, errorOnFail: false);
                if (def != null)
                    meals += CountOnMap(map, def);
            }

            int colonistCount = map.mapPawns.FreeColonistsCount;
            return colonistCount > 0 ? meals / colonistCount : 0;
        }

        public static ResourceState Extract(Map? map)
        {
            if (map == null)
                return new ResourceState();

            var onMap = new Dictionary<string, int>();
            var stockpiled = new Dictionary<string, int>();

            foreach (var def in ImportantDefs)
            {
                if (def == null)
                    continue;
                onMap[def.defName] = CountOnMap(map, def);
                stockpiled[def.defName] = map.resourceCounter.GetCount(def);
            }

            var foodDays = ComputeFoodDays(map);

            var medicineCount = 0;
            foreach (var def in MedicineDefs)
            {
                if (def != null)
                    medicineCount += CountOnMap(map, def);
            }

            return new ResourceState
            {
                Stockpiles = onMap,
                Stockpiled = stockpiled,
                Silver = CountOnMap(map, ThingDefOf.Silver),
                TotalWealth = map.wealthWatcher.WealthTotal,
                FoodDays = foodDays,
                MedicineCount = medicineCount
            };
        }

    }
}
