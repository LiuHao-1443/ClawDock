import { create } from "zustand";

export type Locale = "zh" | "en";

interface I18nStore {
  locale: Locale;
  setLocale: (locale: Locale) => void;
  toggleLocale: () => void;
}

const detectLocale = (): Locale => {
  const lang = navigator.language || "";
  return lang.startsWith("zh") ? "zh" : "en";
};

export const useI18n = create<I18nStore>((set) => ({
  locale: detectLocale(),
  setLocale: (locale) => set({ locale }),
  toggleLocale: () =>
    set((s) => ({ locale: s.locale === "zh" ? "en" : "zh" })),
}));

const translations = {
  // ── Install Page ──────────────────────────────────────────────
  "install.welcome": { zh: "欢迎使用 ClawDock", en: "Welcome to ClawDock" },
  "install.desc": {
    zh: "ClawDock 帮助您安装和管理 OpenClaw（AI 网关）。",
    en: "ClawDock helps you install and manage OpenClaw, an AI gateway.",
  },
  "install.descWsl": {
    zh: "它将为您自动配置 WSL2 和 Ubuntu 环境。",
    en: "It will set up WSL2 and Ubuntu for you.",
  },
  "install.getStarted": { zh: "开始安装", en: "Get Started" },
  "install.systemCheck": { zh: "系统检查", en: "System Check" },
  "install.windowsBuild": { zh: "Windows 版本", en: "Windows Build" },
  "install.virtualization": { zh: "虚拟化", en: "Virtualization" },
  "install.wsl2": { zh: "WSL2", en: "WSL2" },
  "install.distro": { zh: "ClawDock 发行版", en: "ClawDock Distro" },
  "install.systemReady": { zh: "系统就绪", en: "System Ready" },
  "install.installWslAndOc": {
    zh: "安装 WSL + OpenClaw",
    en: "Install WSL + OpenClaw",
  },
  "install.installOc": { zh: "安装 OpenClaw", en: "Install OpenClaw" },
  "install.checking": { zh: "正在检测系统...", en: "Checking system..." },
  "install.installingWsl": {
    zh: "正在安装 WSL2 + ClawDock 发行版...",
    en: "Installing WSL2 + ClawDock Distro...",
  },
  "install.installingOc": {
    zh: "正在安装 OpenClaw...",
    en: "Installing OpenClaw...",
  },
  "install.installing": { zh: "安装中...", en: "Installing..." },
  "install.complete": { zh: "安装完成", en: "Installation Complete" },
  "install.completeDesc": {
    zh: "OpenClaw 已就绪，现在可以启动 Gateway 了。",
    en: "OpenClaw is ready to use. You can now start the Gateway.",
  },
  "install.launch": { zh: "启动 ClawDock", en: "Launch ClawDock" },

  // ── Toolbar ───────────────────────────────────────────────────
  "toolbar.start": { zh: "启动", en: "Start" },
  "toolbar.stop": { zh: "停止", en: "Stop" },
  "toolbar.restart": { zh: "重启", en: "Restart" },
  "toolbar.openBrowser": { zh: "在浏览器中打开", en: "Open in Browser" },
  "toolbar.console": { zh: "控制台", en: "Console" },
  "toolbar.settings": { zh: "系统设置", en: "Settings" },
  "toolbar.uninstall": { zh: "卸载", en: "Uninstall" },

  // ── Console ───────────────────────────────────────────────────
  "console.title": { zh: "控制台", en: "Console" },
  "console.clear": { zh: "清空", en: "Clear" },
  "console.empty": { zh: "暂无日志输出", en: "No log output yet." },

  // ── Status Bar ────────────────────────────────────────────────
  "status.gateway": { zh: "网关", en: "Gateway" },
  "status.running": { zh: "运行中", en: "Running" },
  "status.starting": { zh: "启动中", en: "Starting" },
  "status.stopped": { zh: "已停止", en: "Stopped" },
  "status.error": { zh: "错误", en: "Error" },

  // ── Main Page ─────────────────────────────────────────────────
  "main.starting": { zh: "正在启动 Gateway...", en: "Starting Gateway..." },
  "main.running": { zh: "运行中", en: "is Running" },
  "main.runningHint": {
    zh: "Gateway 已就绪，点击下方按钮打开控制面板",
    en: "Gateway is ready. Click below to open the dashboard.",
  },
  "main.openDashboard": { zh: "打开控制面板", en: "Open Dashboard" },
  "main.error": { zh: "Gateway 错误", en: "Gateway Error" },
  "main.stopped": { zh: "未运行", en: "Not Running" },
  "main.stoppedHint": {
    zh: "点击「启动」按钮开启 Gateway",
    en: 'Click "Start" to start the Gateway',
  },

  // ── Info Panel ───────────────────────────────────────────────
  "info.openclawVersion": { zh: "OpenClaw 版本", en: "OpenClaw Version" },
  "info.configuredModel": { zh: "当前模型", en: "Configured Model" },
  "info.noModel": { zh: "未配置", en: "Not configured" },
  "info.latestVersion": { zh: "最新版本", en: "Latest Version" },
  "info.upToDate": { zh: "已是最新", en: "Up to date" },
  "info.updateAvailable": { zh: "可升级", en: "Update available" },
  "info.updating": { zh: "升级中...", en: "Updating..." },
  "info.update": { zh: "升级", en: "Update" },
  "info.checking": { zh: "检查中...", en: "Checking..." },
  "info.unknown": { zh: "未知", en: "Unknown" },

  // ── Settings Panel ────────────────────────────────────────────
  "settings.title": { zh: "系统设置", en: "Settings" },
  "settings.models": { zh: "模型配置", en: "Model Config" },
  "settings.channels": { zh: "渠道配置", en: "Channel Config" },
  "settings.back": { zh: "返回", en: "Back" },

  // ── Model Tab ─────────────────────────────────────────────────
  "model.loading": { zh: "加载配置中...", en: "Loading config..." },
  "model.primary": { zh: "主模型", en: "Primary Model" },
  "model.primaryDesc": { zh: "请求时优先使用的模型", en: "The model used by default for requests" },
  "model.fallbacks": { zh: "回退模型", en: "Fallback Models" },
  "model.fallbacksDesc": { zh: "主模型不可用时按顺序尝试的备选模型", en: "Models tried in order when the primary is unavailable" },
  "model.addFallback": { zh: "添加回退模型", en: "Add Fallback" },
  "model.providers": { zh: "自定义提供商", en: "Custom Providers" },
  "model.providersDesc": { zh: "添加自定义 API 提供商及其模型", en: "Add custom API providers and their models" },
  "model.provider": { zh: "提供商", en: "Provider" },
  "model.selectProvider": { zh: "选择提供商", en: "Select provider" },
  "model.modelLabel": { zh: "模型", en: "Model" },
  "model.selectModel": { zh: "选择模型", en: "Select model" },
  "model.providerFormDesc": { zh: "配置提供商的 API 地址、密钥和可用模型", en: "Configure the provider's API endpoint, key, and available models" },
  "model.noUrl": { zh: "未配置 URL", en: "No URL" },
  "model.models": { zh: "个模型", en: "models" },
  "model.edit": { zh: "编辑", en: "Edit" },
  "model.delete": { zh: "删除", en: "Delete" },
  "model.addProvider": { zh: "添加提供商", en: "Add Provider" },
  "model.editProvider": { zh: "编辑提供商", en: "Edit Provider" },
  "model.newProvider": { zh: "新建提供商", en: "New Provider" },
  "model.providerName": { zh: "提供商名称", en: "Provider Name" },
  "model.baseUrl": {
    zh: "Base URL（如 https://api.openai.com/v1）",
    en: "Base URL (e.g. https://api.openai.com/v1)",
  },
  "model.apiKey": { zh: "API Key", en: "API Key" },
  "model.saveKey": { zh: "保存", en: "Save" },
  "model.modelName": { zh: "模型名称", en: "Model name" },
  "model.modelId": { zh: "模型 ID", en: "Model ID" },
  "model.addModel": { zh: "添加模型", en: "Add Model" },
  "model.saveProvider": { zh: "保存提供商", en: "Save Provider" },
  "model.cancel": { zh: "取消", en: "Cancel" },
  "model.saving": { zh: "保存中...", en: "Saving..." },
  "model.saved": { zh: "已保存", en: "Saved" },
  "model.saveError": { zh: "保存失败，请重试", en: "Error - Retry" },
  "model.saveConfig": { zh: "保存模型配置", en: "Save Model Config" },
  "model.apiType": { zh: "API 类型", en: "API Type" },
  "model.apiOpenai": { zh: "OpenAI 兼容", en: "OpenAI Compatible" },
  "model.apiAnthropic": { zh: "Anthropic", en: "Anthropic" },
  "model.apiGoogle": { zh: "Google", en: "Google" },
  "model.nameRequired": { zh: "请输入提供商名称", en: "Provider name is required" },
  "model.urlRequired": { zh: "请输入 Base URL", en: "Base URL is required" },

  // ── Channel Tab ───────────────────────────────────────────────
  "channel.feishu": { zh: "飞书", en: "Feishu" },
  "channel.dingtalk": { zh: "钉钉", en: "DingTalk" },
  "channel.enable": { zh: "启用", en: "Enable" },
  "channel.connectionMode": { zh: "连接模式", en: "Connection Mode" },
  "channel.verifyToken": { zh: "验证 Token", en: "Verify Token" },
  "channel.saveFeishu": { zh: "保存飞书配置", en: "Save Feishu Config" },
  "channel.dmPolicy": { zh: "私聊策略", en: "DM Policy" },
  "channel.groupPolicy": { zh: "群聊策略", en: "Group Policy" },
  "channel.policyOpen": { zh: "开放", en: "Open" },
  "channel.policyClose": { zh: "关闭", en: "Close" },
  "channel.policyAllowlist": { zh: "白名单", en: "Allowlist" },
  "channel.saveDingtalk": { zh: "保存钉钉配置", en: "Save DingTalk Config" },
  "channel.installPlugin": { zh: "安装插件", en: "Install Plugin" },
  "channel.pluginInstalling": { zh: "安装中...", en: "Installing..." },
  "channel.saved": { zh: "已保存", en: "Saved" },
  "channel.saveFailed": {
    zh: "保存失败，请查看日志",
    en: "Save failed. Please check logs.",
  },
  "channel.on": { zh: "已开启", en: "ON" },
  "channel.off": { zh: "已关闭", en: "OFF" },
  "channel.appId": { zh: "App ID", en: "App ID" },
  "channel.appSecret": { zh: "App Secret", en: "App Secret" },
  "channel.clientId": { zh: "Client ID", en: "Client ID" },
  "channel.clientSecret": { zh: "Client Secret", en: "Client Secret" },

  // ── Uninstall ──────────────────────────────────────────────────
  "uninstall.title": { zh: "卸载 ClawDock", en: "Uninstall ClawDock" },
  "uninstall.desc": {
    zh: "以下操作将从您的电脑上移除 ClawDock 组件",
    en: "This will remove ClawDock components from your computer",
  },
  "uninstall.stopGateway": { zh: "停止并移除 Gateway", en: "Stop and remove Gateway" },
  "uninstall.removeOpenclaw": { zh: "卸载 OpenClaw", en: "Uninstall OpenClaw" },
  "uninstall.resetState": { zh: "清除安装状态", en: "Reset install state" },
  "uninstall.removeDistro": { zh: "同时移除 WSL2 发行版", en: "Also remove WSL2 distro" },
  "uninstall.removeDistroHint": {
    zh: "将删除 WSL2 中的 ClawDock 环境（不影响其他发行版）",
    en: "Removes the ClawDock environment in WSL2 (does not affect other distros)",
  },
  "uninstall.confirm": { zh: "开始卸载", en: "Start Uninstall" },
  "uninstall.running": { zh: "正在卸载...", en: "Uninstalling..." },
  "uninstall.complete": { zh: "卸载完成", en: "Uninstall Complete" },
  "uninstall.completeHint": {
    zh: "重新运行程序即可重新安装",
    en: "Run the app again to reinstall",
  },
  "uninstall.exit": { zh: "退出应用", en: "Exit App" },
  "uninstall.failed": { zh: "卸载失败", en: "Uninstall Failed" },
  "uninstall.retry": { zh: "重试", en: "Retry" },

  // ── Common ────────────────────────────────────────────────────
  "common.lang": { zh: "EN", en: "中" },
} as const;

export type TranslationKey = keyof typeof translations;

export function useT() {
  const locale = useI18n((s) => s.locale);
  return (key: TranslationKey) => translations[key][locale];
}
