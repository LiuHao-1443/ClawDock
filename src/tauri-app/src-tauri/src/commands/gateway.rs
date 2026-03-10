use std::sync::Arc;
use tauri::{AppHandle, Emitter, Manager, State};

use crate::services::gateway_service::GatewayStatus;
use crate::AppState;

#[tauri::command]
pub async fn gateway_start(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let gateway = Arc::clone(&state.gateway);
    let app_log = app.clone();
    let app_status = app.clone();

    gateway
        .start(
            move |msg| {
                let _ = app_log.emit("gateway-log", &msg);
            },
            move |status| {
                let _ = app_status.emit("gateway-status", &status);
            },
        )
        .await
}

#[tauri::command]
pub async fn gateway_stop(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let app_log = app.clone();
    let result = state
        .gateway
        .stop(move |msg| {
            let _ = app_log.emit("gateway-log", &msg);
        })
        .await;
    // Emit status after stop so frontend store stays in sync
    let _ = app.emit("gateway-status", &GatewayStatus::Stopped);
    result
}

#[tauri::command]
pub async fn gateway_restart(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let gateway = Arc::clone(&state.gateway);
    let app_log = app.clone();
    let app_status = app.clone();

    gateway
        .restart(
            move |msg| {
                let _ = app_log.emit("gateway-log", &msg);
            },
            move |status| {
                let _ = app_status.emit("gateway-status", &status);
            },
        )
        .await
}

#[tauri::command]
pub async fn gateway_status(state: State<'_, AppState>) -> Result<GatewayStatus, String> {
    Ok(state.gateway.status().await)
}

#[tauri::command]
pub async fn gateway_last_error(state: State<'_, AppState>) -> Result<String, String> {
    Ok(state.gateway.last_error().await)
}

#[tauri::command]
pub async fn gateway_dashboard_url(state: State<'_, AppState>) -> Result<String, String> {
    Ok(state.gateway.dashboard_url().await)
}

#[tauri::command]
pub async fn gateway_is_running(state: State<'_, AppState>) -> Result<bool, String> {
    Ok(state.gateway.is_running().await)
}

/// Show the dashboard as a child webview inside the main window content area.
/// This avoids iframe CSP/connection issues by using a native webview.
#[tauri::command]
pub async fn gateway_open_dashboard(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let url = state.gateway.dashboard_url().await;
    let parsed_url: tauri::Url = url.parse().map_err(|e| format!("Invalid URL: {}", e))?;

    // If dashboard webview already exists, just navigate to latest URL
    if let Some(wv) = app.get_webview("dashboard") {
        let _ = wv.navigate(parsed_url);
        return Ok(());
    }

    let window = app.get_window("main").ok_or("Main window not found")?;
    let phy_size = window.inner_size().map_err(|e: tauri::Error| e.to_string())?;
    let scale = window.scale_factor().map_err(|e: tauri::Error| e.to_string())?;

    let toolbar_h = 44.0;
    let statusbar_h = 24.0;
    let w = phy_size.width as f64 / scale;
    let h = ((phy_size.height as f64 / scale) - toolbar_h - statusbar_h).max(100.0);

    let builder = tauri::webview::WebviewBuilder::new(
        "dashboard",
        tauri::WebviewUrl::External(parsed_url),
    );

    window
        .add_child(
            builder,
            tauri::LogicalPosition::new(0.0, toolbar_h),
            tauri::LogicalSize::new(w, h),
        )
        .map_err(|e| format!("Failed to create dashboard webview: {}", e))?;

    Ok(())
}

/// Hide (close/destroy) the dashboard child webview.
#[tauri::command]
pub async fn gateway_hide_dashboard(app: AppHandle) -> Result<(), String> {
    if let Some(wv) = app.get_webview("dashboard") {
        wv.close().map_err(|e: tauri::Error| e.to_string())?;
    }
    Ok(())
}

/// Toggle dashboard webview visibility (show/hide without destroying).
#[tauri::command]
pub async fn gateway_set_dashboard_visible(app: AppHandle, visible: bool) -> Result<(), String> {
    if let Some(wv) = app.get_webview("dashboard") {
        if visible {
            wv.show().map_err(|e: tauri::Error| e.to_string())?;
        } else {
            wv.hide().map_err(|e: tauri::Error| e.to_string())?;
        }
    }
    Ok(())
}
