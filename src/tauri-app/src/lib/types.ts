export type GatewayStatus = "Stopped" | "Starting" | "Running" | "Error";

export type InstallPhase =
  | "notStarted"
  | "wsl2Enabled"
  | "wsl2Reboot"
  | "distroImported"
  | "distroConfigured"
  | "openClawInstalling"
  | "complete";

export interface GatewayState {
  status: GatewayStatus;
  dashboardUrl: string | null;
  authToken: string | null;
  errorMessage: string | null;
}

export interface LogEntry {
  id: number;
  timestamp: number;
  message: string;
}

export interface ModelConfig {
  primaryModel: string;
  fallbackModels: string[];
}

export interface ProviderModel {
  name: string;
  id: string;
  contextWindow?: number;
  maxTokens?: number;
  reasoning?: boolean;
}

export interface ProviderConfig {
  name: string;
  baseUrl: string;
  apiKey: string;
  api: string;
  models: ProviderModel[];
}

export interface ChannelConfig {
  name: string;
  enabled: boolean;
  properties: Record<string, string>;
}

export interface OpenClawFullConfig {
  model: ModelConfig;
  providers: ProviderConfig[];
  channels: ChannelConfig[];
  parseError?: string;
}

export interface InstallState {
  installed: boolean;
  phase: InstallPhase;
  wsl2Installed: boolean;
  distroInstalled: boolean;
  openclawInstalled: boolean;
  installDate?: string;
  version: string;
}

export interface WslCheckResult {
  windowsVersionOk: boolean;
  virtualizationEnabled: boolean;
  wsl2Installed: boolean;
  distroInstalled: boolean;
  windowsBuild: string;
}

export interface InstallProgress {
  step: string;
  progress: number;
  message: string;
}
