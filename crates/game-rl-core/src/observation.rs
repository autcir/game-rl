//! Observation types

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

use crate::agent::AgentId;
use crate::reward::RewardComponents;

/// Result of a simulation step
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct StepResult {
    /// Agent that took the action
    pub agent_id: AgentId,

    /// Global step counter
    pub step_id: u64,

    /// Current simulation tick
    pub tick: u64,

    /// Agent-specific observation
    pub observation: Observation,

    /// Scalar reward signal
    pub reward: f64,

    /// Decomposed reward for analysis
    #[serde(default)]
    pub reward_components: RewardComponents,

    /// Episode terminated (goal reached or failed)
    pub done: bool,

    /// Episode truncated (time limit)
    pub truncated: bool,

    /// Why episode ended
    #[serde(skip_serializing_if = "Option::is_none")]
    pub termination_reason: Option<TerminationReason>,

    /// Notable events this step
    #[serde(default)]
    pub events: Vec<GameEvent>,

    /// Vision stream frame references
    #[serde(default)]
    pub frame_ids: HashMap<String, u64>,

    /// Valid actions for next step (if dynamic action space)
    #[serde(skip_serializing_if = "Option::is_none")]
    pub available_actions: Option<Vec<serde_json::Value>>,

    /// Performance metrics
    #[serde(skip_serializing_if = "Option::is_none")]
    pub metrics: Option<StepMetrics>,

    /// Determinism verification hash
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state_hash: Option<String>,
}

/// Agent observation (game-specific contents)
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(untagged)]
pub enum Observation {
    /// Structured observation
    Structured(HashMap<String, serde_json::Value>),
    /// Raw vector observation
    Vector(Vec<f64>),
    /// Custom observation format
    Custom(serde_json::Value),
}

/// How a reset should produce its world.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum ResetMode {
    /// Load a known-good template save, then apply the seed to the RNG.
    ///
    /// The default, so an older client that sends no `Mode` keeps working. Note the
    /// limit this carries: in this mode the world comes from the template file, so
    /// the seed governs what happens *after* the load, not which world is loaded.
    /// Two resets at different seeds therefore yield the same colony.
    #[default]
    Fast,
    /// Generate a new colony from the seed. The only mode where the seed selects
    /// the world.
    Fresh,
}

/// One postcondition the adapter observed after a reset.
///
/// `ok` must be the result of reading a value. `observed` carries what was read,
/// so a failed check is diagnosable without re-running it.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ResetCheck {
    pub name: String,
    pub ok: bool,
    pub observed: String,
}

/// What a reset actually did.
///
/// A reset that returns successfully has not thereby demonstrated that it reset
/// anything: the adapter reports the postconditions it observed, and the caller
/// decides. Keeping the checks separate from the success flag is the point --
/// `reset` returning `Ok` is the dispatcher not throwing.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ResetOutcome {
    pub observation: Observation,
    #[serde(default)]
    pub state_hash: Option<String>,
    pub mode: ResetMode,
    #[serde(default)]
    pub seed: Option<u64>,
    /// Whether the seed was applied, as opposed to left pending.
    #[serde(default)]
    pub seed_consumed: bool,
    #[serde(default)]
    pub postconditions: Vec<ResetCheck>,
}

impl ResetOutcome {
    /// `Some(true)` only if the adapter reported postconditions and all passed.
    ///
    /// `None` when it reported none. An adapter that reports nothing has not
    /// demonstrated it can reset, so this is deliberately not a success: a gate
    /// reading this must treat `None` as unproven rather than as a pass.
    pub fn verified(&self) -> Option<bool> {
        if self.postconditions.is_empty() {
            None
        } else {
            Some(self.postconditions.iter().all(|c| c.ok))
        }
    }

    /// The postconditions that failed, named.
    pub fn failed_checks(&self) -> Vec<&str> {
        self.postconditions
            .iter()
            .filter(|c| !c.ok)
            .map(|c| c.name.as_str())
            .collect()
    }
}

/// Why an episode ended
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "PascalCase")]
pub enum TerminationReason {
    Success,
    Failure,
    Timeout,
    External,
}

/// Game event that occurred during a step
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct GameEvent {
    /// Event type identifier
    #[serde(rename = "Type")]
    pub event_type: String,

    /// Tick when event occurred
    pub tick: u64,

    /// Severity level (0-3)
    #[serde(default)]
    pub severity: u8,

    /// Event-specific details
    #[serde(default)]
    pub details: serde_json::Value,
}

/// Performance metrics for a step
///
/// The split matters more than the total. A slow environment and a slow bridge
/// look identical from the outside, and they call for opposite responses: the
/// first means port the simulation, the second means fix the adapter.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct StepMetrics {
    /// Total step time in milliseconds
    pub step_ms: f64,

    /// GPU time in milliseconds
    #[serde(skip_serializing_if = "Option::is_none")]
    pub gpu_ms: Option<f64>,

    /// Whether a vision frame was dropped
    #[serde(default)]
    pub frame_dropped: bool,

    /// Pure simulation: time inside the forced tick loop
    #[serde(default)]
    pub tick_ms: f64,

    /// Action execution, excluding the tick loop
    #[serde(default)]
    pub execute_ms: f64,

    /// State extraction
    #[serde(default)]
    pub extract_ms: f64,

    /// Reward computation
    #[serde(default)]
    pub reward_ms: f64,

    /// Vision frame capture, when a stream is configured
    #[serde(default)]
    pub capture_ms: f64,
}
