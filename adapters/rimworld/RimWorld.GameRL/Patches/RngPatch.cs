// Harmony patch for deterministic RNG seeding

using HarmonyLib;
using Verse;

namespace RimWorld.GameRL.Patches
{
    /// <summary>
    /// Manages RNG seeding for deterministic episodes
    /// </summary>
    public static class RngManager
    {
        /// <summary>
        /// Seed to apply on next RNG initialization
        /// </summary>
        public static ulong? PendingSeed { get; set; }

        /// <summary>
        /// Whether a forced seed is active
        /// </summary>
        public static bool HasPendingSeed => PendingSeed.HasValue;

        /// <summary>
        /// Consume the pending seed and return it
        /// </summary>
        public static int ConsumeSeed()
        {
            if (!PendingSeed.HasValue)
                return 0;

            var seed = (int)(PendingSeed.Value & 0x7FFFFFFF);
            PendingSeed = null;
            Log.Message($"[GameRL] Consumed RNG seed: {seed}");
            return seed;
        }

        /// <summary>
        /// Apply a seed to the game's single global RNG stream, and consume it.
        ///
        /// Goes through PendingSeed on purpose. While a seed is pending the
        /// Rand.Seed setter prefix overwrites whatever it is given, so consuming
        /// here is what makes the assignment below reach Rand unchanged instead
        /// of being replaced. PendingSeed is null again on return: a seed that
        /// stays pending has not been applied, it has only been queued.
        ///
        /// An int is the widest seed the API accepts, so the high 32 bits of a
        /// 64-bit wire seed cannot be honoured and are dropped.
        /// </summary>
        /// <returns>The value actually written to Rand.Seed</returns>
        public static int ApplySeed(ulong seed)
        {
            PendingSeed = seed;
            var value = ConsumeSeed();
            Rand.Seed = value;
            return value;
        }
    }

    /// <summary>
    /// Patch Rand.Seed setter to apply forced seeds
    /// </summary>
    [HarmonyPatch(typeof(Rand), nameof(Rand.Seed), MethodType.Setter)]
    public static class RandSeedPatch
    {
        static void Prefix(ref int value)
        {
            if (RngManager.HasPendingSeed)
            {
                value = RngManager.ConsumeSeed();
            }
        }
    }

    /// <summary>
    /// Patch game initialization to apply seed
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
    public static class GameInitPatch
    {
        static void Prefix()
        {
            if (RngManager.HasPendingSeed)
            {
                Log.Message("[GameRL] Applying seed before game initialization");
            }
        }
    }
}
