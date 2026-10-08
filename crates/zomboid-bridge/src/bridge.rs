//! Bridge to Project Zomboid via file-based IPC
//!
//! Uses file system for communication since PZ's Lua is sandboxed
//! and doesn't have socket access.

use game_bridge::{GameCapabilities, GameMessage, StepResultPayload, observation_tick};
use game_rl_core::{
    Action, AgentConfig, AgentId, AgentManifest, AgentType, GameManifest, GameRLError, Observation,
    ResetMode, ResetOutcome, Result, StepResult, StreamDescriptor,
};
use game_rl_server::GameEnvironment;
use game_rl_server::environment::StateUpdate;
use std::collections::HashMap;
use std::path::PathBuf;
use std::time::Duration;
use tokio::fs;
use tokio::sync::broadcast;
use tokio::time::sleep;
use tracing::{debug, info, warn};

/// Configuration for Zomboid bridge
#[derive(Debug, Clone)]
pub struct ZomboidConfig {
    /// Base path for IPC files (default: ~/Zomboid/gamerl/)
    pub ipc_path: PathBuf,
    /// Timeout waiting for game response
    pub response_timeout: Duration,
    /// Poll interval for file changes
    pub poll_interval: Duration,
}

impl Default for ZomboidConfig {
    fn default() -> Self {
        let home = std::env::var("HOME")
            .or_else(|_| std::env::var("USERPROFILE"))
            .unwrap_or_else(|_| "/tmp".to_string());

        Self {
            // PZ Lua uses ~/Zomboid/Lua/ for file I/O
            ipc_path: PathBuf::from(home).join("Zomboid").join("Lua"),
            response_timeout: Duration::from_secs(30),
            poll_interval: Duration::from_millis(50),
        }
    }
}

/// Bridge to Project Zomboid via file-based IPC
pub struct ZomboidBridge {
    /// Connection configuration
    config: ZomboidConfig,
    /// Path to command file (Rust writes, Lua reads)
    command_file: PathBuf,
    /// Path to response file (Lua writes, Rust reads)
    response_file: PathBuf,
    /// Path to status file
    status_file: PathBuf,
    /// Whether connected
    connected: bool,
    /// Broadcast channel for pushed state updates
    event_tx: broadcast::Sender<StateUpdate>,
    /// Game capabilities received during Ready
    capabilities: Option<GameCapabilities>,
    /// Game name
    game_name: String,
    /// Game version
    game_version: String,
    /// Steps executed through this bridge since the last `reset`.
    ///
    /// Reported as `StepResult::step_id`. Counted here because a step id answers
    /// "which step of this episode is this", which is a property of the
    /// conversation with the game rather than of the game state -- and because
    /// the Lua mod has no such field to report.
    step_count: u64,
    /// Whether the "the game reported no tick" warning has already been logged
    /// for this episode, so a misreporting mod warns once instead of once per
    /// step.
    warned_no_tick: bool,
}

impl ZomboidBridge {
    /// Create a new bridge with default configuration
    pub fn new() -> Self {
        Self::with_config(ZomboidConfig::default())
    }

    /// Create a new bridge with custom configuration
    pub fn with_config(config: ZomboidConfig) -> Self {
        // Use flat files with gamerl_ prefix (PZ Lua can't read subdirectories)
        let command_file = config.ipc_path.join("gamerl_command.json");
        let response_file = config.ipc_path.join("gamerl_response.json");
        let status_file = config.ipc_path.join("gamerl_status.json");
        let (event_tx, _) = broadcast::channel(64);

        Self {
            config,
            command_file,
            response_file,
            status_file,
            connected: false,
            event_tx,
            capabilities: None,
            game_name: "Project Zomboid".into(),
            game_version: "0.0.0".into(),
            step_count: 0,
            warned_no_tick: false,
        }
    }

