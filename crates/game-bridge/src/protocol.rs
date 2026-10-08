//! Wire protocol for Rust <-> game communication
//!
//! Messages are serialized as JSON with internally-tagged enums.
//! Format: {"Type": "MessageType", ...fields}
//!
//! Uses PascalCase throughout for LLM-friendly natural language readability.

use game_rl_core::{
    Action, AgentConfig, AgentId, AgentType, GameEvent, Observation, ResetCheck, ResetMode,
    StepMetrics, StreamDescriptor,
};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// Step result payload for single-agent or batch responses
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct StepResultPayload {
    pub agent_id: AgentId,
    pub observation: Observation,
    pub reward: f64,
    #[serde(default)]
    pub reward_components: HashMap<String, f64>,
    pub done: bool,
    pub truncated: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state_hash: Option<String>,
    /// Per-phase timings, so a slow simulation can be told apart from a slow bridge.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub metrics: Option<StepMetrics>,
}

/// The game tick an adapter reported in an observation, if it reported one.
///
/// The tick is not a field on `StepResultPayload`: adapters put it at the top
/// level of the observation object they return -- `RimWorldStateExtractor`
/// sets `Tick` from `Find.TickManager.TicksGame`, the Zomboid
/// `StateExtractor` sets `Tick` from the world age, and the Factorio observer
/// writes a lowercase `tick`. Both spellings are accepted so a bridge does not
/// have to know which adapter it is talking to.
///
/// `None` means the adapter sent no tick: a vector observation, a per-agent map
/// of observations, or an object with no tick key. That is "unmeasured", which
/// is not the same as tick zero, so callers must not silently substitute a
/// counter for it.
pub fn observation_tick(observation: &Observation) -> Option<u64> {
    let tick = match observation {
        Observation::Structured(fields) => fields.get("Tick").or_else(|| fields.get("tick")),
        Observation::Custom(value) => value
            .as_object()
            .and_then(|fields| fields.get("Tick").or_else(|| fields.get("tick"))),
        // A vector observation has no field names, so there is nothing to read.
        Observation::Vector(_) => None,
    }?;

    // Lua numbers arrive as doubles, so an integral float is still a tick; a
    // fractional or negative one is not a tick at all.
    tick.as_u64().or_else(|| match tick.as_f64() {
        Some(value) if value >= 0.0 && value.fract() == 0.0 => Some(value as u64),
        _ => None,
    })
}

/// Messages sent between Rust bridge and game process
///
/// Note: `rename_all` on enums only affects variant names, not field names inside variants.
/// Each field must be explicitly renamed using `#[serde(rename = "...")]` for PascalCase.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "Type", rename_all = "PascalCase")]
pub enum GameMessage {
    // === Game -> Rust ===
    /// Game is ready and provides manifest
    Ready {
        #[serde(rename = "Name")]
        name: String,
        #[serde(rename = "Version")]
        version: String,
        #[serde(rename = "Capabilities")]
        capabilities: GameCapabilities,
    },

    /// Current game state update
    StateUpdate {
        #[serde(rename = "Tick")]
        tick: u64,
        #[serde(rename = "State")]
        state: serde_json::Value,
        #[serde(rename = "Events")]
        events: Vec<GameEvent>,
    },

    /// Agent registration response
    AgentRegistered {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
        #[serde(rename = "ObservationSpace")]
        observation_space: serde_json::Value,
        #[serde(rename = "ActionSpace")]
        action_space: serde_json::Value,
    },

    /// Step result
    StepResult {
        #[serde(flatten)]
        result: StepResultPayload,
    },

    /// Batch step results for multiple agents
    BatchStepResult {
        #[serde(rename = "Results")]
        results: Vec<StepResultPayload>,
    },

    /// Reset complete
    #[allow(clippy::too_many_arguments)]
    ResetComplete {
        #[serde(rename = "Observation")]
        observation: Observation,
        #[serde(rename = "StateHash")]
        state_hash: Option<String>,
        /// Which mode the adapter actually ran.
        #[serde(rename = "Mode", default)]
        mode: ResetMode,
        /// The seed the reset was asked to apply.
        #[serde(rename = "Seed", default)]
        seed: Option<u64>,
        /// Whether the seed was applied, not left pending.
        #[serde(rename = "SeedConsumed", default)]
        seed_consumed: bool,
        /// Postconditions the adapter observed. Empty means it reported none, which
        /// is not a pass -- see `ResetOutcome::verified`.
        #[serde(rename = "Postconditions", default)]
        postconditions: Vec<ResetCheck>,
    },

    /// State hash response
    StateHash {
        #[serde(rename = "Hash")]
        hash: String,
    },

    /// The real action contract, from the adapter's own reflection
    ActionsListed {
        #[serde(rename = "Actions")]
        actions: Vec<serde_json::Value>,
    },

    /// Vision streams configured
    StreamsConfigured {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
        #[serde(rename = "Descriptors")]
        descriptors: Vec<StreamDescriptor>,
    },

    /// Error response
    Error {
        #[serde(rename = "Code")]
        code: i32,
        #[serde(rename = "Message")]
        message: String,
    },

    // === Rust -> Game ===
    /// Register an agent
    RegisterAgent {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
        #[serde(rename = "AgentType")]
        agent_type: AgentType,
        #[serde(rename = "Config")]
        config: AgentConfig,
    },

    /// Deregister an agent
    DeregisterAgent {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
    },

    /// Execute an action
    ExecuteAction {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
        #[serde(rename = "Action")]
        action: Action,
        #[serde(rename = "Ticks")]
        ticks: u32,
    },

    /// Reset environment
    Reset {
        #[serde(rename = "Seed")]
        seed: Option<u64>,
        #[serde(rename = "Scenario")]
        scenario: Option<String>,
        /// Absent means `Fast`; see `ResetMode`.
        #[serde(rename = "Mode", default)]
        mode: ResetMode,
    },

