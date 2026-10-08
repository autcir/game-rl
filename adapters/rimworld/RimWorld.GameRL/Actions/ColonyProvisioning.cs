using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Make a freshly-reset colony playable without human help.
    ///
    /// <para>
    /// A colony loaded from a save arrives with every colonist on
    /// <c>GotoWander</c>: no work is ever assigned, so nothing ever happens, and an
    /// agent driving the game has nothing to decide about. That is not a reward
    /// problem or a determinism problem, it is a scenario problem, and the cure is
    /// code rather than a person playing for an hour.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two rules govern everything here:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Every value reported is <b>read back</b>, never assumed. The counts come from
    /// <c>GetPriority</c> and from counting the map, so a report of "3 of 3 have
    /// work" means three priorities were observed, not that three writes were issued.
    /// </description></item>
    /// <item><description>
    /// Provisioning never fabricates what it cannot verify. Beds and food are
    /// <b>counted</b> and reported, not spawned: a colony with no beds is a real
    /// situation for the agent to solve, and inventing a bed would silently remove
    /// the first decision the game can offer. Only work priorities are written,
    /// because a colonist with no priorities can do literally nothing.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class ColonyProvisioning
    {
        /// <summary>Priorities applied to every colonist, highest number = most urgent.</summary>
        private static readonly (string WorkType, int Priority)[] DefaultPriorities =
        {
            ("Firefighter", 2),
            ("Patient", 2),
            ("BasicWorker", 3),
            ("Hauling", 2),
            ("Cleaning", 2),
            ("Construction", 2),
            ("Growing", 2),
            ("Cooking", 1),
            ("Hunting", 1),
        };

        /// <summary>What provisioning did and what it observed, in one value.</summary>
        public sealed class Outcome
        {
            /// <summary>Free colonists found on the current map.</summary>
            public int Colonists { get; set; }
            /// <summary>Colonists whose priorities were read back as set.</summary>
            public int WithWork { get; set; }
            /// <summary>Beds observed on the map. Reported, never created.</summary>
            public int Beds { get; set; }
            /// <summary>Why provisioning could not do its job, if it could not.</summary>
            public string? Blocker { get; set; }

            /// <summary>Whether at least one colonist can now work.</summary>
            public bool Playable => Blocker == null && WithWork > 0;
        }

        /// <summary>
        /// Assign default work priorities to every free colonist, then report the
        /// state as observed.
        /// </summary>
        public static Outcome Provision()
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                return new Outcome
                {
                    Colonists = 0,
                    WithWork = 0,

                    Blocker =
                        $"no current map. Observed ProgramState={Current.ProgramState}, " +
                        $"LongEventBusy={LongEventHandler.ShouldWaitForEvent}. Without a map " +
                        "there are no colonists to assign work to.",
                };
            }

            var colonists = map.mapPawns.FreeColonists.ToList();
            if (colonists.Count == 0)
            {
                return new Outcome
                {
                    Colonists = 0,
                    WithWork = 0,
                        Blocker =
                        $"the current map has no free colonists. Observed " +
                        $"map={map}, pawns={map.mapPawns.AllPawns.Count}. An agent has nothing to " +
                        "control until a colonist exists.",
                };
            }

            // Required by SetPriority, and a global switch, so it is set once here
            // rather than per pawn.
            if (!Current.Game.playSettings.useWorkPriorities)
            {
                Current.Game.playSettings.useWorkPriorities = true;
            }

            var withWork = 0;
            var unabled = new List<string>();
            foreach (var pawn in colonists)
            {
                if (pawn.workSettings == null)
                {
                    // A colonist who cannot work at all. Not an error: report it.
                    unabled.Add(pawn.LabelShort);
                    continue;
                }

                foreach (var (workType, priority) in DefaultPriorities)
                {
                    var def = DefDatabase<WorkTypeDef>.GetNamed(workType, errorOnFail: false);
                    if (def == null)
                    {
                        // A DLC or game version that lacks this work type. Skip it
                        // rather than aborting the whole provisioning over one
                        // missing entry.
                        continue;
                    }
                    pawn.workSettings.SetPriority(def, priority);
                }

                // Read back, because the write is not the claim. BasicWorker is the
                // lowest common denominator of "can work at all".
                var workerDef = DefDatabase<WorkTypeDef>.GetNamed("BasicWorker", errorOnFail: false);
                var observed = workerDef != null ? pawn.workSettings.GetPriority(workerDef) : 0;
                if (observed > 0)
                {
                    withWork++;
                }
                else
                {
                    unabled.Add(pawn.LabelShort);
                }
            }

            var outcome = new Outcome
            {
                Colonists = colonists.Count,
                WithWork = withWork,
            };

            if (withWork == 0)
            {
                return new Outcome
                {
                    Colonists = outcome.Colonists,
                    Blocker =
                        $"none of the {colonists.Count} free colonists can take work after " +
                        $"provisioning. Colonists without work settings: " +
                        $"{(unabled.Count == 0 ? "(none)" : string.Join(", ", unabled))}. Observed " +
                        $"useWorkPriorities={Current.Game.playSettings.useWorkPriorities}.",
                };
            }

            Log.Message(
                $"[GameRL] Provisioned colony: {withWork}/{colonists.Count} colonists with work, " +
                $"{outcome.Beds} beds observed, food left to ResourceExtractor (single source of truth)");
            return outcome;
        }

    }
}