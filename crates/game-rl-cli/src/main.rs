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
use game_bridge::Endpoint;
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

/// Resolve the RimWorld bridge endpoint.
///
/// Order matters and mirrors the C# side exactly, so both ends agree without
/// extra configuration:
///
///   1. `--endpoint`
///   2. `GAMERL_TCP`
///   3. `GAMERL_SOCKET`
///   4. `GAMERL_ENDPOINT_FILE`, which the mod writes once it has bound. This is
///      how an ephemeral port (`:0`) gets discovered.
///   5. a single live Unix socket, only when there is exactly one
///
/// Anything ambiguous fails with the candidate list rather than picking by
/// modification time, which is how a parallel run silently attaches to the
/// wrong colony.
fn resolve_rimworld_endpoints(explicit: Option<&str>) -> Result<Vec<String>> {
    if let Some(e) = explicit {
        return Ok(vec![e.to_string()]);
    }

    if let Ok(v) = std::env::var("GAMERL_TCP")
        && !v.trim().is_empty()
    {
        return Ok(vec![v.trim().to_string()]);
    }

    if let Ok(v) = std::env::var("GAMERL_SOCKET")
        && !v.trim().is_empty()
    {
        return Ok(vec![v.trim().to_string()]);
    }

    if let Ok(path) = std::env::var("GAMERL_ENDPOINT_FILE")
        && let Ok(contents) = std::fs::read_to_string(&path)
    {
        let raw = contents.trim();
        if !raw.is_empty() {
            // The mod writes "tcp host:port" or "unix /path".
            return Ok(vec![match raw.split_once(char::is_whitespace) {
                Some((kind, rest)) if kind.eq_ignore_ascii_case("unix") => rest.to_string(),
                Some((kind, rest)) if kind.eq_ignore_ascii_case("tcp") => rest.to_string(),
                _ => raw.to_string(),
            }]);
        }
    }

    let live: Vec<String> = std::env::temp_dir()
        .read_dir()
        .map(|rd| {
            rd.flatten()
                .map(|e| e.path().display().to_string())
                .filter(|p| {
                    Path::new(p)
                        .file_name()
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

    match live.len() {
        1 => Ok(live),
        0 => Ok(Vec::new()),
        n => bail!(
            "{n} RimWorld instances are live ({}) and none was selected. Refusing to guess: \
             set GAMERL_TCP, or pass --endpoint <host:port>.",
            live.join(", ")
        ),
    }
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

/// `--endpoint <host:port|path>`, or the older `--socket <path>`. None otherwise.
fn parse_endpoint_arg() -> Option<String> {
    let mut args = std::env::args().skip(1);
    while let Some(a) = args.next() {
        for flag in ["--endpoint", "--socket"] {
            if a == flag {
                return args.next();
            }
            if let Some(v) = a.strip_prefix(&format!("{flag}=")) {
                return Some(v.to_string());
            }
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

    let explicit = parse_endpoint_arg();
    // Resolve once: ambiguity must be reported before we start polling, not
    // silently resolved by mtime later.
    let rimworld_endpoints = resolve_rimworld_endpoints(explicit.as_deref())?;

    info!("Game-RL MCP server starting (auto-detecting game)...");

    // Detection paths
    let zomboid_config = ZomboidConfig::default();
    let zomboid_response = zomboid_config.ipc_path.join("gamerl_response.json");
    let factorio_config = FactorioConfig::default();

    // Auto-detect game - check all sources and pick most recent
    loop {
        let mut candidates: Vec<GameCandidate> = Vec::new();

        // Check all potential game sources
        // TCP endpoints are not files, so they are probed by connection rather
        // than by mtime.
        for ep in &rimworld_endpoints {
            let ready = match Endpoint::parse(ep) {
                Ok(Endpoint::Tcp(addr)) => tokio::net::TcpStream::connect(&addr).await.is_ok(),
                Ok(Endpoint::Unix(path)) => Path::new(&path).exists(),
                Err(_) => false,
            };
            if ready {
                candidates.push(GameCandidate {
                    name: "RimWorld",
                    modified: SystemTime::now(),
                    sock: Some(PathBuf::from(ep)),
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
        candidates.sort_by_key(|a| std::cmp::Reverse(a.modified));

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
                    let mut bridge = match HarmonyBridge::new(&sock) {
                        Ok(b) => b,
                        Err(e) => {
                            warn!("RimWorld endpoint '{sock}' is not usable: {e}");
                            continue;
                        }
                    };
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
            if rimworld_endpoints.is_empty() {
                info!(
                    "  RimWorld: no endpoint configured and none live. Set GAMERL_TCP=127.0.0.1:<port> \
                     (required on Wine/Proton), or GAMERL_ENDPOINT_FILE for an ephemeral port."
                );
            } else {
                for e in &rimworld_endpoints {
                    info!("  RimWorld: endpoint {e}");
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
