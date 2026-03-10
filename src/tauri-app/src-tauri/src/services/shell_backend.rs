use async_trait::async_trait;
use std::process::ExitStatus;
use std::sync::LazyLock;
use tokio::process::Child;

static RE_ANSI: LazyLock<regex::Regex> =
    LazyLock::new(|| regex::Regex::new(r"\x1B\[[0-9;]*[A-Za-z]").unwrap());
static RE_CTRL: LazyLock<regex::Regex> =
    LazyLock::new(|| regex::Regex::new(r"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]").unwrap());
static RE_SPACES: LazyLock<regex::Regex> =
    LazyLock::new(|| regex::Regex::new(r" {2,}").unwrap());

#[derive(Debug, Clone)]
pub struct CommandOutput {
    pub stdout: String,
    pub stderr: String,
    pub status: ExitStatus,
}

#[async_trait]
pub trait ShellBackend: Send + Sync {
    /// Run a command and wait for completion
    async fn run_command(&self, command: &str) -> Result<CommandOutput, ShellError>;

    /// Run a command, streaming stdout/stderr lines to callbacks
    async fn run_command_stream(
        &self,
        command: &str,
        on_line: &(dyn Fn(String) + Send + Sync),
    ) -> Result<i32, ShellError>;

    /// Spawn a long-running process (e.g., gateway)
    async fn spawn_process(&self, command: &str) -> Result<Child, ShellError>;

    /// Get the path to openclaw config directory inside the shell environment
    fn config_path(&self) -> String;

    /// Get platform name
    fn platform(&self) -> &'static str;
}

/// Clean a line of output: strip ANSI escapes, handle \r progress lines, remove control chars.
/// Ported from WslService.cs CleanLine.
pub fn clean_line(line: &str) -> String {
    // Remove ANSI escape sequences
    let mut cleaned = RE_ANSI.replace_all(line, "").to_string();

    // Normalize \r\n to \n
    cleaned = cleaned.replace("\r\n", "\n");

    // apt uses \r to overwrite progress lines - keep only the last segment
    if let Some(cr_idx) = cleaned.rfind('\r') {
        cleaned = cleaned[cr_idx + 1..].to_string();
    }

    // Remove null bytes and non-printable control chars (keep tab)
    cleaned = RE_CTRL.replace_all(&cleaned, "").to_string();

    // Collapse multiple spaces
    cleaned = RE_SPACES.replace_all(&cleaned, " ").to_string();

    cleaned.trim().to_string()
}

#[derive(Debug, thiserror::Error)]
pub enum ShellError {
    #[error("Command execution failed: {0}")]
    ExecutionFailed(String),

    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),

    #[error("Command timed out")]
    Timeout,

    #[error("Process not found")]
    ProcessNotFound,
}

impl serde::Serialize for ShellError {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        serializer.serialize_str(&self.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_clean_line_ansi() {
        assert_eq!(clean_line("\x1B[32mhello\x1B[0m"), "hello");
    }

    #[test]
    fn test_clean_line_progress() {
        assert_eq!(clean_line("old progress\rfinal line"), "final line");
    }

    #[test]
    fn test_clean_line_spaces() {
        assert_eq!(clean_line("hello    world"), "hello world");
    }

    #[test]
    fn test_clean_line_null_bytes() {
        assert_eq!(clean_line("he\x00llo"), "hello");
    }
}
