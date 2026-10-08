// Interface for game-specific state extraction

using System.Collections.Generic;
using GameRL.Harmony.Protocol;

namespace GameRL.Harmony
{
    /// <summary>
    /// Interface for extracting game state into observations.
    /// Implement this in game-specific adapters.
    /// </summary>
    public interface IStateExtractor
    {
        /// <summary>
        /// Current game tick
        /// </summary>
        ulong CurrentTick { get; }

        /// <summary>
        /// Extract observation for a specific agent
        /// </summary>
        /// <param name="agentId">Agent requesting observation</param>
        /// <returns>Observation object (serialized as JSON on the IPC link)</returns>
        object ExtractObservation(string agentId);

        /// <summary>
        /// Compute state hash for determinism verification
        /// </summary>
        /// <remarks>
        /// Describes the world, not when it was sampled, and returns null when
        /// no digest could be computed. There is deliberately no sentinel
        /// string for failure: a failed extraction that looks like a digest by
        /// prefix is a failed extraction that reads as a valid state.
        /// </remarks>
        /// <returns>A "sha256:..." digest, or null when none could be produced</returns>
        string? ComputeStateHash();

        /// <summary>
        /// Collect events that occurred since last collection
        /// </summary>
        List<GameEvent> CollectEvents();

        /// <summary>
        /// Get observation space definition for an agent type
        /// </summary>
        object GetObservationSpace(string agentType);

        /// <summary>
        /// Get action space definition for an agent type
        /// </summary>
        object GetActionSpace(string agentType);
    }
}
