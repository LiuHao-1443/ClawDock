use tauri::{AppHandle, Emitter, Manager, State};

use crate::services::install_state::{InstallPhase, InstallState, InstallStateService};
use crate::services::wsl_service::{self, WslCheckResult};
use crate::AppState;

#[tauri::command]
pub fn install_state_load() -> Result<InstallState, String> {
    Ok(InstallStateService::load())
}

#[tauri::command]
pub fn install_state_save(state: InstallState) -> Result<(), String> {
    InstallStateService::save(&state)
}

#[tauri::command]
pub fn install_state_save_phase(phase: InstallPhase) -> Result<(), String> {
    InstallStateService::save_phase(phase)
}

#[tauri::command]
pub fn install_state_mark_wsl2_done() -> Result<(), String> {
    InstallStateService::mark_wsl2_done()
}

#[tauri::command]
pub fn install_state_mark_openclaw_done() -> Result<(), String> {
    InstallStateService::mark_openclaw_done()
}

#[tauri::command]
pub fn install_state_reset() -> Result<(), String> {
    InstallStateService::reset()
}

#[tauri::command]
pub fn wsl_check() -> Result<WslCheckResult, String> {
    Ok(wsl_service::check())
}

#[tauri::command]
pub async fn wsl_install_distro(app: AppHandle) -> Result<(), String> {
    let resource_dir = app
        .path()
        .resource_dir()
        .map_err(|e| format!("Failed to get resource dir: {}", e))?;

    let app_log = app.clone();
    wsl_service::install_distro(resource_dir, move |msg| {
        let _ = app_log.emit("install-progress", &msg);
    })
    .await
}

#[tauri::command]
pub async fn openclaw_install(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let app_log = app.clone();
    state
        .openclaw
        .install(&move |msg| {
            let _ = app_log.emit("install-progress", &msg);
        })
        .await
}

#[tauri::command]
pub async fn openclaw_uninstall(
    app: AppHandle,
    state: State<'_, AppState>,
    remove_distro: bool,
) -> Result<(), String> {
    let app_log = app.clone();

    // 1. Stop gateway
    let _ = app.emit("install-progress", "▶ Stopping Gateway...");
    let app_stop = app.clone();
    let _ = state.gateway.stop(move |msg| {
        let _ = app_stop.emit("install-progress", &msg);
    }).await;
    let _ = app.emit("install-progress", "  ✓ Gateway stopped");

    // 2. Uninstall OpenClaw
    state
        .openclaw
        .uninstall(&move |msg| {
            let _ = app_log.emit("install-progress", &msg);
        })
        .await?;

    // 3. Optionally remove WSL distro (Windows only)
    if remove_distro && state.shell.platform() == "windows" {
        let _ = app.emit("install-progress", "▶ Removing WSL distro...");
        #[cfg(target_os = "windows")]
        const CREATE_NO_WINDOW: u32 = 0x08000000;
        let mut cmd = tokio::process::Command::new("wsl");
        cmd.args(["--unregister", wsl_service::DISTRO_NAME]);
        #[cfg(target_os = "windows")]
        cmd.creation_flags(CREATE_NO_WINDOW);
        let output = cmd
            .output()
            .await
            .map_err(|e| format!("Failed to unregister distro: {}", e))?;

        if output.status.success() {
            let _ = app.emit("install-progress", "  ✓ WSL distro removed");
        } else {
            let stderr = String::from_utf8_lossy(&output.stderr);
            let _ = app.emit("install-progress", &format!("  ⚠ {}", stderr.trim()));
        }
    }

    // 4. Reset install state
    let _ = app.emit("install-progress", "▶ Resetting install state...");
    InstallStateService::reset()?;
    let _ = app.emit("install-progress", "  ✓ Install state reset");

    let _ = app.emit("install-progress", "✓ Uninstall complete");
    Ok(())
}

#[tauri::command]
pub async fn openclaw_get_installed_version(
    state: State<'_, AppState>,
) -> Result<Option<String>, String> {
    Ok(state.openclaw.get_installed_version().await)
}

#[tauri::command]
pub async fn openclaw_get_latest_version(
    state: State<'_, AppState>,
) -> Result<Option<String>, String> {
    Ok(state.openclaw.get_latest_version().await)
}

#[tauri::command]
pub async fn openclaw_update(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<bool, String> {
    let app_log = app.clone();
    state
        .openclaw
        .update(&move |msg| {
            let _ = app_log.emit("install-progress", &msg);
        })
        .await
}
