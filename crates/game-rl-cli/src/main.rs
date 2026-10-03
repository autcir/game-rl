//! Game-RL MCP Server
//!
//! Unified server that auto-detects which game is running:
//! - RimWorld via Unix socket (/tmp/gamerl-rimworld-{INSTANCE_ID}.sock)
//! - Project Zomboid via file IPC (~/Zomboid/Lua/gamerl_response.json)
//! - Factorio via RCON (localhost:27015)
//!
//! Detection checks all sources and picks the most recently active one. With
//! several RimWorld instances running that heuristic is ambiguous, so an
//! explicit --socket (or GAMERL_INSTANCE_ID) takes precedence and, for
//! RimWorld, is required: there is no global fallback path.

use anyhow::{Result, bail};
use factorio_bridge::{FactorioBridge, FactorioConfig};
use game_rl_server::{GameEnvironment, GameRLServer};
use harmony_bridge::HarmonyBridge;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime};
use tokio::net::TcpStream;
use tokio::time::sleep;
use tracing::{Level, debug, info, warn};
use tracing_subscriber::FmtSubscriber;
use zomboid_bridge::{ZomboidBridge, ZomboidConfig};

/// Candidate game with its freshness timestamp
struct GameCandidate {
    name: &'static str,
    modified: SystemTime,
    /// Set for RimWorld: which instance's socket this candidate refers to.
    sock: Option<PathBuf>,
}

const RIMWORLD_SOCKET_PREFIX: &str = "gamerl-rimworld-";
const RIMWORLD_SOCKET_SUFFIX: &str = ".sock";
const FACTORIO_RCON_ADDR: &str = "127.0.0.1:27015";

/// Resolve the RimWorld IPC socket.
///
/// `explicit` comes from `--socket` and wins outright. Otherwise
/// `GAMERL_INSTANCE_ID` selects one instance's socket. With neither we refuse to
/// start rather than fall back to a shared path: under parallel rollout a shared
/// path silently attaches to whichever instance is there, and the resulting data
/// is contaminated rather than broken.
///
/// When several instances are live, every candidate socket is returned so the
/// caller can report them instead of guessing by mtime.
fn resolve_rimworld_sockets(explicit: Option<&str>) -> Result<Vec<std::path::PathBuf>> {
    if let Some(p) = explicit {
        return Ok(vec![PathBuf::from(p)]);
    }

    if let Ok(id) = std::env::var("GAMERL_INSTANCE_ID")
        && !id.trim().is_empty()
    {
        let p = std::env::temp_dir().join(format!(
            "{RIMWORLD_SOCKET_PREFIX}{id}{RIMWORLD_SOCKET_SUFFIX}"
        ));
        return Ok(vec![p]);
    }

    let live: Vec<std::path::PathBuf> = std::env::temp_dir()
        .read_dir()
        .map(|rd| {
            rd.flatten()
                .map(|e| e.path())
                .filter(|p| {
                    p.file_name()
                        .and_then(|n| n.to_str())
                        .map(|n| {
                            n.starts_with(RIMWORLD_SOCKET_PREFIX)
                                && n.ends_with(RIMWORLD_SOCKET_SUFFIX)
                        })
                        .unwrap_or(false)
                })
                .collect()
        })
        .unwrap_or_default();

    if live.len() == 1 {
        return Ok(live);
    }

    if live.is_empty() {
        // No socket yet: the game may simply not have launched. An empty target
        // list means "keep waiting", not "pick something".
        return Ok(Vec::new());
    }

    bail!(
        "{} RimWorld instances are live ({}) and none was selected. Refusing to guess: \
         set GAMERL_INSTANCE_ID or pass --socket <path>.",
        live.len(),
        live.iter()
            .map(|p| p.display().to_string())
            .collect::<Vec<_>>()
            .join(", ")
    )
}

/// Run the MCP server with a game bridge
async fn run_with_bridge<E: GameEnvironment>(bridge: E) -> Result<()> {
    let manifest = bridge.manifest();
    info!("Connected to {} v{}", manifest.name, manifest.version);
    let server = GameRLServer::new(bridge, manifest);
    server.run_stdio().await?;
    Ok(())
}

/// Get file modification time, or None if file doesn't exist
fn get_mtime(path: &Path) -> Option<SystemTime> {
    std::fs::metadata(path).ok()?.modified().ok()
}

