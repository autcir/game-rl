//! TCP transport implementation for game bridges.
//!
//! Exists because `AF_UNIX` is unavailable on Windows and therefore on any
//! Wine or Proton runtime, which is the only way to run RimWorld on Linux.
//! Wine's `bind()` answers `WSAEAFNOSUPPORT` for `AF_UNIX` because the family is
//! absent from its `supported_protocols[]`, and .NET Framework exposes no
//! `Socket` support for it either. TCP on loopback is what every other RimWorld
//! bridge uses for exactly this reason.
//!
//! The framing is identical to the Unix transport: 4-byte little-endian length
//! followed by a JSON payload. Only the byte stream differs.

use crate::transport::{AsyncReader, AsyncWriter};
use async_trait::async_trait;
use game_rl_core::{GameRLError, Result};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};

/// Messages above this are a bug or an attack, not an observation.
const MAX_MESSAGE: usize = 64 * 1024 * 1024;

pub struct TcpReadWrapper(pub OwnedReadHalf);

#[async_trait]
impl AsyncReader for TcpReadWrapper {
    async fn read_message(&mut self) -> Result<Vec<u8>> {
        let mut len_bytes = [0u8; 4];
        self.0
            .read_exact(&mut len_bytes)
            .await
            .map_err(|e| GameRLError::IpcError(format!("TCP read length failed: {}", e)))?;
        let len = u32::from_le_bytes(len_bytes) as usize;

        if len > MAX_MESSAGE {
            return Err(GameRLError::IpcError(format!(
                "Message too large: {} bytes",
                len
            )));
        }

        let mut data = vec![0u8; len];
        self.0
            .read_exact(&mut data)
            .await
            .map_err(|e| GameRLError::IpcError(format!("TCP read data failed: {}", e)))?;

        Ok(data)
    }
}

pub struct TcpWriteWrapper(pub OwnedWriteHalf);

#[async_trait]
impl AsyncWriter for TcpWriteWrapper {
    async fn write_message(&mut self, data: &[u8]) -> Result<()> {
        let len = (data.len() as u32).to_le_bytes();
        self.0
            .write_all(&len)
            .await
            .map_err(|e| GameRLError::IpcError(format!("TCP write length failed: {}", e)))?;
        self.0
            .write_all(data)
            .await
            .map_err(|e| GameRLError::IpcError(format!("TCP write data failed: {}", e)))?;
        self.0
            .flush()
            .await
            .map_err(|e| GameRLError::IpcError(format!("TCP flush failed: {}", e)))?;
        Ok(())
    }
}

/// Where the bridge should connect.
///
/// Resolved once at startup so an ambiguous configuration fails loudly instead
/// of attaching to whichever instance answers first.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Endpoint {
    Unix(String),
    Tcp(String),
}

impl Endpoint {
    /// Parse `host:port`, or treat the value as a filesystem path.
    ///
    /// Anything containing a colon that parses as `host:port` is TCP. Windows
    /// drive letters never appear here because this is the host side.
    pub fn parse(raw: &str) -> Result<Self> {
        let raw = raw.trim();
        if raw.is_empty() {
            return Err(GameRLError::IpcError("empty endpoint".into()));
        }

        // A bare path that exists, or that looks like one, wins over parsing.
        if raw.starts_with('/') || raw.starts_with('.') || raw.contains(".sock") {
            return Ok(Endpoint::Unix(raw.to_string()));
        }

        match raw.rsplit_once(':') {
            Some((host, port)) if !host.is_empty() && port.chars().all(|c| c.is_ascii_digit()) => {
                Ok(Endpoint::Tcp(raw.to_string()))
            }
            _ => Ok(Endpoint::Unix(raw.to_string())),
        }
    }

    pub fn describe(&self) -> String {
        match self {
            Endpoint::Unix(p) => format!("unix socket {p}"),
            Endpoint::Tcp(a) => format!("tcp {a}"),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tcp_endpoint_is_detected() {
        assert_eq!(
            Endpoint::parse("127.0.0.1:5174").unwrap(),
            Endpoint::Tcp("127.0.0.1:5174".into())
        );
    }

    #[test]
    fn absolute_path_stays_unix() {
        assert_eq!(
            Endpoint::parse("/tmp/gamerl-rimworld-alpha.sock").unwrap(),
            Endpoint::Unix("/tmp/gamerl-rimworld-alpha.sock".into())
        );
    }

    #[test]
    fn relative_socket_path_stays_unix() {
        assert_eq!(
            Endpoint::parse("./gamerl.sock").unwrap(),
            Endpoint::Unix("./gamerl.sock".into())
        );
    }

    #[test]
    fn non_numeric_port_is_not_tcp() {
        assert_eq!(
            Endpoint::parse("/tmp/weird:name").unwrap(),
            Endpoint::Unix("/tmp/weird:name".into())
        );
    }

    #[test]
    fn empty_endpoint_is_rejected() {
        assert!(Endpoint::parse("   ").is_err());
    }
}
