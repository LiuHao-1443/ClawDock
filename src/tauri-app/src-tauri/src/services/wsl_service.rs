//! Windows-only WSL management service.
//! Handles system checks (Windows build, virtualization, WSL2, distro)
//! and distro import from bundled rootfs.

#[cfg(target_os = "windows")]
use winreg::enums::*;
#[cfg(target_os = "windows")]
use winreg::RegKey;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WslCheckResult {
    pub windows_version_ok: bool,
    pub virtualization_enabled: bool,
    pub wsl2_installed: bool,
    pub distro_installed: bool,
    pub windows_build: String,
}

pub const DISTRO_NAME: &str = "ClawDock";
const MIN_BUILD_NUMBER: u32 = 19041;

/// Perform all WSL system checks (Windows only).
/// On non-Windows platforms, returns a dummy result.
pub fn check() -> WslCheckResult {
    #[cfg(target_os = "windows")]
    {
        let build = get_windows_build();
        let build_ok = build.parse::<u32>().map_or(false, |n| n >= MIN_BUILD_NUMBER);

        WslCheckResult {
            windows_version_ok: build_ok,
            virtualization_enabled: is_virtualization_enabled(),
            wsl2_installed: is_wsl2_installed(),
            distro_installed: is_distro_installed(),
            windows_build: build,
        }
    }

    #[cfg(not(target_os = "windows"))]
    {
        WslCheckResult {
            windows_version_ok: true,
            virtualization_enabled: true,
            wsl2_installed: true,
            distro_installed: true,
            windows_build: "N/A".to_string(),
        }
    }
}