    /// Initialize IPC directory and wait for game to connect
    pub async fn init(&mut self) -> Result<()> {
        // Create IPC directory
        fs::create_dir_all(&self.config.ipc_path)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Failed to create IPC directory: {}", e)))?;

        info!("IPC directory: {:?}", self.config.ipc_path);

        // Wait for Lua to create the response file with Ready message
        // Lua initiates the handshake (PZ sandbox: Lua can only read files it created)
        info!("Waiting for PZ Lua to send Ready message...");
        info!("Start the game with GameRL mod enabled, then load/start a game");

        // Wait for Ready message from game (no timeout - game may take a while to start)
        let ready_msg = self.wait_for_initial_response().await?;

        match ready_msg {
            GameMessage::Ready {
                name,
                version,
                capabilities,
            } => {
                info!("Game connected: {} v{}", name, version);
                self.game_name = name;
                self.game_version = version;
                self.capabilities = Some(capabilities);
                self.connected = true;

                // Clear command file to signal we're ready
                let _ = fs::write(&self.command_file, "").await;

                Ok(())
            }
            _ => Err(GameRLError::ProtocolError(format!(
                "Expected Ready message, got {:?}",
                ready_msg
            ))),
        }
    }

    /// Check if PZ is alive by checking if response file has content
    async fn check_game_alive(&self) -> bool {
        // Game is alive if we recently got a response
        // For now, just return connected status
        self.connected
    }

    /// Attempt to reconnect to PZ after it restarts
    async fn reconnect(&mut self) -> Result<()> {
        info!("Attempting to reconnect to Project Zomboid...");
        self.connected = false;

        info!("Waiting for PZ Lua to send Ready message...");

        // Wait for Ready message from game
        let ready_msg = self.wait_for_initial_response().await?;

        match ready_msg {
            GameMessage::Ready {
                name,
                version,
                capabilities,
            } => {
                info!("Reconnected to {} v{}", name, version);
                self.game_name = name;
                self.game_version = version;
                self.capabilities = Some(capabilities);
                self.connected = true;

                // Clear command file to signal we're ready
                let _ = fs::write(&self.command_file, "").await;

                Ok(())
            }
            _ => Err(GameRLError::ProtocolError(format!(
                "Expected Ready message, got {:?}",
                ready_msg
            ))),
        }
    }

    /// Send a command and wait for response (with auto-reconnect on timeout)
    async fn request(&mut self, msg: GameMessage) -> Result<GameMessage> {
        // Auto-reconnect if not connected
        if !self.connected {
            self.reconnect().await?;
        }

        // Serialize message
        let json = serde_json::to_string(&msg)
            .map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        debug!("[Rust→PZ] {}", &json[..json.len().min(200)]);

        // Write command file
        fs::write(&self.command_file, &json)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Failed to write command: {}", e)))?;

        // Wait for response
        match self.wait_for_response().await {
            Ok(response) => Ok(response),
            Err(e) => {
                // On timeout, check if game died and try to reconnect
                if !self.check_game_alive().await {
                    warn!("Game appears to have restarted, attempting reconnect...");
                    self.reconnect().await?;
                    // Retry the request once after reconnect
                    fs::write(&self.command_file, &json).await.map_err(|e| {
                        GameRLError::IpcError(format!("Failed to write command: {}", e))
                    })?;
                    self.wait_for_response().await
                } else {
                    Err(e)
                }
            }
        }
    }

    /// Wait for a response in the response file
    /// If `initial_wait` is true, waits indefinitely (for game startup)
    async fn wait_for_response_impl(&self, initial_wait: bool) -> Result<GameMessage> {
        let start = std::time::Instant::now();
        let mut last_log = std::time::Instant::now();

        loop {
            // For normal requests, use timeout; for initial connection, wait forever
            if !initial_wait && start.elapsed() > self.config.response_timeout {
                return Err(GameRLError::IpcError("Response timeout".into()));
            }

            // Periodic status logging during initial wait
            if initial_wait && last_log.elapsed() > Duration::from_secs(10) {
                info!("Still waiting for Project Zomboid... (start/load a game, press F11)");
                last_log = std::time::Instant::now();
            }

            // Try to read response file
            match fs::read_to_string(&self.response_file).await {
                Ok(content) if !content.is_empty() => {
                    // Clear response file
                    let _ = fs::write(&self.response_file, "").await;

                    debug!("[PZ→Rust] {}", &content[..content.len().min(200)]);

                    // Parse JSON
                    let msg: GameMessage = serde_json::from_str(&content)
                        .map_err(|e| GameRLError::SerializationError(e.to_string()))?;

                    return Ok(msg);
                }
                _ => {
                    // Wait and retry
                    sleep(self.config.poll_interval).await;
                }
            }
        }
    }

