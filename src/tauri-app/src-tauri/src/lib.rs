mod commands;
mod platform;
mod services;

use std::sync::Arc;

use tauri::{
    menu::{Menu, MenuItem},
    tray::TrayIconBuilder,
    Emitter, Manager, WindowEvent,
};

use commands::config::*;
use commands::gateway::*;
use commands::install::*;
use commands::system::*;

use services::config_service::ConfigService;
use services::gateway_service::GatewayService;
use services::openclaw_service::OpenClawService;
use services::shell_backend::ShellBackend;

/// Shared application state, managed by Tauri.
pub struct AppState {
    pub gateway: Arc<GatewayService>,
    pub config: Arc<ConfigService>,
    pub openclaw: Arc<OpenClawService>,
    pub shell: Arc<dyn ShellBackend>,
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let shell: Arc<dyn ShellBackend> = platform::create_shell_backend().into();
    let gateway = Arc::new(GatewayService::new(Arc::clone(&shell)));
    let config = Arc::new(ConfigService::new(Arc::clone(&shell)));
    let openclaw = Arc::new(OpenClawService::new(Arc::clone(&shell)));

    let app_state = AppState {
        gateway,
        config,
        openclaw,
        shell,
    };

    tauri::Builder::default()
        .plugin(tauri_plugin_os::init())
        .plugin(tauri_plugin_shell::init())
        // Window size is managed manually (small for install, large for main)
        .plugin(tauri_plugin_autostart::init(
            tauri_plugin_autostart::MacosLauncher::LaunchAgent,
            Some(vec![]),
        ))
        .manage(app_state)
        .setup(|app| {
            // System tray
            let show = MenuItem::with_id(app, "show", "Open ClawDock", true, None::<&str>)?;
            let start = MenuItem::with_id(app, "start", "Start Gateway", true, None::<&str>)?;
            let stop = MenuItem::with_id(app, "stop", "Stop Gateway", true, None::<&str>)?;
            let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&show, &start, &stop, &quit])?;

            let _tray = TrayIconBuilder::new()
                .tooltip("ClawDock")
                .icon(app.default_window_icon().cloned().expect("default window icon must be set in tauri.conf.json"))
                .menu(&menu)
                .on_menu_event(move |app, event| match event.id.as_ref() {
                    "show" => {
                        if let Some(window) = app.get_webview_window("main") {
                            let _ = window.show();
                            let _ = window.set_focus();
                        }
                    }
                    "start" => {
                        let _ = app.emit("tray-action", "start");
                    }
                    "stop" => {
                        let _ = app.emit("tray-action", "stop");
                    }
                    "quit" => {
                        app.exit(0);
                    }
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    match event {
                        tauri::tray::TrayIconEvent::Click { button: tauri::tray::MouseButton::Left, .. }
                        | tauri::tray::TrayIconEvent::DoubleClick { .. } => {
                            let app = tray.app_handle();
                            if let Some(window) = app.get_webview_window("main") {
                                let _ = window.unminimize();
                                let _ = window.show();
                                let _ = window.set_focus();
                            }
                        }
                        _ => {}
                    }
                })
                .build(app)?;

            Ok(())
        })
        .on_window_event(|window, event| {
            match event {
                WindowEvent::CloseRequested { api, .. } => {
                    // Hide to tray instead of closing
                    let _ = window.hide();
                    api.prevent_close();
                }
                WindowEvent::Resized(size) => {
                    // Resize the dashboard child webview if it exists
                    if let Some(wv) = window.app_handle().get_webview("dashboard") {
                        if let Ok(scale) = window.scale_factor() {
                            let toolbar_h = 44.0;
                            let statusbar_h = 24.0;
                            let w = size.width as f64 / scale;
                            let h = ((size.height as f64 / scale) - toolbar_h - statusbar_h).max(100.0);
                            let _ = wv.set_size(tauri::LogicalSize::<f64>::new(w, h));
                        }
                    }
                }
                _ => {}
            }
        })
        .invoke_handler(tauri::generate_handler![
            // System
            get_platform,
            app_exit,
            // Gateway
            gateway_start,
            gateway_stop,
            gateway_restart,
            gateway_status,
            gateway_dashboard_url,
            gateway_is_running,
            gateway_last_error,
            gateway_open_dashboard,
            gateway_hide_dashboard,
            gateway_set_dashboard_visible,
            // Config
            config_read,
            config_save_models,
            config_save_provider,
            config_delete_provider,
            config_save_provider_api_key,
            config_save_channel,
            config_list_models,
            config_install_dingtalk,
            // Install
            install_state_load,
            install_state_save,
            install_state_save_phase,
            install_state_mark_wsl2_done,
            install_state_mark_openclaw_done,
            install_state_reset,
            wsl_check,
            wsl_install_distro,
            openclaw_install,
            openclaw_get_installed_version,
            openclaw_get_latest_version,
            openclaw_update,
            openclaw_uninstall,
        ])
        .run(tauri::generate_context!())
        .expect("error while running ClawDock");
}