/// Full WSL2 + ClawDock distro installation.
/// Ported from WPF WslService.InstallAsync.
#[cfg(target_os = "windows")]
pub async fn install_distro(
    resource_dir: std::path::PathBuf,
    on_log: impl Fn(String) + Send + Sync,
) -> Result<(), String> {
    on_log("正在检查系统状态...".to_string());

    if is_wsl2_installed() && is_distro_installed() {
        on_log("✓ WSL2 + ClawDock 发行版已安装，跳过此步骤".to_string());
        return Ok(());
    }

    // Step 1: Enable WSL2 if needed
    if !is_wsl2_installed() {
        on_log("".to_string());
        on_log("▶ 正在启用 WSL2...".to_string());

        let exit_code = run_wsl_cmd_logged(&["--install", "--no-distribution"], &on_log).await;
        on_log(format!("  WSL2 安装命令完成 (exit={})", exit_code));

        if exit_code != 0 && !is_wsl2_installed() {
            on_log("⚠ WSL2 功能已启用，可能需要重启计算机以激活".to_string());
            return Err("WSL2 安装需要重启计算机，请重启后再运行 ClawDock".to_string());
        }
    }

    // Step 2: Import distro from bundled rootfs
    if !is_distro_installed() {
        on_log("".to_string());
        on_log("▶ 正在从内置镜像导入 Ubuntu 22.04...".to_string());
        on_log("  （本地解压，无需下载）".to_string());

        let rootfs_path = resource_dir.join("resources").join("ubuntu-base.tar.gz");
        if !rootfs_path.exists() {
            return Err(format!(
                "找不到内嵌 rootfs: {}",
                rootfs_path.display()
            ));
        }

        let install_dir = get_distro_install_dir();

        // Clean up stale directory
        if install_dir.exists() {
            let _ = std::fs::remove_dir_all(&install_dir);
        }
        std::fs::create_dir_all(&install_dir)
            .map_err(|e| format!("创建安装目录失败: {}", e))?;

        on_log(format!(
            "  导入路径: {}",
            install_dir.display()
        ));

        let exit_code = run_wsl_cmd_logged(
            &[
                "--import",
                DISTRO_NAME,
                &install_dir.to_string_lossy(),
                &rootfs_path.to_string_lossy(),
                "--version",
                "2",
            ],
            &on_log,
        )
        .await;

        if exit_code != 0 {
            // Check if kernel update needed
            on_log("⚠ 导入失败，尝试更新 WSL 内核...".to_string());
            let update_code = run_wsl_cmd_logged(&["--update"], &on_log).await;
            if update_code == 0 {
                on_log("  ✓ WSL 内核更新完成，正在重试导入...".to_string());
            }

            // Retry import
            let _ = std::fs::remove_dir_all(&install_dir);
            std::fs::create_dir_all(&install_dir)
                .map_err(|e| format!("创建安装目录失败: {}", e))?;

            let retry_code = run_wsl_cmd_logged(
                &[
                    "--import",
                    DISTRO_NAME,
                    &install_dir.to_string_lossy(),
                    &rootfs_path.to_string_lossy(),
                    "--version",
                    "2",
                ],
                &on_log,
            )
            .await;

            if retry_code != 0 {
                return Err(format!(
                    "WSL --import 失败 (exit code: {})，请确认 WSL2 已正确安装",
                    retry_code
                ));
            }
        }

        on_log("  ✓ 发行版导入完成".to_string());
    }

    // Step 3: Configure apt mirror (USTC)
    on_log("".to_string());
    on_log("▶ 配置 apt 国内镜像（USTC）...".to_string());
    let _ = run_wsl_distro_cmd(
        "LANG=C sed -i 's|http://archive.ubuntu.com|http://mirrors.ustc.edu.cn|g' /etc/apt/sources.list && \
         LANG=C sed -i 's|http://security.ubuntu.com|http://mirrors.ustc.edu.cn|g' /etc/apt/sources.list",
        &on_log,
    )
    .await;
    on_log("  ✓ 镜像配置完成".to_string());

    // Step 4: Configure wsl.conf (disable Windows PATH, enable systemd)
    on_log("".to_string());
    on_log("▶ 配置 wsl.conf（禁用 Windows PATH 互通）...".to_string());
    let _ = run_wsl_distro_cmd(
        "cat > /etc/wsl.conf << 'WSLCONF'\n[interop]\nappendWindowsPath = false\n\n[boot]\nsystemd = true\nWSLCONF",
        &on_log,
    )
    .await;
    on_log("  ✓ wsl.conf 配置完成".to_string());

    // Step 5: Restart distro and wait for systemd
    on_log("".to_string());
    on_log("▶ 正在重启发行版以应用配置...".to_string());
    let _ = run_wsl_cmd_logged(&["--terminate", DISTRO_NAME], &on_log).await;
    tokio::time::sleep(std::time::Duration::from_secs(3)).await;

    let mut verified = false;
    for i in 0..15 {
        let code = run_wsl_distro_cmd("echo ok", &|_| {}).await;
        if code == 0 {
            verified = true;
            break;
        }
        on_log(format!("  等待发行版就绪... ({}/15)", i + 1));
        tokio::time::sleep(std::time::Duration::from_secs(2)).await;
    }

    if !verified {
        return Err(
            "重启发行版后无法连接（已等待 30 秒），请确认 WSL2 已正确安装".to_string(),
        );
    }
    on_log("  ✓ 发行版重启完成".to_string());

    on_log("".to_string());
    on_log("✓ WSL2 + ClawDock 发行版安装完成".to_string());
    Ok(())
}

/// Non-Windows stub
#[cfg(not(target_os = "windows"))]
pub async fn install_distro(
    _resource_dir: std::path::PathBuf,
    on_log: impl Fn(String) + Send + Sync,
) -> Result<(), String> {
    on_log("非 Windows 平台，无需安装 WSL 发行版".to_string());
    Ok(())
}

// ── Windows helpers ────────────────────────────────────────────────────────

#[cfg(target_os = "windows")]
fn get_distro_install_dir() -> std::path::PathBuf {
    let local_app_data = std::env::var("LOCALAPPDATA")
        .unwrap_or_else(|_| dirs::data_local_dir().unwrap_or_default().to_string_lossy().to_string());
    std::path::PathBuf::from(local_app_data)
        .join("ClawDock")
        .join("WSL")
}