    /// Wait for a response (with timeout for normal operations)
    async fn wait_for_response(&self) -> Result<GameMessage> {
        self.wait_for_response_impl(false).await
    }

    /// Wait for initial connection (no timeout, for game startup)
    async fn wait_for_initial_response(&self) -> Result<GameMessage> {
        self.wait_for_response_impl(true).await
    }

    /// Send a message without waiting for response
    async fn send(&mut self, msg: GameMessage) -> Result<()> {
        if !self.connected {
            return Err(GameRLError::IpcError("Not connected".into()));
        }

        let json = serde_json::to_string(&msg)
            .map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        debug!("[Rust→PZ] {}", &json[..json.len().min(200)]);

        fs::write(&self.command_file, &json)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Failed to write command: {}", e)))?;

        Ok(())
    }

    /// Account for one step the game ran, and report the pair that goes on
    /// `StepResult`.
    ///
    /// `step_id` is measured here: it counts the steps this bridge has executed
    /// since the last `reset`, one per step the game actually ran.
    ///
    /// `tick` is read out of the observation the Lua mod just sent, where
    /// `StateExtractor.extractObservation` publishes the world age in seconds.
    /// It is *not* derived from `step_count`: a step is a request, a tick is the
    /// game's clock, and the mod advances 60 of them per step
    /// (`ticksPerStep`). An observation with no tick in it means "unmeasured",
    /// which is reported as 0 and warned about once per episode, because a
    /// silent 0 is indistinguishable from a real measurement.
    fn record_step(&mut self, observation: &Observation) -> (u64, u64) {
        self.step_count += 1;

        let tick = match observation_tick(observation) {
            Some(tick) => tick,
            None => {
                if !self.warned_no_tick {
                    self.warned_no_tick = true;
                    warn!(
                        "PZ mod sent no Tick in its observation; reporting tick=0 for step {}. \
                         That 0 is unmeasured, not a game tick.",
                        self.step_count
                    );
                }
                0
            }
        };

        (self.step_count, tick)
    }

    /// Begin a new episode: step numbering restarts, and so does the warning.
    fn start_episode(&mut self) {
        self.step_count = 0;
        self.warned_no_tick = false;
    }
}

/// The error both unsupported trajectory calls return.
///
/// `save_trajectory` and `load_trajectory` still fail, because no trajectory
/// format exists on either side of the file IPC boundary yet. What changed is
/// that the failure now explains itself: a caller reading only this cannot tell
/// an unimplemented feature from a path that could not be written, so every
/// clause below is load-bearing. It names the ignored argument, the path, the
/// fact that nothing was touched, and what the manifest claims.
fn trajectory_unsupported(
    method: &str,
    path: &str,
    manifest: &GameManifest,
    ipc_dir: &std::path::Path,
    file_action: &str,
) -> GameRLError {
    GameRLError::GameError(format!(
        "{method}(path={path:?}) is not supported by the Project Zomboid bridge and did \
         nothing: no trajectory format has been implemented on either side of the file IPC \
         boundary at {}, so no file was {file_action} at that path, nothing was created there, \
         and the Lua mod was never called. The `path` argument is accepted only so it can be \
         reported back here; it is ignored. The manifest this bridge reports declares \
         capabilities.save_replay = {}, so trajectory replay is declared off rather than broken: \
         treat this as an unavailable feature rather than as a failed operation.",
        ipc_dir.display(),
        manifest.capabilities.save_replay,
    ))
}

impl Default for ZomboidBridge {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait::async_trait]
impl GameEnvironment for ZomboidBridge {
    async fn register_agent(
        &mut self,
        agent_id: AgentId,
        agent_type: AgentType,
        config: AgentConfig,
    ) -> Result<AgentManifest> {
        let response = self
            .request(GameMessage::RegisterAgent {
                agent_id: agent_id.clone(),
                agent_type: agent_type.clone(),
                config,
            })
            .await?;

        match response {
            GameMessage::AgentRegistered {
                agent_id,
                observation_space,
                action_space,
            } => Ok(AgentManifest {
                agent_id,
                agent_type,
                observation_space,
                action_space,
                reward_components: vec![],
            }),
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }
    }

