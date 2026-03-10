use std::sync::Arc;

use crate::services::shell_backend::ShellBackend;

/// Service for installing/updating the OpenClaw binary inside the shell environment.
pub struct OpenClawService {
    shell: Arc<dyn ShellBackend>,
}

impl OpenClawService {
    pub fn new(shell: Arc<dyn ShellBackend>) -> Self {
        Self { shell }
    }

    /// Install Node.js 22 + OpenClaw inside the shell environment.
    /// macOS/Linux: installs directly.
    /// Windows: installs inside WSL distro.
    pub async fn install(
        &self,
        on_log: &(dyn Fn(String) + Send + Sync),
    ) -> Result<(), String> {
        let platform = self.shell.platform();

        let steps: Vec<(&str, String)> = match platform {
            "windows" => {
                // Windows/WSL: controlled Ubuntu environment, use apt + install Node.js
                let env = "LANG=C DEBIAN_FRONTEND=noninteractive";

                let node_install_script = r#"#!/bin/bash
set -e
NODE_VER=$(curl -fsSL https://npmmirror.com/mirrors/node/latest-v22.x/SHASUMS256.txt | head -1 | sed 's/.*node-v//' | sed 's/-.*//')
if [ -z "$NODE_VER" ]; then echo 'ERROR: Failed to determine Node.js version'; exit 1; fi
echo "Installing Node.js v$NODE_VER..."
ARCH=$(uname -m)
if [ "$ARCH" = "aarch64" ] || [ "$ARCH" = "arm64" ]; then
    NODE_ARCH="arm64"
else
    NODE_ARCH="x64"
fi
curl -fsSL "https://npmmirror.com/mirrors/node/v$NODE_VER/node-v$NODE_VER-linux-$NODE_ARCH.tar.xz" | tar -xJ --strip-components=1 -C /usr/local
"#;
                let script_b64 = {
                    use base64::Engine;
                    base64::engine::general_purpose::STANDARD.encode(node_install_script.as_bytes())
                };

                let switch_apt_mirror = r#"if grep -q 'archive.ubuntu.com' /etc/apt/sources.list 2>/dev/null; then sed -i 's|http://archive.ubuntu.com|http://mirrors.aliyun.com|g; s|http://security.ubuntu.com|http://mirrors.aliyun.com|g' /etc/apt/sources.list && echo 'Switched to Aliyun mirror'; elif [ -d /etc/apt/sources.list.d ]; then find /etc/apt/sources.list.d -name '*.sources' -exec sed -i 's|http://archive.ubuntu.com|http://mirrors.aliyun.com|g; s|http://security.ubuntu.com|http://mirrors.aliyun.com|g' {} + && echo 'Switched to Aliyun mirror (deb822)'; fi"#;

                vec![
                    ("Switching to Aliyun apt mirror", switch_apt_mirror.to_string()),
                    ("Updating package list", format!("{} apt-get update -q", env)),
                    (
                        "Installing base tools",
                        format!("{} apt-get install -y -q curl ca-certificates git xz-utils", env),
                    ),
                    (
                        "Downloading and installing Node.js 22",
                        format!("echo {} | base64 -d | bash", script_b64),
                    ),
                    ("Verifying Node.js version", "/usr/local/bin/node --version".to_string()),
                    (
                        "Configuring npm mirror",
                        "/usr/local/bin/npm config set registry https://registry.npmmirror.com"
                            .to_string(),
                    ),
                    (
                        "Installing OpenClaw globally",
                        "/usr/local/bin/npm install -g openclaw@latest".to_string(),
                    ),
                ]
            }
            _ => {
                // macOS and Linux: use official one-click install script
                vec![
                    (
                        "Installing OpenClaw via official script",
                        "curl -fsSL https://openclaw.ai/install.sh | bash".to_string(),
                    ),
                ]
            }
        };

        for (label, cmd) in &steps {
            on_log(format!("▶ {}...", label));

            let exit_code = self
                .shell
                .run_command_stream(cmd, on_log)
                .await
                .map_err(|e| format!("{} failed: {}", label, e))?;

            if exit_code != 0 {
                return Err(format!("「{}」failed (exit code {})", label, exit_code));
            }

            on_log(format!("  ✓ {} done", label));
            on_log(String::new());
        }

        on_log("✓ OpenClaw installation complete".to_string());
        Ok(())
    }

    /// Get the currently installed OpenClaw version
    pub async fn get_installed_version(&self) -> Option<String> {
        // Windows/WSL uses absolute path; macOS/Linux rely on shell PATH
        let cmd = if self.shell.platform() == "windows" {
            "/usr/local/bin/openclaw --version"
        } else {
            "openclaw --version"
        };

        let output = self.shell.run_command(cmd).await.ok()?;
        if !output.status.success() {
            return None;
        }
        // Output may be like "OpenClaw 2026.3.8 (3caab92)" or "openclaw/1.2.3" or just "1.2.3"
        let raw = output.stdout.trim().to_string();
        // Extract version number: sequence of digits and dots (e.g. "2026.3.8")
        let version = raw
            .split_whitespace()
            .find(|s| s.chars().next().map_or(false, |c| c.is_ascii_digit()) && s.contains('.'))
            .unwrap_or(&raw)
            .trim()
            .to_string();
        if version.is_empty() {
            None
        } else {
            Some(version)
        }
    }

    /// Get the latest OpenClaw version from npm registry
    pub async fn get_latest_version(&self) -> Option<String> {
        let client = reqwest::Client::builder()
            .timeout(std::time::Duration::from_secs(10))
            .build()
            .ok()?;

        let resp = client
            .get("https://registry.npmmirror.com/openclaw/latest")
            .send()
            .await
            .ok()?;

        let json: serde_json::Value = resp.json().await.ok()?;
        json.get("version")
            .and_then(|v| v.as_str())
            .map(|s| s.to_string())
    }

    /// Uninstall OpenClaw from the shell environment.
    pub async fn uninstall(
        &self,
        on_log: &(dyn Fn(String) + Send + Sync),
    ) -> Result<(), String> {
        on_log("▶ Uninstalling OpenClaw...".to_string());

        let cmd = if self.shell.platform() == "windows" {
            "/usr/local/bin/npm uninstall -g openclaw 2>&1 || true"
        } else {
            "openclaw uninstall --all --yes 2>&1 || true; rm -rf ~/.openclaw 2>/dev/null || true"
        };

        let exit_code = self
            .shell
            .run_command_stream(cmd, on_log)
            .await
            .map_err(|e| e.to_string())?;

        if exit_code != 0 {
            on_log(format!("  ⚠ npm uninstall exited with code {} (continuing)", exit_code));
        } else {
            on_log("  ✓ OpenClaw uninstalled".to_string());
        }

        Ok(())
    }

    /// Update OpenClaw to latest version
    pub async fn update(
        &self,
        on_log: &(dyn Fn(String) + Send + Sync),
    ) -> Result<bool, String> {
        on_log("▶ Updating OpenClaw...".to_string());

        let cmd = if self.shell.platform() == "windows" {
            "/usr/local/bin/npm install -g openclaw@latest"
        } else {
            "npm install -g openclaw@latest"
        };

        let exit_code = self
            .shell
            .run_command_stream(cmd, on_log)
            .await
            .map_err(|e| e.to_string())?;

        if exit_code != 0 {
            on_log(format!("  ✗ Update failed (exit code {})", exit_code));
            return Ok(false);
        }

        on_log("  ✓ OpenClaw update complete".to_string());
        Ok(true)
    }
}
