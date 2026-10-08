//! IPC communication with .NET games

use async_trait::async_trait;
use game_bridge::observation_tick;
#[cfg(unix)]
use game_bridge::reader_task;
#[cfg(unix)]
use game_bridge::{
    AsyncWriter, Endpoint, GameCapabilities, GameMessage, StepResultPayload, TcpReadWrapper,
    TcpWriteWrapper, serialize,
};
use game_rl_core::{
    Action, AgentConfig, AgentId, AgentManifest, AgentType, GameManifest, GameRLError, Observation,
    ResetMode, ResetOutcome, Result, StepResult, StreamDescriptor,
};
use game_rl_server::GameEnvironment;
use game_rl_server::environment::StateUpdate;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::sync::{Mutex, broadcast, mpsc, oneshot};
use tracing::{debug, info, warn};

/// The error both unsupported trajectory calls return.
///
/// `save_trajectory` and `load_trajectory` still fail, because no trajectory
/// format exists on either side of the IPC boundary yet. What changed is that
/// the failure now explains itself: a caller reading only this cannot tell an
/// unimplemented feature from a path that could not be written, so every clause
/// below is load-bearing. It names the ignored argument, the path, the fact
/// that nothing was touched, and what the manifest claims about the capability.
fn trajectory_unsupported(
    method: &str,
    path: &str,
    manifest: &GameManifest,
    bridge: &str,
    file_action: &str,
) -> GameRLError {
    GameRLError::GameError(format!(
        "{method}(path={path:?}) is not supported by the Harmony bridge over {bridge} and did \
         nothing: no trajectory format has been implemented on either side of this boundary, so \
         no file was {file_action} at that path, nothing was created there, and the adapter was \
         never called. The `path` argument is accepted only so it can be reported back here; it \
         is ignored. The manifest this bridge reports declares capabilities.save_replay = {}, so \
         trajectory replay is declared off rather than broken: treat this as an unavailable \
         feature rather than as a failed operation.",
        manifest.capabilities.save_replay,
    ))
}

/// Bridge to a .NET game via IPC
pub struct HarmonyBridge {
    /// Resolved once at startup: this bridge never guesses between instances
    endpoint: Endpoint,
    /// Writer half of the connection
    writer: Arc<Mutex<Option<Box<dyn AsyncWriter>>>>,
    /// Channel to send requests to the reader task
    request_tx: mpsc::Sender<(GameMessage, oneshot::Sender<Result<GameMessage>>)>,
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
    /// conversation with the adapter rather than of the game state -- and
    /// because the adapter has no such field to report.
    step_count: u64,
    /// Whether the "the adapter reported no tick" warning has already been
    /// logged for this episode, so a misreporting adapter warns once instead of
    /// once per step.
    warned_no_tick: bool,
    /// Background reader task handle
    _reader_handle: Option<tokio::task::JoinHandle<()>>,
}

impl HarmonyBridge {
    /// Create a new bridge (not connected yet).
    ///
    /// The endpoint is resolved here rather than at connect time so an
    /// unparseable or empty value fails at construction.
    pub fn new(endpoint: &str) -> Result<Self> {
        let endpoint = Endpoint::parse(endpoint)?;
        info!("Bridge target: {}", endpoint.describe());

        // Create channels
        let (request_tx, _request_rx) = mpsc::channel(16);
        let (event_tx, _) = broadcast::channel(64);

        Ok(Self {
            endpoint,
            writer: Arc::new(Mutex::new(None)),
            request_tx,
            event_tx,
            capabilities: None,
            game_name: "Unknown".into(),
            game_version: "0.0.0".into(),
            step_count: 0,
            warned_no_tick: false,
            _reader_handle: None,
        })
    }