    async fn deregister_agent(&mut self, agent_id: &AgentId) -> Result<()> {
        self.send(GameMessage::DeregisterAgent {
            agent_id: agent_id.clone(),
        })
        .await
    }

    async fn step(&mut self, agent_id: &AgentId, action: Action, ticks: u32) -> Result<StepResult> {
        let response = self
            .request(GameMessage::ExecuteAction {
                agent_id: agent_id.clone(),
                action,
                ticks,
            })
            .await?;

        fn build_step_result(payload: StepResultPayload, step_id: u64, tick: u64) -> StepResult {
            StepResult {
                agent_id: payload.agent_id,
                step_id,
                tick,
                observation: payload.observation,
                reward: payload.reward,
                reward_components: payload.reward_components,
                done: payload.done,
                truncated: payload.truncated,
                termination_reason: None,
                events: vec![],
                frame_ids: HashMap::new(),
                available_actions: None,
                metrics: None,
                state_hash: payload.state_hash,
            }
        }

        let payload = match response {
            GameMessage::StepResult { result } => Ok(result),
            GameMessage::BatchStepResult { results } => results
                .into_iter()
                .find(|result| &result.agent_id == agent_id)
                .ok_or_else(|| {
                    GameRLError::ProtocolError("BatchStepResult missing requested agent".into())
                }),
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }?;

        // Counted only now: a step that failed or never reached the game must
        // not consume a step id.
        let (step_id, tick) = self.record_step(&payload.observation);

        Ok(build_step_result(payload, step_id, tick))
    }