    /// Request state hash
    GetStateHash,

    /// Request the action catalogue with real parameter names and types
    ListActions,

    /// Configure vision streams
    ConfigureStreams {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
        #[serde(rename = "Profile")]
        profile: String,
    },

    /// Shutdown the game
    Shutdown,
}

/// Game capabilities sent during Ready
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct GameCapabilities {
    pub multi_agent: bool,
    pub max_agents: usize,
    pub deterministic: bool,
    pub headless: bool,
}

/// Serialize a message to JSON bytes
pub fn serialize(msg: &GameMessage) -> Result<Vec<u8>, serde_json::Error> {
    serde_json::to_vec(msg)
}

/// Deserialize a message from JSON bytes
pub fn deserialize(bytes: &[u8]) -> Result<GameMessage, serde_json::Error> {
    serde_json::from_slice(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_roundtrip() {
        let msg = GameMessage::Ready {
            name: "TestGame".into(),
            version: "1.0.0".into(),
            capabilities: GameCapabilities {
                multi_agent: true,
                max_agents: 4,
                deterministic: true,
                headless: true,
            },
        };

        let bytes = serialize(&msg).unwrap();
        let decoded: GameMessage = deserialize(&bytes).unwrap();

        match decoded {
            GameMessage::Ready { name, .. } => assert_eq!(name, "TestGame"),
            _ => panic!("Wrong message type"),
        }
    }

    #[test]
    fn test_ready_from_game() {
        // Exact JSON format expected from games
        let json = r#"{"Type":"Ready","Name":"ProjectZomboid","Version":"41.78","Capabilities":{"MultiAgent":true,"MaxAgents":8,"Deterministic":false,"Headless":false}}"#;

        let result: Result<GameMessage, _> = serde_json::from_str(json);
        match result {
            Ok(msg) => match msg {
                GameMessage::Ready {
                    name,
                    version,
                    capabilities,
                } => {
                    assert_eq!(name, "ProjectZomboid");
                    assert_eq!(version, "41.78");
                    assert!(capabilities.multi_agent);
                    assert_eq!(capabilities.max_agents, 8);
                    assert!(!capabilities.deterministic);
                }
                _ => panic!("Wrong message type"),
            },
            Err(e) => panic!("Deserialization failed: {}", e),
        }
    }

    #[test]
    fn test_get_state_hash_format() {
        let msg = GameMessage::GetStateHash;
        let bytes = serialize(&msg).unwrap();
        let json = String::from_utf8_lossy(&bytes);

        assert!(json.contains("\"Type\":\"GetStateHash\""));

        let decoded: GameMessage = deserialize(&bytes).unwrap();
        match decoded {
            GameMessage::GetStateHash => {}
            _ => panic!("Wrong message type"),
        }
    }

    #[test]
    fn test_observation_tick_reads_the_rimworld_wire_shape() {
        // Byte-for-byte the shape `Bridge.SerializeStepResult` emits for
        // `RimWorldObservation`, whose `Tick` is `TickManager.TicksGame`. If the
        // adapter ever stops sending it, this fails instead of the bridge
        // quietly reporting zero.
        let json = r#"{"Type":"StepResult","AgentId":"colonist-1","Observation":{"Tick":48213,"ColonistCount":3},"Reward":0.0,"RewardComponents":{},"Done":false,"Truncated":false,"StateHash":"ab12"}"#;

        let msg: GameMessage = serde_json::from_str(json).unwrap();
        match msg {
            GameMessage::StepResult { result } => {
                assert_eq!(observation_tick(&result.observation), Some(48213));
            }
            _ => panic!("Wrong message type"),
        }
    }

    #[test]
    fn test_observation_tick_accepts_every_adapter_spelling() {
        let structured = |tick: serde_json::Value| {
            Observation::Structured(
                [(String::from("tick"), tick)]
                    .into_iter()
                    .collect::<HashMap<_, _>>(),
            )
        };

        // PascalCase (RimWorld, Zomboid) and lowercase (Factorio observer).
        assert_eq!(
            observation_tick(&Observation::Structured(
                [(String::from("Tick"), serde_json::json!(60))]
                    .into_iter()
                    .collect()
            )),
            Some(60)
        );
        assert_eq!(
            observation_tick(&structured(serde_json::json!(60))),
            Some(60)
        );
        assert_eq!(
            observation_tick(&Observation::Custom(serde_json::json!({"Tick": 7}))),
            Some(7)
        );
        // Lua sends doubles; an integral one is still a tick.
        assert_eq!(
            observation_tick(&structured(serde_json::json!(43200.0))),
            Some(43200)
        );
    }

    #[test]
    fn test_observation_tick_is_none_when_unmeasured() {
        // No tick key: the caller must be able to tell this from tick zero.
        assert_eq!(
            observation_tick(&Observation::Structured(HashMap::new())),
            None
        );
        // Wrong shape for a tick: a string, a fraction, a negative number.
        assert_eq!(
            observation_tick(&Observation::Structured(
                [(String::from("Tick"), serde_json::json!("12"))]
                    .into_iter()
                    .collect()
            )),
            None
        );
        assert_eq!(
            observation_tick(&Observation::Structured(
                [(String::from("Tick"), serde_json::json!(1.5))]
                    .into_iter()
                    .collect()
            )),
            None
        );
        assert_eq!(
            observation_tick(&Observation::Structured(
                [(String::from("Tick"), serde_json::json!(-1))]
                    .into_iter()
                    .collect()
            )),
            None
        );
        // A vector observation carries no field names.
        assert_eq!(observation_tick(&Observation::Vector(vec![1.0])), None);
    }
}
