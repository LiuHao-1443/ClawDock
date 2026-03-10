use std::sync::Arc;
use tokio::io::{AsyncBufReadExt, BufReader};
use tokio::process::Child;
use tokio::sync::Mutex;

use crate::services::shell_backend::{clean_line, ShellBackend};

const GATEWAY_PORT: u16 = 18789;
const GATEWAY_URL: &str = "http://127.0.0.1:18789";

#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum GatewayStatus {
    Stopped,
    Starting,
    Running,
    Error,
}

pub struct GatewayService {
    shell: Arc<dyn ShellBackend>,
    state: Mutex<GatewayState>,
}

struct GatewayState {
    status: GatewayStatus,
    process: Option<Child>,
    auth_token: String,
    last_error: String,
}

impl GatewayService {
    pub fn new(shell: Arc<dyn ShellBackend>) -> Self {
        Self {
            shell,
            state: Mutex::new(GatewayState {
                status: GatewayStatus::Stopped,
                process: None,
                auth_token: String::new(),
                last_error: String::new(),
            }),
        }
    }

    pub async fn status(&self) -> GatewayStatus {
        self.state.lock().await.status
    }

    pub async fn last_error(&self) -> String {
        self.state.lock().await.last_error.clone()
    }

    pub async fn auth_token(&self) -> String {
        self.state.lock().await.auth_token.clone()
    }

    pub async fn dashboard_url(&self) -> String {
        let state = self.state.lock().await;
        if state.auth_token.is_empty() {
            GATEWAY_URL.to_string()
        } else {
            let encoded_token = url_encode(&state.auth_token);
            format!("{}?token={}", GATEWAY_URL, encoded_token)
        }
    }

    /// Start the gateway process. Returns immediately; process runs in background.
    /// Caller should use the AppHandle to emit events.
    pub async fn start(
        self: &Arc<Self>,
        on_log: impl Fn(String) + Send + Sync + 'static,
        on_status: impl Fn(GatewayStatus) + Send + Sync + 'static,
    ) -> Result<(), String> {
        {
            let mut state = self.state.lock().await;
            if state.status == GatewayStatus::Running || state.status == GatewayStatus::Starting {
                return Ok(());
            }
            state.status = GatewayStatus::Starting;
            state.last_error.clear();
        }
        on_status(GatewayStatus::Starting);
        on_log("[START] Starting Gateway process...".to_string());

        // Kill any stale openclaw processes before starting
        let _ = self.shell.run_command("pkill -9 -x openclaw 2>/dev/null || true").await;
        // Brief pause to let the port release
        tokio::time::sleep(std::time::Duration::from_millis(500)).await;

        let cmd = format!(
            "openclaw gateway --port {} --allow-unconfigured",
            GATEWAY_PORT
        );

        let mut child: tokio::process::Child = self
            .shell
            .spawn_process(&cmd)
            .await
            .map_err(|e| format!("Failed to spawn gateway: {}", e))?;

        let stdout = child.stdout.take();
        let stderr = child.stderr.take();

        {
            let mut state = self.state.lock().await;
            state.process = Some(child);
        }

        // Wrap callbacks in Arc so they can be shared across concurrent tasks
        let on_log = Arc::new(on_log);
        let on_status = Arc::new(on_status);

        // Spawn task to read stdout/stderr CONCURRENTLY and detect ready state
        let this = Arc::clone(self);
        let shell = Arc::clone(&self.shell);
        tokio::spawn(async move {
            use std::sync::atomic::{AtomicBool, Ordering};
            let ready = Arc::new(AtomicBool::new(false));

            // Helper: detect ready and set state (shared by stdout/stderr readers)
            async fn handle_ready_detection(
                line: &str,
                ready: &AtomicBool,
                this: &GatewayService,
                shell: &dyn ShellBackend,
                on_log: &(dyn Fn(String) + Send + Sync),
                on_status: &(dyn Fn(GatewayStatus) + Send + Sync),
            ) {
                if !ready.load(Ordering::Acquire)
                    && (line.contains("listening on")
                        || line.contains(&format!(":{}", GATEWAY_PORT)))
                {
                    // Use compare_exchange to ensure only one task wins
                    if ready
                        .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
                        .is_ok()
                    {
                        let token = read_auth_token(shell).await;
                        {
                            let mut state = this.state.lock().await;
                            state.status = GatewayStatus::Running;
                            state.auth_token = token;
                        }
                        on_log("[START] Gateway ready!".to_string());
                        on_status(GatewayStatus::Running);
                    }
                }
            }

            // Spawn stdout reader
            let this_out = Arc::clone(&this);
            let shell_out = Arc::clone(&shell);
            let ready_out = Arc::clone(&ready);
            let on_log_out = Arc::clone(&on_log);
            let on_status_out = Arc::clone(&on_status);
            let stdout_task = tokio::spawn(async move {
                if let Some(stdout) = stdout {
                    let mut lines = BufReader::new(stdout).lines();
                    while let Ok(Some(line)) = lines.next_line().await {
                        let cleaned = clean_line(&line);
                        if cleaned.is_empty() {
                            continue;
                        }
                        handle_ready_detection(
                            &cleaned, &ready_out, &this_out, &*shell_out,
                            &*on_log_out, &*on_status_out,
                        ).await;
                        on_log_out(cleaned);
                    }
                }
            });

            // Spawn stderr reader — also detect "listening on" (some loggers write to stderr)
            let this_err = Arc::clone(&this);
            let shell_err = Arc::clone(&shell);
            let ready_err = Arc::clone(&ready);
            let on_log_err = Arc::clone(&on_log);
            let on_status_err = Arc::clone(&on_status);
            let stderr_task = tokio::spawn(async move {
                if let Some(stderr) = stderr {
                    let mut lines = BufReader::new(stderr).lines();
                    while let Ok(Some(line)) = lines.next_line().await {
                        let cleaned = clean_line(&line);
                        if cleaned.is_empty() {
                            continue;
                        }
                        handle_ready_detection(
                            &cleaned, &ready_err, &this_err, &*shell_err,
                            &*on_log_err, &*on_status_err,
                        ).await;
                        on_log_err(cleaned);
                    }
                }
            });

            // Startup timeout: if not ready within 30s, mark as error
            let this_timeout = Arc::clone(&this);
            let ready_timeout = Arc::clone(&ready);
            let on_log_timeout = Arc::clone(&on_log);
            let on_status_timeout = Arc::clone(&on_status);
            let timeout_task = tokio::spawn(async move {
                tokio::time::sleep(std::time::Duration::from_secs(30)).await;
                if !ready_timeout.load(Ordering::Acquire) {
                    let mut state = this_timeout.state.lock().await;
                    // Only report timeout if process is still present (not taken by stop())
                    if state.process.is_some() && state.status == GatewayStatus::Starting {
                        state.status = GatewayStatus::Error;
                        state.last_error = "Gateway startup timed out (30s)".to_string();
                        on_log_timeout("[ERROR] Gateway startup timed out".to_string());
                        on_status_timeout(GatewayStatus::Error);
                    }
                }
            });

            // Wait for both readers to finish (process exited)
            let _ = stdout_task.await;
            let _ = stderr_task.await;
            timeout_task.abort();

            // Process exited — reap the child process
            let mut state = this.state.lock().await;
            if state.process.is_none() {
                // process was taken by stop() — intentional shutdown, don't touch status
                return;
            }
            if let Some(ref mut child) = state.process {
                let _ = child.wait().await;
            }
            if state.status == GatewayStatus::Running {
                state.status = GatewayStatus::Stopped;
                on_log("[START] Gateway process exited".to_string());
                on_status(GatewayStatus::Stopped);
            } else if state.status == GatewayStatus::Starting {
                state.status = GatewayStatus::Error;
                state.last_error = "Gateway process exited before becoming ready".to_string();
                on_log("[ERROR] Gateway process exited unexpectedly".to_string());
                on_status(GatewayStatus::Error);
            }
            state.process = None;
        });

        Ok(())
    }