    /// Connect to the game process
    pub async fn connect(&mut self) -> Result<()> {
        info!("Connecting to game via {}", self.endpoint.describe());

        // Build reader/writer halves for whichever transport was resolved. The
        // two wrappers are byte-for-byte equivalent; only the stream differs.
        let writer: Box<dyn AsyncWriter>;

        let reader_task_handle: tokio::task::JoinHandle<()> = match self.endpoint.clone() {
            #[cfg(unix)]
            Endpoint::Unix(path) => {
                #[cfg(unix)]
                use game_bridge::{UnixReadWrapper, UnixWriteWrapper};
                use tokio::net::UnixStream;

                let stream = UnixStream::connect(&path).await.map_err(|e| {
                    GameRLError::IpcError(format!("Failed to connect to unix socket {path}: {e}"))
                })?;
                let (read_half, write_half) = stream.into_split();
                writer = Box::new(UnixWriteWrapper(write_half));

                let (request_tx, request_rx) = mpsc::channel(16);
                self.request_tx = request_tx;
                let event_tx = self.event_tx.clone();
                tokio::spawn(reader_task(
                    UnixReadWrapper(read_half),
                    request_rx,
                    event_tx,
                ))
            }
            Endpoint::Tcp(addr) => {
                use tokio::net::TcpStream;

                let stream = TcpStream::connect(&addr).await.map_err(|e| {
                    GameRLError::IpcError(format!("Failed to connect to tcp {addr}: {e}"))
                })?;
                // The bridge is loopback-only by construction; Nagle would add
                // latency to a request/response protocol with small messages.
                let _ = stream.set_nodelay(true);
                let (read_half, write_half) = stream.into_split();
                writer = Box::new(TcpWriteWrapper(write_half));

                let (request_tx, request_rx) = mpsc::channel(16);
                self.request_tx = request_tx;
                let event_tx = self.event_tx.clone();
                tokio::spawn(reader_task(TcpReadWrapper(read_half), request_rx, event_tx))
            }
        };

        {
            let mut guard = self.writer.lock().await;
            *guard = Some(writer);
        }
        self._reader_handle = Some(reader_task_handle);

        #[cfg(unix)]
        {
            // Wait for Ready message by sending a dummy request that expects Ready
            // The reader task will handle routing the response
            let (response_tx, response_rx) = oneshot::channel();
            // Send an empty marker - the first message from the game is Ready
            self.request_tx
                .send((GameMessage::GetStateHash, response_tx)) // Dummy, reader handles Ready specially
                .await
                .map_err(|_| GameRLError::IpcError("Failed to send to reader task".into()))?;

            // Wait for Ready response
            let msg = response_rx
                .await
                .map_err(|_| GameRLError::IpcError("Reader task died".into()))??;

            match msg {
                GameMessage::Ready {
                    name,
                    version,
                    capabilities,
                } => {
                    info!("Connected to {} v{}", name, version);
                    self.game_name = name;
                    self.game_version = version;
                    self.capabilities = Some(capabilities);
                    Ok(())
                }
                _ => Err(GameRLError::ProtocolError(format!(
                    "Expected Ready message, got {:?}",
                    msg
                ))),
            }
        }
    }

    /// Send a message and wait for response
    async fn request(&mut self, msg: GameMessage) -> Result<GameMessage> {
        let data = serialize(&msg).map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        // Log outgoing message
        let json_preview: String = String::from_utf8_lossy(&data).chars().take(200).collect();
        debug!("[Rust→C#] len={} json={}", data.len(), json_preview);

        // Send through writer
        {
            let mut guard = self.writer.lock().await;
            let writer = guard
                .as_mut()
                .ok_or_else(|| GameRLError::IpcError("Not connected".into()))?;
            writer.write_message(&data).await?;
        }

        // Wait for response via channel
        let (response_tx, response_rx) = oneshot::channel();
        self.request_tx
            .send((msg, response_tx))
            .await
            .map_err(|_| GameRLError::IpcError("Reader task not running".into()))?;

        response_rx
            .await
            .map_err(|_| GameRLError::IpcError("Reader task died waiting for response".into()))?
    }

    /// Ask the adapter what actions it actually supports.
    ///
    /// Read from reflection rather than a manifest, so the contract cannot drift
    /// from the dispatcher.
    pub async fn list_actions(&mut self) -> Result<serde_json::Value> {
        match self.request(GameMessage::ListActions).await? {
            GameMessage::ActionsListed { actions } => Ok(serde_json::Value::Array(actions)),
            other => Err(GameRLError::ProtocolError(format!(
                "Expected ActionsListed, got {other:?}"
            ))),
        }
    }