/// Run a `wsl <args>` command, log stdout/stderr, return exit code.
#[cfg(target_os = "windows")]
async fn run_wsl_cmd_logged(
    args: &[&str],
    on_log: &(dyn Fn(String) + Send + Sync),
) -> i32 {
    use tokio::io::{AsyncBufReadExt, BufReader};

    let mut cmd = tokio::process::Command::new("wsl");
    cmd.args(args)
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped());

    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            on_log(format!("  ✗ 启动 wsl 失败: {}", e));
            return -1;
        }
    };

    let stdout = child.stdout.take();
    let stderr = child.stderr.take();

    // Read stdout and stderr concurrently to avoid pipe buffer deadlock
    let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel::<String>();

    let tx_out = tx.clone();
    let stdout_task = tokio::spawn(async move {
        if let Some(stdout) = stdout {
            let mut lines = BufReader::new(stdout).lines();
            while let Ok(Some(line)) = lines.next_line().await {
                let trimmed = line.trim().to_string();
                if !trimmed.is_empty() {
                    let _ = tx_out.send(format!("  {}", trimmed));
                }
            }
        }
    });

    let tx_err = tx.clone();
    let stderr_task = tokio::spawn(async move {
        if let Some(stderr) = stderr {
            let mut lines = BufReader::new(stderr).lines();
            while let Ok(Some(line)) = lines.next_line().await {
                let trimmed = line.trim().to_string();
                if !trimmed.is_empty() {
                    let _ = tx_err.send(format!("  {}", trimmed));
                }
            }
        }
    });

    drop(tx);

    while let Some(line) = rx.recv().await {
        on_log(line);
    }

    let _ = stdout_task.await;
    let _ = stderr_task.await;

    match child.wait().await {
        Ok(status) => status.code().unwrap_or(-1),
        Err(_) => -1,
    }
}

/// Run a command inside the ClawDock distro, return exit code.
#[cfg(target_os = "windows")]
async fn run_wsl_distro_cmd(
    command: &str,
    on_log: &(dyn Fn(String) + Send + Sync),
) -> i32 {
    run_wsl_cmd_logged(
        &["-d", DISTRO_NAME, "--user", "root", "--", "bash", "-l", "-c", command],
        on_log,
    )
    .await
}

#[cfg(target_os = "windows")]
fn get_windows_build() -> String {
    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    match hklm.open_subkey(r"SOFTWARE\Microsoft\Windows NT\CurrentVersion") {
        Ok(key) => key
            .get_value::<String, _>("CurrentBuildNumber")
            .unwrap_or_else(|_| "0".to_string()),
        Err(_) => "0".to_string(),
    }
}

#[cfg(target_os = "windows")]
fn is_virtualization_enabled() -> bool {
    let output = std::process::Command::new("powershell")
        .args([
            "-NoProfile",
            "-Command",
            "$p = Get-WmiObject Win32_Processor; $c = Get-WmiObject Win32_ComputerSystem; ($p.VirtualizationFirmwareEnabled -eq $true) -or ($c.HypervisorPresent -eq $true)"
        ])
        .output();

    match output {
        Ok(o) => {
            let val = String::from_utf8_lossy(&o.stdout).trim().to_string();
            !val.eq_ignore_ascii_case("False")
        }
        Err(_) => true,
    }
}

#[cfg(target_os = "windows")]
fn is_wsl2_installed() -> bool {
    let output = std::process::Command::new("wsl")
        .args(["--status"])
        .output();

    match output {
        Ok(o) => {
            o.status.success()
                || String::from_utf8_lossy(&o.stdout).contains("WSL")
        }
        Err(_) => false,
    }
}

#[cfg(target_os = "windows")]
fn is_distro_installed() -> bool {
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    match hkcu.open_subkey(r"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss") {
        Ok(lxss) => {
            for name in lxss.enum_keys().filter_map(|k| k.ok()) {
                if let Ok(sub) = lxss.open_subkey(&name) {
                    if let Ok(dist_name) = sub.get_value::<String, _>("DistributionName") {
                        if dist_name.eq_ignore_ascii_case(DISTRO_NAME) {
                            return true;
                        }
                    }
                }
            }
            false
        }
        Err(_) => false,
    }
}