    async fn reset(
        &mut self,
        seed: Option<u64>,
        scenario: Option<String>,
        mode: ResetMode,
    ) -> Result<ResetOutcome> {
        let response = self
            .request(GameMessage::Reset {
                seed,
                scenario,
                mode,
            })
            .await?;

        match response {
            GameMessage::ResetComplete {
                observation,
                state_hash,
                mode,
                seed: applied,
                seed_consumed,
                postconditions,
            } => {
                // Only once the reset is known to have happened: a reset that
                // failed must not renumber the episode that is still running.
                self.start_episode();
                Ok(ResetOutcome {
                    observation,
                    state_hash,
                    mode,
                    seed: applied,
                    seed_consumed,
                    postconditions,
                })
            }
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError(
                "Unexpected response to Reset".into(),
            )),
        }
    }

    async fn state_hash(&mut self) -> Result<String> {
        let response = self.request(GameMessage::GetStateHash).await?;

        match response {
            GameMessage::StateHash { hash } => Ok(hash),
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }
    }

    async fn configure_streams(
        &mut self,
        agent_id: &AgentId,
        profile: &str,
    ) -> Result<Vec<StreamDescriptor>> {
        let response = self
            .request(GameMessage::ConfigureStreams {
                agent_id: agent_id.clone(),
                profile: profile.to_string(),
            })
            .await?;

        match response {
            GameMessage::StreamsConfigured {
                agent_id: response_agent_id,
                descriptors,
            } => {
                if &response_agent_id != agent_id {
                    warn!(
                        "StreamsConfigured agent mismatch: expected {}, got {}",
                        agent_id, response_agent_id
                    );
                }
                Ok(descriptors)
            }
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }
    }

    async fn save_trajectory(&self, path: &str) -> Result<()> {
        // Still a failure, and deliberately: there is no trajectory format on
        // either side of this boundary yet. The message has to stand on its own,
        // because the caller cannot tell an unimplemented feature from a failed
        // write without it.
        Err(trajectory_unsupported(
            "save_trajectory",
            path,
            &self.manifest(),
            &self.config.ipc_path,
            "written",
        ))
    }

    async fn load_trajectory(&mut self, path: &str) -> Result<()> {
        Err(trajectory_unsupported(
            "load_trajectory",
            path,
            &self.manifest(),
            &self.config.ipc_path,
            "read",
        ))
    }

    async fn shutdown(&mut self) -> Result<()> {
        self.send(GameMessage::Shutdown).await?;
        self.connected = false;

        // Clean up status file
        let _ = fs::remove_file(&self.status_file).await;

        Ok(())
    }

    fn manifest(&self) -> GameManifest {
        let caps = self.capabilities.clone().unwrap_or(GameCapabilities {
            multi_agent: true,
            max_agents: 4,
            deterministic: false,
            headless: false,
        });

        GameManifest {
            name: self.game_name.clone(),
            version: self.game_version.clone(),
            game_rl_version: env!("CARGO_PKG_VERSION").into(),
            capabilities: game_rl_core::Capabilities {
                multi_agent: caps.multi_agent,
                max_agents: caps.max_agents,
                deterministic: caps.deterministic,
                headless: caps.headless,
                ..Default::default()
            },
            ..Default::default()
        }
    }

    fn subscribe_events(&self) -> Option<broadcast::Receiver<StateUpdate>> {
        Some(self.event_tx.subscribe())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A bridge that has not connected: the counters and the error messages do
    /// not need a game, so nothing here touches the IPC directory.
    fn bridge() -> ZomboidBridge {
        ZomboidBridge::new()
    }

    /// An observation shaped like the one the PZ mod sends, with the given tick.
    fn observation_at(tick: u64) -> Observation {
        Observation::Structured(
            [
                (String::from("Tick"), serde_json::json!(tick)),
                (String::from("SurvivorCount"), serde_json::json!(1)),
            ]
            .into_iter()
            .collect(),
        )
    }

    #[test]
    fn step_id_counts_steps_while_tick_comes_from_the_mod() {
        let mut bridge = bridge();

        // The mod advances 60 game ticks per step, so three steps move the tick
        // by 180 and the step id by 3.
        assert_eq!(bridge.record_step(&observation_at(1_000)), (1, 1_000));
        assert_eq!(bridge.record_step(&observation_at(1_060)), (2, 1_060));
        assert_eq!(bridge.record_step(&observation_at(1_120)), (3, 1_120));
    }

    #[test]
    fn step_id_restarts_after_reset() {
        let mut bridge = bridge();
        bridge.record_step(&observation_at(500));
        bridge.record_step(&observation_at(560));

        // What `reset` calls once the mod has confirmed the reset.
        bridge.start_episode();

        assert_eq!(bridge.record_step(&observation_at(9_000)), (1, 9_000));
    }

    #[test]
    fn missing_tick_is_reported_as_zero_and_not_as_the_step_id() {
        let mut bridge = bridge();

        // An observation with no tick in it: the mod did not measure one.
        let (step_id, tick) = bridge.record_step(&Observation::Structured(Default::default()));
        assert_eq!(step_id, 1);
        assert_eq!(tick, 0, "an unmeasured tick must not borrow the step count");
        // ...and it must not drift into the step count either.
        assert_eq!(bridge.record_step(&Observation::Vector(vec![1.0])).1, 0);
    }

    #[tokio::test]
    async fn save_trajectory_error_explains_itself() {
        let err = bridge()
            .save_trajectory("/tmp/episode-7.jsonl")
            .await
            .expect_err("no trajectory format exists yet")
            .to_string();

        // What was asked, where, why not, and what the manifest says.
        assert!(err.contains("save_trajectory"), "{err}");
        assert!(err.contains("/tmp/episode-7.jsonl"), "{err}");
        assert!(err.contains("it is ignored"), "{err}");
        assert!(err.contains("no file was written"), "{err}");
        assert!(err.contains("capabilities.save_replay = false"), "{err}");
    }

    #[tokio::test]
    async fn load_trajectory_error_explains_itself() {
        let mut bridge = bridge();
        let err = bridge
            .load_trajectory("/tmp/episode-7.jsonl")
            .await
            .expect_err("no trajectory format exists yet")
            .to_string();

        assert!(err.contains("load_trajectory"), "{err}");
        assert!(err.contains("/tmp/episode-7.jsonl"), "{err}");
        assert!(err.contains("it is ignored"), "{err}");
        assert!(err.contains("no file was read"), "{err}");
        assert!(err.contains("capabilities.save_replay = false"), "{err}");
    }
}