    /// Send a message without waiting for response (fire-and-forget)
    async fn send(&mut self, msg: GameMessage) -> Result<()> {
        let data = serialize(&msg).map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        let json_preview: String = String::from_utf8_lossy(&data).chars().take(200).collect();
        debug!("[Rust→C#] len={} json={}", data.len(), json_preview);

        let mut guard = self.writer.lock().await;
        let writer = guard
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("Not connected".into()))?;
        writer.write_message(&data).await
    }

    /// Account for one step the game ran, and report the pair that goes on
    /// `StepResult`.
    ///
    /// `step_id` is measured here: it counts the steps this bridge has executed
    /// since the last `reset`, one per step the game actually ran.
    ///
    /// `tick` is read out of the observation the adapter just sent, where the
    /// RimWorld extractor publishes `Find.TickManager.TicksGame`. It is *not*
    /// derived from `step_count`: a step is a request, a tick is the game's
    /// clock, and `ticks` per step is the adapter's choice. An observation with
    /// no tick in it means "unmeasured", which is reported as 0 and warned
    /// about once per episode, because a silent 0 is indistinguishable from a
    /// real measurement.
    fn record_step(&mut self, observation: &Observation) -> (u64, u64) {
        self.step_count += 1;

        let tick = match observation_tick(observation) {
            Some(tick) => tick,
            None => {
                if !self.warned_no_tick {
                    self.warned_no_tick = true;
                    warn!(
                        "Adapter sent no Tick in its observation; reporting tick=0 for step {}. \
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

#[async_trait]
impl GameEnvironment for HarmonyBridge {
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
                metrics: payload.metrics,
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

    async fn list_actions(&mut self) -> Result<serde_json::Value> {
        HarmonyBridge::list_actions(self).await
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
            &self.endpoint.describe(),
            "written",
        ))
    }

    async fn load_trajectory(&mut self, path: &str) -> Result<()> {
        Err(trajectory_unsupported(
            "load_trajectory",
            path,
            &self.manifest(),
            &self.endpoint.describe(),
            "read",
        ))
    }

    async fn shutdown(&mut self) -> Result<()> {
        self.send(GameMessage::Shutdown).await?;
        let mut guard = self.writer.lock().await;
        *guard = None;
        Ok(())
    }

    fn manifest(&self) -> GameManifest {
        let caps = self.capabilities.clone().unwrap_or(GameCapabilities {
            multi_agent: false,
            max_agents: 1,
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
    /// not need a game, so nothing here touches the socket.
    fn bridge() -> HarmonyBridge {
        HarmonyBridge::new("/tmp/game-rl-test.sock").expect("endpoint parses")
    }

    /// An observation shaped like the one the RimWorld adapter sends, with the
    /// given game tick.
    fn observation_at(tick: u64) -> Observation {
        Observation::Structured(
            [
                (String::from("Tick"), serde_json::json!(tick)),
                (String::from("ColonistCount"), serde_json::json!(3)),
            ]
            .into_iter()
            .collect(),
        )
    }

    #[test]
    fn step_id_counts_steps_while_tick_comes_from_the_adapter() {
        let mut bridge = bridge();

        // The two are independent: three steps of 60 game ticks each move the
        // tick by 180 and the step id by 3.
        assert_eq!(bridge.record_step(&observation_at(1_000)), (1, 1_000));
        assert_eq!(bridge.record_step(&observation_at(1_060)), (2, 1_060));
        assert_eq!(bridge.record_step(&observation_at(1_120)), (3, 1_120));
    }

    #[test]
    fn step_id_restarts_after_reset() {
        let mut bridge = bridge();
        bridge.record_step(&observation_at(500));
        bridge.record_step(&observation_at(560));

        // What `reset` calls once the adapter has confirmed the reset.
        bridge.start_episode();

        assert_eq!(bridge.record_step(&observation_at(9_000)), (1, 9_000));
    }

    #[test]
    fn missing_tick_is_reported_as_zero_and_not_as_the_step_id() {
        let mut bridge = bridge();

        // An observation with no tick in it: the adapter did not measure one.
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
