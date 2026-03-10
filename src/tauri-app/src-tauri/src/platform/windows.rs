use async_trait::async_trait;
use tokio::io::{AsyncBufReadExt, BufReader};
use tokio::process::{Child, Command};

use crate::services::shell_backend::{clean_line, CommandOutput, ShellBackend, ShellError};

const WSL_DISTRO: &str = "ClawDock";

pub struct WslShellBackend {
    distro: String,
}

impl WslShellBackend {
    pub fn new() -> Self {
        Self {
            distro: WSL_DISTRO.to_string(),
        }
    }

    fn wrap_command(&self, command: &str) -> Command {
        let mut cmd = Command::new("wsl");
        cmd.args([
            "-d",
            &self.distro,
            "--user",
            "root",
            "--",
            "bash",
            "-l",
            "-c",
            command,
        ]);
        cmd
    }
}

#[async_trait]
impl ShellBackend for WslShellBackend {
    async fn run_command(&self, command: &str) -> Result<CommandOutput, ShellError> {
        let output = self
            .wrap_command(command)
            .output()
            .await
            .map_err(|e| ShellError::ExecutionFailed(e.to_string()))?;

        let stdout = String::from_utf8_lossy(&output.stdout).to_string();
        let stderr = String::from_utf8_lossy(&output.stderr).to_string();

        // Filter garbled WSL messages
        let stdout = clean_wsl_output(&stdout);
        let stderr = clean_wsl_output(&stderr);

        Ok(CommandOutput {
            stdout,
            stderr,
            status: output.status,
        })
    }

    async fn run_command_stream(
        &self,
        command: &str,
        on_line: &(dyn Fn(String) + Send + Sync),
    ) -> Result<i32, ShellError> {
        let mut child = self
            .wrap_command(command)
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::piped())
            .spawn()
            .map_err(|e| ShellError::ExecutionFailed(e.to_string()))?;

        let stdout = child.stdout.take();
        let stderr = child.stderr.take();

        // Use a channel to stream lines in real-time while reading concurrently
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel::<String>();

        let tx_out = tx.clone();
        let stdout_task = tokio::spawn(async move {
            if let Some(stdout) = stdout {
                let mut lines = BufReader::new(stdout).lines();
                while let Ok(Some(line)) = lines.next_line().await {
                    let cleaned = clean_line(&line);
                    if !cleaned.is_empty() && !is_garbled_wsl_message(&cleaned) {
                        let _ = tx_out.send(cleaned);
                    }
                }
            }
        });

        let tx_err = tx.clone();
        let stderr_task = tokio::spawn(async move {
            if let Some(stderr) = stderr {
                let mut lines = BufReader::new(stderr).lines();
                while let Ok(Some(line)) = lines.next_line().await {
                    let cleaned = clean_line(&line);
                    if !cleaned.is_empty() && !is_garbled_wsl_message(&cleaned) {
                        let _ = tx_err.send(cleaned);
                    }
                }
            }
        });

        drop(tx);

        while let Some(line) = rx.recv().await {
            on_line(line);
        }

        let _ = stdout_task.await;
        let _ = stderr_task.await;

        let status = child
            .wait()
            .await
            .map_err(|e| ShellError::ExecutionFailed(e.to_string()))?;

        Ok(status.code().unwrap_or(-1))
    }

    async fn spawn_process(&self, command: &str) -> Result<Child, ShellError> {
        let child = self
            .wrap_command(command)
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::piped())
            .spawn()
            .map_err(|e| ShellError::ExecutionFailed(e.to_string()))?;

        Ok(child)
    }

    fn config_path(&self) -> String {
        "/root/.openclaw".to_string()
    }

    fn platform(&self) -> &'static str {
        "windows"
    }
}

/// Filter garbled WSL output lines
fn clean_wsl_output(output: &str) -> String {
    output
        .lines()
        .map(|line| clean_line(line))
        .filter(|line| !line.is_empty() && !is_garbled_wsl_message(line))
        .collect::<Vec<_>>()
        .join("\n")
}

/// Detect garbled WSL messages (ported from WslService.cs IsGarbledWslMessage)
fn is_garbled_wsl_message(line: &str) -> bool {
    if line.len() < 4 {
        return false;
    }

    // Known garbled WSL infrastructure messages
    let garbled_patterns = [
        "<3>init: ",
        "<3>WSL",
        "<4>init: ",
        "[process exited with code",
        "Processing fstab",
    ];

    for pattern in &garbled_patterns {
        if line.contains(pattern) {
            return true;
        }
    }

    // WSL localhost proxy message pattern (UTF-16LE read as UTF-8)
    if line.contains("localhost") && line.len() < 40 && !line.contains("http") && !line.contains("://")
    {
        return true;
    }

    // Count non-ASCII, non-CJK characters (garbled UTF-16LE → UTF-8)
    let garbled_count = line
        .chars()
        .filter(|c| {
            let cp = *c as u32;
            cp > 0x7F
                && !(0x4E00..=0x9FFF).contains(&cp) // CJK Unified
                && !(0x3000..=0x303F).contains(&cp) // CJK Symbols
                && !(0xFF00..=0xFFEF).contains(&cp) // Fullwidth
                && *c != '\u{2713}'                  // ✓
                && *c != '\u{2717}'                  // ✗
                && !c.is_ascii()
        })
        .count();

    garbled_count >= 3
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_garbled_detection() {
        assert!(is_garbled_wsl_message("<3>init: some wsl message"));
        assert!(is_garbled_wsl_message("[process exited with code 1]"));
        assert!(!is_garbled_wsl_message("normal output line"));
        assert!(!is_garbled_wsl_message(""));
        assert!(!is_garbled_wsl_message("✓ done"));
    }

    #[test]
    fn test_clean_wsl_output() {
        let input = "good line\n<3>init: blah\nanother good line";
        let result = clean_wsl_output(input);
        assert_eq!(result, "good line\nanother good line");
    }
}
