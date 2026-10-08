// Reset modes and the result of a reset attempt

using System;

namespace GameRL.Harmony
{
    /// <summary>
    /// How a reset produces a world.
    /// </summary>
    public enum ResetMode
    {
        /// <summary>
        /// Load a known-good save named by GAMERL_RESET_TEMPLATE. Reproducible,
        /// but the loaded world does not depend on the seed: two resets with
        /// different seeds load the same world.
        /// </summary>
        Fast,

        /// <summary>
        /// Generate a world, a map and a colonist roster from the seed, so two
        /// resets with different seeds produce different colonies.
        /// </summary>
        Fresh
    }

    /// <summary>
    /// Wire names for <see cref="ResetMode"/>.
    ///
    /// The wire is a string, not an enum ordinal, so an unknown value is an
    /// error rather than a default. A reset silently downgraded to "load the
    /// template again" is the exact failure the environment contract exists to
    /// catch, and it must not be something a typo can produce.
    /// </summary>
    public static class ResetModes
    {
        public const string FastName = "Fast";
        public const string FreshName = "Fresh";

        public static string ToWireString(ResetMode mode)
        {
            if (mode == ResetMode.Fresh)
                return FreshName;
            return FastName;
        }

        /// <summary>
        /// Parse the wire value.
        ///
        /// Absent means Fast: the field did not exist when the first clients
        /// were written, and Fast is what they were getting.
        /// </summary>
        /// <exception cref="ArgumentException">The value is present and unknown.</exception>
        public static ResetMode Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return ResetMode.Fast;

            if (string.Equals(value, FastName, StringComparison.OrdinalIgnoreCase))
                return ResetMode.Fast;

            if (string.Equals(value, FreshName, StringComparison.OrdinalIgnoreCase))
                return ResetMode.Fresh;

            throw new ArgumentException(
                $"unknown reset mode '{value}', expected \"{FastName}\" or \"{FreshName}\"");
        }
    }

    /// <summary>
    /// What a reset actually did, as observed after it ran.
    ///
    /// Every field here is read back from the game, never assumed: the wire
    /// turns these into the reset postconditions, and an assumed value would
    /// certify an episode that never happened.
    /// </summary>
    /// <summary>
    /// What colony provisioning observed after the world was put in place.
    /// Every number here is read back from game state, never assumed from a write.
    /// </summary>
    public class ResetProvisioning
    {
        /// <summary>Free colonists on the current map.</summary>
        public int Colonists { get; set; }

        /// <summary>Colonists whose work priorities were read back as set.</summary>
        public int WithWork { get; set; }


        /// <summary>Nutrition-bearing food items observed. Reported, never created.</summary>
        public int Food { get; set; }
    }

    public class ResetOutcome
    {
        public ResetMode RequestedMode { get; set; }

        /// <summary>Seed as sent on the wire, echoed back.</summary>
        public ulong? Seed { get; set; }

        /// <summary>True only when the seed reached Rand, not when it was merely queued.</summary>
        /// <summary>Provisioning outcome, or null when it was not run.</summary>
        public ResetProvisioning? Provisioning { get; set; }

        public bool SeedConsumed { get; set; }

        public string SeedObserved { get; set; } = "";

        public bool HasMap { get; set; }

        public string MapObserved { get; set; } = "";

        public int ColonistCount { get; set; }
    }
}