    /// Stop the gateway process
    pub async fn stop(
        &self,
        on_log: impl Fn(String) + Send + Sync + 'static,
    ) -> Result<(), String> {
        on_log("[STOP] Stopping Gateway...".to_string());

        // Take the child out of state and update status while holding the lock,
        // then kill/wait OUTSIDE the lock to avoid blocking other state readers.
        let child = {
            let mut state = self.state.lock().await;
            let child = state.process.take();
            state.status = GatewayStatus::Stopped;
            state.auth_token.clear();
            child
        };
        if let Some(mut child) = child {
            let _ = child.kill().await;
            let _ = child.wait().await;
        }

        on_log("[STOP] Process killed".to_string());

        // Kill any remaining openclaw processes inside the shell environment
        let shell = Arc::clone(&self.shell);
        let _ = shell.run_command("pkill -9 -x openclaw 2>/dev/null || true").await;
        on_log("[STOP] Cleanup complete".to_string());

        Ok(())
    }

    /// Restart = stop + delay + start
    pub async fn restart(
        self: &Arc<Self>,
        on_log: impl Fn(String) + Send + Sync + Clone + 'static,
        on_status: impl Fn(GatewayStatus) + Send + Sync + Clone + 'static,
    ) -> Result<(), String> {
        on_log.clone()("[RESTART] Restarting...".to_string());
        self.stop(on_log.clone()).await?;
        tokio::time::sleep(std::time::Duration::from_secs(2)).await;
        self.start(on_log, on_status).await
    }

    /// Check if gateway is reachable via TCP
    pub async fn is_running(&self) -> bool {
        tokio::net::TcpStream::connect(format!("127.0.0.1:{}", GATEWAY_PORT))
            .await
            .is_ok()
    }
}

/// Read auth token from openclaw.json
async fn read_auth_token(shell: &dyn ShellBackend) -> String {
    let cmd = format!("cat '{}/openclaw.json'", shell.config_path());
    match shell.run_command(&cmd).await {
        Ok(output) => {
            let stdout = output.stdout;
            // Find the JSON start
            if let Some(json_start) = stdout.find('{') {
                if let Ok(doc) = serde_json::from_str::<serde_json::Value>(&stdout[json_start..]) {
                    if let Some(token) = doc
                        .get("gateway")
                        .and_then(|g| g.get("auth"))
                        .and_then(|a| a.get("token"))
                        .and_then(|t| t.as_str())
                    {
                        return token.to_string();
                    }
                }
            }
            String::new()
        }
        Err(_) => String::new(),
    }
}

/// Percent-encode a string for safe use in URL query parameters.
fn url_encode(s: &str) -> String {
    let mut encoded = String::with_capacity(s.len());
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                encoded.push(b as char);
            }
            _ => {
                encoded.push_str(&format!("%{:02X}", b));
            }
        }
    }
    encoded
}