/// `--socket <path>`, or None.
fn parse_socket_arg() -> Option<String> {
    let mut args = std::env::args().skip(1);
    while let Some(a) = args.next() {
        if a == "--socket" {
            return args.next();
        }
        if let Some(v) = a.strip_prefix("--socket=") {
            return Some(v.to_string());
        }
    }
    None
}

#[tokio::main]
async fn main() -> Result<()> {
    // Initialize logging
    let subscriber = FmtSubscriber::builder()
        .with_max_level(Level::DEBUG)
        .with_writer(std::io::stderr)
        .finish();
    tracing::subscriber::set_global_default(subscriber)?;

    let explicit = parse_socket_arg();
    // Resolve once: ambiguity must be reported before we start polling, not
    // silently resolved by mtime later.
    let rimworld_sockets = resolve_rimworld_sockets(explicit.as_deref())?;

    info!("Game-RL MCP server starting (auto-detecting game)...");

    // Detection paths
    let zomboid_config = ZomboidConfig::default();
    let zomboid_response = zomboid_config.ipc_path.join("gamerl_response.json");
    let factorio_config = FactorioConfig::default();

    // Auto-detect game - check all sources and pick most recent
    loop {
        let mut candidates: Vec<GameCandidate> = Vec::new();

        // Check all potential game sources
        for sock in &rimworld_sockets {
            if let Some(mtime) = get_mtime(sock) {
                candidates.push(GameCandidate {
                    name: "RimWorld",
                    modified: mtime,
                    sock: Some(sock.clone()),
                });
            }
        }

        if let Some(mtime) = get_mtime(&zomboid_response) {
            candidates.push(GameCandidate {
                name: "Zomboid",
                modified: mtime,
                sock: None,
            });
        }

        // For Factorio, check if RCON port is open (use current time as "freshness")
        if TcpStream::connect(FACTORIO_RCON_ADDR).await.is_ok() {
            candidates.push(GameCandidate {
                name: "Factorio",
                modified: SystemTime::now(), // Active connection = most fresh
                sock: None,
            });
        }

        // Sort by most recent first
        candidates.sort_by(|a, b| b.modified.cmp(&a.modified));

        // Try to connect to each candidate in order of freshness
        for candidate in &candidates {
            debug!(
                "Trying {} (modified: {:?})",
                candidate.name, candidate.modified
            );

            match candidate.name {
                "RimWorld" => {
                    let Some(sock) = candidate.sock.as_ref() else {
                        continue;
                    };
                    let sock = sock.display().to_string();
                    info!("RimWorld socket detected: {}", sock);
                    let mut bridge = HarmonyBridge::new(&sock);
                    match bridge.connect().await {
                        Ok(()) => return run_with_bridge(bridge).await,
                        Err(e) => warn!("RimWorld socket exists but connect failed: {}", e),
                    }
                }
                "Zomboid" => {
                    info!("Project Zomboid IPC detected: {:?}", zomboid_response);
                    let mut bridge = ZomboidBridge::with_config(zomboid_config.clone());
                    match bridge.init().await {
                        Ok(()) => return run_with_bridge(bridge).await,
                        Err(e) => warn!("Zomboid response exists but init failed: {}", e),
                    }
                }
                "Factorio" => {
                    info!("Factorio RCON detected at {}", FACTORIO_RCON_ADDR);
                    let mut bridge = FactorioBridge::with_config(factorio_config.clone());
                    match bridge.init().await {
                        Ok(()) => return run_with_bridge(bridge).await,
                        Err(e) => warn!("Factorio RCON exists but init failed: {}", e),
                    }
                }
                _ => {}
            }
        }

        if candidates.is_empty() {
            info!("Waiting for game connection...");
            if rimworld_sockets.is_empty() {
                info!(
                    "  RimWorld: no socket yet and no GAMERL_INSTANCE_ID set. If more than one \
                     instance will run, set it now so this server attaches to the right one."
                );
            } else {
                for s in &rimworld_sockets {
                    info!("  RimWorld: socket at {}", s.display());
                }
            }
            info!("  Zomboid:  file at {:?}", zomboid_response);
            info!(
                "  Factorio: RCON at {} (enable in config.ini, host multiplayer)",
                FACTORIO_RCON_ADDR
            );
        } else {
            info!("No game connected successfully, retrying...");
        }
        sleep(Duration::from_secs(2)).await;
    }
}
