use tauri::{AppHandle, Emitter, State};

use crate::services::config_service::{
    ChannelConfig, ModelConfig, OpenClawFullConfig, ProviderConfig,
};
use crate::AppState;

#[tauri::command]
pub async fn config_read(state: State<'_, AppState>) -> Result<OpenClawFullConfig, String> {
    Ok(state.config.read_full_config().await)
}

#[tauri::command]
pub async fn config_save_models(
    state: State<'_, AppState>,
    config: ModelConfig,
) -> Result<Vec<String>, String> {
    Ok(state.config.save_model_config(&config).await)
}

#[tauri::command]
pub async fn config_save_provider(
    state: State<'_, AppState>,
    provider: ProviderConfig,
) -> Result<Vec<String>, String> {
    Ok(state.config.save_provider(&provider).await)
}

#[tauri::command]
pub async fn config_delete_provider(
    state: State<'_, AppState>,
    name: String,
) -> Result<bool, String> {
    state.config.delete_provider(&name).await
}

#[tauri::command]
pub async fn config_save_provider_api_key(
    state: State<'_, AppState>,
    provider_name: String,
    api_key: String,
) -> Result<bool, String> {
    state
        .config
        .save_provider_api_key(&provider_name, &api_key)
        .await
}

#[tauri::command]
pub async fn config_save_channel(
    state: State<'_, AppState>,
    channel: ChannelConfig,
) -> Result<Vec<String>, String> {
    Ok(state.config.save_channel(&channel).await)
}

#[tauri::command]
pub async fn config_list_models(state: State<'_, AppState>) -> Result<Vec<String>, String> {
    Ok(state.config.list_available_models().await)
}

#[tauri::command]
pub async fn config_install_dingtalk(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<bool, String> {
    let app_log = app.clone();
    state
        .config
        .install_dingtalk_plugin(&move |msg| {
            let _ = app_log.emit("install-progress", &msg);
        })
        .await
}
