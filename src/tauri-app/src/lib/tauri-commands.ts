import { invoke } from "@tauri-apps/api/core";
import type {
  GatewayStatus,
  ModelConfig,
  ProviderConfig,
  ChannelConfig,
  OpenClawFullConfig,
  InstallState,
  InstallPhase,
  WslCheckResult,
} from "./types";

// ── System ────────────────────────────────────────────────────────────────

export async function getPlatform(): Promise<string> {
  return invoke<string>("get_platform");
}

export async function appExit(): Promise<void> {
  return invoke("app_exit");
}

// ── Gateway ───────────────────────────────────────────────────────────────

export async function gatewayStart(): Promise<void> {
  return invoke("gateway_start");
}

export async function gatewayStop(): Promise<void> {
  return invoke("gateway_stop");
}

export async function gatewayRestart(): Promise<void> {
  return invoke("gateway_restart");
}

export async function gatewayStatus(): Promise<GatewayStatus> {
  return invoke<GatewayStatus>("gateway_status");
}

export async function gatewayDashboardUrl(): Promise<string> {
  return invoke<string>("gateway_dashboard_url");
}

export async function gatewayIsRunning(): Promise<boolean> {
  return invoke<boolean>("gateway_is_running");
}

export async function gatewayLastError(): Promise<string> {
  return invoke<string>("gateway_last_error");
}

export async function gatewayOpenDashboard(): Promise<void> {
  return invoke("gateway_open_dashboard");
}

export async function gatewayHideDashboard(): Promise<void> {
  return invoke("gateway_hide_dashboard");
}

export async function gatewaySetDashboardVisible(visible: boolean): Promise<void> {
  return invoke("gateway_set_dashboard_visible", { visible });
}

// ── Config ────────────────────────────────────────────────────────────────

export async function configRead(): Promise<OpenClawFullConfig> {
  return invoke<OpenClawFullConfig>("config_read");
}

export async function configSaveModels(
  config: ModelConfig,
): Promise<string[]> {
  return invoke<string[]>("config_save_models", { config });
}

export async function configSaveProvider(
  provider: ProviderConfig,
): Promise<string[]> {
  return invoke<string[]>("config_save_provider", { provider });
}

export async function configDeleteProvider(name: string): Promise<boolean> {
  return invoke<boolean>("config_delete_provider", { name });
}

export async function configSaveProviderApiKey(
  providerName: string,
  apiKey: string,
): Promise<boolean> {
  return invoke<boolean>("config_save_provider_api_key", {
    provider_name: providerName,
    api_key: apiKey,
  });
}

export async function configSaveChannel(
  channel: ChannelConfig,
): Promise<string[]> {
  return invoke<string[]>("config_save_channel", { channel });
}

export async function configListModels(): Promise<string[]> {
  return invoke<string[]>("config_list_models");
}

export async function configInstallDingtalk(): Promise<boolean> {
  return invoke<boolean>("config_install_dingtalk");
}

// ── Install ───────────────────────────────────────────────────────────────

export async function installStateLoad(): Promise<InstallState> {
  return invoke<InstallState>("install_state_load");
}

export async function installStateSave(state: InstallState): Promise<void> {
  return invoke("install_state_save", { state });
}

export async function installStateSavePhase(
  phase: InstallPhase,
): Promise<void> {
  return invoke("install_state_save_phase", { phase });
}

export async function installStateMarkWsl2Done(): Promise<void> {
  return invoke("install_state_mark_wsl2_done");
}

export async function installStateMarkOpenclawDone(): Promise<void> {
  return invoke("install_state_mark_openclaw_done");
}

export async function installStateReset(): Promise<void> {
  return invoke("install_state_reset");
}

export async function wslCheck(): Promise<WslCheckResult> {
  return invoke<WslCheckResult>("wsl_check");
}

export async function wslInstallDistro(): Promise<void> {
  return invoke("wsl_install_distro");
}

export async function openclawInstall(): Promise<void> {
  return invoke("openclaw_install");
}

export async function openclawGetInstalledVersion(): Promise<string | null> {
  return invoke<string | null>("openclaw_get_installed_version");
}

export async function openclawGetLatestVersion(): Promise<string | null> {
  return invoke<string | null>("openclaw_get_latest_version");
}

export async function openclawUpdate(): Promise<boolean> {
  return invoke<boolean>("openclaw_update");
}

export async function openclawUninstall(removeDistro: boolean): Promise<void> {
  return invoke("openclaw_uninstall", { remove_distro: removeDistro });
}
