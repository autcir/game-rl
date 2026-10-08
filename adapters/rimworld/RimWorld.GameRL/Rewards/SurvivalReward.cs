// Survival-focused reward computation for RimWorld
//
// The reward is a function of two readings of the colony, and of nothing else.
// It holds no state, so it cannot disagree with itself, and it reads nothing
// but its arguments, so the answer cannot depend on when it was asked.

using System.Collections.Generic;

namespace RimWorld.GameRL.Rewards
{
    /// <summary>
    /// Computes reward components focused on colony survival.
    ///
    /// Stateless by construction. Both endpoints of every delta arrive as
    /// arguments — <see cref="RewardSnapshot"/> values captured by the caller —
    /// and no field is read or written, so:
    ///
    ///   * calling Compute twice with the same pair returns the same frame, and
    ///   * the total and the components come out of one
    ///     <see cref="RewardFrame"/>, so they cannot be two computations of the
    ///     same question.
    ///
    /// The previous version kept `_last*` fields, read the live world and
    /// updated those fields as it went. It was a delta function wearing the
    /// signature of a pure one, which is why calling it twice in a step
    /// produced a reward and a total from two different worlds.
    ///
    /// Deltas are between steps, not within a step. Which step is "previous" is
    /// the caller's business: the executor owns the per-episode baseline and
    /// resets it at the episode boundary.
    /// </summary>
    public sealed class SurvivalReward
    {
        /// <summary>
        /// PURE: (previous snapshot, current snapshot) -&gt; reward frame.
        ///
        /// Reads no instance field and writes none, and calls nothing that can
        /// see one: the game is reached only through the snapshots handed in.
        /// </summary>
        /// <param name="previous">The reading the last step was measured against.</param>
        /// <param name="current">The reading taken now.</param>
        public RewardFrame Compute(RewardSnapshot previous, RewardSnapshot current)
        {
            // No map is not a colony that lost everything. The old early return
            // is preserved deliberately: differencing against a mapless
            // baseline would book the missing map as a mass colonist death.
            if (!current.HasMap)
            {
                return RewardFrame.Empty();
            }

            var components = new Dictionary<string, double>();
            var colonistCount = current.ColonistCount;
            var wealth = current.Wealth;

            // ═══════════════════════════════════════════════════════════════
            // CRITICAL: Colonist survival (large magnitude)
            // ═══════════════════════════════════════════════════════════════
            var colonistDelta = colonistCount - previous.ColonistCount;
            if (colonistDelta < 0)
            {
                // Major penalty for deaths
                components["colonist_death"] = colonistDelta * 100.0;
            }
            else if (colonistDelta > 0)
            {
                // Bonus for recruiting new colonists
                components["colonist_recruited"] = colonistDelta * 10.0;
            }

            // ═══════════════════════════════════════════════════════════════
            // NEEDS: Hunger, mood, health (continuous signals)
            // ═══════════════════════════════════════════════════════════════
            if (colonistCount > 0)
            {
                // Mood: reward for happy colonists, penalty for unhappy
                components["mood"] = (current.MoodAverage - 0.5) * 0.2;

                // Hunger: penalty scales with severity
                if (current.AverageHunger > 0.3f)
                {
                    components["hunger"] = -current.AverageHunger * 0.3;
                }

                // Rest: penalty for exhausted colonists
                if (current.ExhaustedCount > 0)
                {
                    components["exhaustion"] = -current.ExhaustedCount * 0.1;
                }

                // Health: penalty for injuries
                if (current.AverageHealth < 0.9f)
                {
                    components["health"] = (current.AverageHealth - 1.0) * 0.2;
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // PRODUCTIVITY: Idle penalty, wealth growth
            // ═══════════════════════════════════════════════════════════════
            if (current.IdleCount > 0)
            {
                components["idle"] = -current.IdleCount * 0.05;
            }

            // Wealth progress (capped to avoid exploitation)
            var wealthDelta = wealth - previous.Wealth;
            var wealthReward = Clamp(wealthDelta / 5000.0, -0.5, 0.5);
            if (System.Math.Abs(wealthReward) > 0.001)
            {
                components["wealth"] = wealthReward;
            }

            // ═══════════════════════════════════════════════════════════════
            // FOOD SECURITY: Days of food stockpiled
            // ═══════════════════════════════════════════════════════════════
            var foodDays = current.FoodDays;
            var foodDelta = foodDays - previous.FoodDays;
            if (foodDelta != 0)
            {
                // Reward for building food reserves, penalty for losing them
                components["food_security"] = Clamp(foodDelta * 0.1, -0.5, 0.5);
            }
            // Continuous penalty if food is critically low
            if (foodDays < 2)
            {
                components["food_critical"] = -0.2;
            }

            // ═══════════════════════════════════════════════════════════════
            // THREATS: Bonus for eliminating hostiles
            // ═══════════════════════════════════════════════════════════════
            var hostileDelta = current.HostileCount - previous.HostileCount;
            if (hostileDelta < 0)
            {
                // Bonus for killing hostiles
                components["threat_eliminated"] = -hostileDelta * 0.5;
            }
            else if (hostileDelta > 0)
            {
                // Penalty for new threats appearing (raid started)
                components["threat_appeared"] = -hostileDelta * 0.1;
            }

            // ═══════════════════════════════════════════════════════════════
            // RESOURCES: Gathering wood, steel, etc.
            // ═══════════════════════════════════════════════════════════════
            var woodDelta = current.WoodCount - previous.WoodCount;
            var steelDelta = current.SteelCount - previous.SteelCount;

            if (woodDelta > 0)
            {
                components["wood_gathered"] = System.Math.Min(woodDelta * 0.002, 0.2);
            }
            if (steelDelta > 0)
            {
                components["steel_gathered"] = System.Math.Min(steelDelta * 0.003, 0.2);
            }

            // ═══════════════════════════════════════════════════════════════
            // RESEARCH: Progress on current project
            // ═══════════════════════════════════════════════════════════════
            var currentProgress = current.ResearchProgress;
            if (currentProgress > 0)
            {
                var progressDelta = currentProgress - previous.ResearchProgress;
                if (progressDelta > 0)
                {
                    components["research"] = progressDelta * 2.0;
                }
                // Bonus for completing research
                if (currentProgress >= 1.0f && previous.ResearchProgress < 1.0f)
                {
                    components["research_complete"] = 1.0;
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // CONSTRUCTION: Building new structures
            // ═══════════════════════════════════════════════════════════════
            var buildingDelta = current.BuildingCount - previous.BuildingCount;
            if (buildingDelta > 0)
            {
                components["construction"] = buildingDelta * 0.1;
            }

            return RewardFrame.FromComponents(components);
        }

        /// <summary>
        /// Clamp for net472.
        ///
        /// Not <c>Math.Clamp</c>. That overload is .NET Core 2.0 and later and is
        /// absent from the net472 targeting pack this mod builds against, so
        /// calling it only compiled because Krafs.Rimworld.Ref ships its own
        /// mscorlib that happens to include it. RimWorld's Unity Mono BCL does
        /// have it, so it would have worked at runtime — which is exactly what
        /// makes it worth removing: the reference package was quietly widening
        /// the BCL surface the code compiles against, and the widened surface
        /// was load-bearing for two reward components.
        ///
        /// Two-sided by construction: Math.Min(max, Math.Max(min, value)) is
        /// correct when min &lt;= max, and when min &gt; max it returns min
        /// rather than throwing, so a caller that passes the bounds in the
        /// wrong order gets a clamped-to-min value instead of an exception
        /// mid-reward. Every use here passes a literal lower and upper bound.
        /// </summary>
        private static double Clamp(double value, double min, double max)
        {
            return System.Math.Min(max, System.Math.Max(min, value));
        }
    }
}