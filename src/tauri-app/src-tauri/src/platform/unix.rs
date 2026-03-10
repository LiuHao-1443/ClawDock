use async_trait::async_trait;
use tokio::io::{AsyncBufReadExt, BufReader};
use tokio::process::{Child, Command};

use crate::services::shell_backend::{clean_line, CommandOutput, ShellBackend, ShellError};

pub struct DirectShellBackend;

impl DirectShellBackend {
    pub fn new() -> Self {
        Self
    }
}

#[async_trait]
impl ShellBackend for DirectShellBackend {
    async fn run_command(&self, command: &str) -> Result<CommandOutput, ShellError> {
        let output = Command::new("bash")
            .args(["-l", "-c", command])
            .output()
            .await
            .map_err(|e| ShellError::ExecutionFailed(e.to_string()))?;

        Ok(CommandOutput {
            stdout: String::from_utf8_lossy(&output.stdout).to_string(),
            stderr: String::from_utf8_lossy(&output.stderr).to_string(),
            status: output.status,
        })
    }

    async fn run_command_stream(
        &self,
        command: &str,
        on_line: &(dyn Fn(String) + Send + Sync),
    ) -> Result<i32, ShellError> {
        let mut child = Command::new("bash")
            .args(["-l", "-c", command])
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
                    if !cleaned.is_empty() {
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
                    if !cleaned.is_empty() {
                        let _ = tx_err.send(cleaned);
                    }
                }
            }
        });

        // Drop our sender so rx closes when tasks finish
        drop(tx);

        // Stream lines to callback as they arrive
        while let Some(line) = rx.recv().await {
            on_line(line);
        }

        // Wait for reader tasks to complete
        let _ = stdout_task.await;
        let _ = stderr_task.await;

        let status = child
            .wait()
            .await
            .map_err(|e| ShellError::ExecutionFailed(e.to_string()))?;

        Ok(status.code().unwrap_or(-1))
    }

    async fn spawn_process(&self, command: &str) -> Result<Child, ShellError> {
        let child = Command::new("bash")
            .args(["-l", "-c", command])
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::piped())
            .spawn()
            .map_err(|e| ShellError::ExecutionFailed(e.to_string()))?;

        Ok(child)
    }

    fn config_path(&self) -> String {
        let home = dirs::home_dir()
            .map(|p| p.to_string_lossy().to_string())
            .unwrap_or_else(|| "/root".to_string());
        format!("{}/.openclaw", home)
    }

    fn platform(&self) -> &'static str {
        if cfg!(target_os = "macos") {
            "macos"
        } else {
            "linux"
        }
    }
}
