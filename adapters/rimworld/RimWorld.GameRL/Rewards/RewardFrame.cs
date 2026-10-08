// One step's reward: the components, and the total that is their sum.
//
// The total is not computed by whoever needs it. It is computed here, once,
// from the same dictionary that goes on the wire, and the dictionary is not
// modifiable from outside. A caller cannot pair this total with a different set
// of components, which is the whole point: the harness checks
// Reward == sum(RewardComponents) every step, and the only way to satisfy that
// by construction rather than by luck is for both numbers to come out of one
// immutable object.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace RimWorld.GameRL.Rewards
{
    /// <summary>
    /// A reward and its parts, from one computation.
    ///
    /// Immutable. The component dictionary is only reachable read-only, and the
    /// total is the sum of exactly that dictionary taken in ordinal key order,
    /// so the same components always produce the same total regardless of the
    /// order they were computed in.
    /// </summary>
    public sealed class RewardFrame
    {
        private readonly Dictionary<string, double> _components;
        private readonly ReadOnlyDictionary<string, double> _readOnly;

        /// <summary>The components, as they go on the wire.</summary>
        public IReadOnlyDictionary<string, double> Components => _readOnly;

        /// <summary>
        /// The sum of <see cref="Components"/>. Always finite, because every
        /// component that reached this object was finite.
        /// </summary>
        public double Total { get; }

        /// <summary>True when nothing was rewarded or penalised this step.</summary>
        public bool IsEmpty => _components.Count == 0;

        /// <summary>
        /// Components that were computed as NaN or infinity and dropped.
        ///
        /// Non-empty means the game produced a value no JSON encoder can write:
        /// serde_json serialises a non-finite f64 as null, and a null component
        /// reads on the other side as a missing reward rather than as the broken
        /// reading that caused it. Dropped rather than zeroed because a zero
        /// would be a claim — "this term contributed nothing" — when the truth
        /// is "this term could not be measured".
        /// </summary>
        public IReadOnlyList<string> DroppedComponents { get; }

        private RewardFrame(
            Dictionary<string, double> components,
            double total,
            IReadOnlyList<string> dropped)
        {
            _components = components;
            _readOnly = new ReadOnlyDictionary<string, double>(components);
            Total = total;
            DroppedComponents = dropped;
        }

        /// <summary>
        /// A frame with no components, for a step that could not be measured.
        /// Total is 0, which is the sum of nothing.
        /// </summary>
        public static RewardFrame Empty()
        {
            return new RewardFrame(
                new Dictionary<string, double>(),
                0.0,
                Array.Empty<string>());
        }

        /// <summary>
        /// Build a frame from computed components.
        ///
        /// The only way to make one: everything that reports a reward goes
        /// through here, so the sanitising and the summing cannot be skipped.
        /// </summary>
        public static RewardFrame FromComponents(Dictionary<string, double> components)
        {
            var clean = new Dictionary<string, double>(components.Count);
            var dropped = new List<string>();

            foreach (var entry in components)
            {
                if (double.IsNaN(entry.Value) || double.IsInfinity(entry.Value))
                {
                    dropped.Add(entry.Key);
                    continue;
                }

                clean[entry.Key] = entry.Value;
            }

            return new RewardFrame(clean, Sum(clean), dropped);
        }

        /// <summary>
        /// A copy of the components, for handing to the wire.
        ///
        /// A copy rather than the dictionary itself, because the wire type is a
        /// mutable Dictionary and a caller holding that could add to it after
        /// the total was taken. This object cannot be made to disagree with
        /// itself; a copy it hands out cannot be changed under it.
        /// </summary>
        public Dictionary<string, double> CopyComponents()
        {
            return new Dictionary<string, double>(_components);
        }

        /// <summary>
        /// Sum in ordinal key order, so the total is a function of the component
        /// set and not of the order the components were added in.
        /// </summary>
        private static double Sum(Dictionary<string, double> components)
        {
            var keys = new List<string>(components.Keys);
            keys.Sort(StringComparer.Ordinal);

            var total = 0.0;
            foreach (var key in keys)
            {
                total += components[key];
            }

            // Every component here is finite and every component is derived from
            // an int count times a literal scale, so the magnitudes involved top
            // out around 1e11 and this cannot overflow. It is here so that a
            // future component with a larger scale fails as a zero reward with a
            // dropped-components report rather than as a Reward that is not a
            // JSON number.
            if (double.IsNaN(total) || double.IsInfinity(total))
            {
                return 0.0;
            }

            return total;
        }
    }
}