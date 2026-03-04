using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClawDock.Services;

// ── Data Models ────────────────────────────────────────────────────────────

public class ModelConfig
{
    public string PrimaryModel { get; set; } = "";
    public List<string> FallbackModels { get; set; } = new();
}

public class ProviderModel
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
}

public class ProviderConfig
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Api { get; set; } = "openai";
    public List<ProviderModel> Models { get; set; } = new();
}

public class ChannelConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new();
}

public class OpenClawFullConfig
{
    public ModelConfig Model { get; set; } = new();
    public List<ProviderConfig> Providers { get; set; } = new();
    public List<ChannelConfig> Channels { get; set; } = new();
}

// ── Service ────────────────────────────────────────────────────────────────

public class OpenClawConfigService
{
    /// <summary>Read and parse the full openclaw.json config from WSL</summary>
    public async Task<OpenClawFullConfig> ReadFullConfigAsync()
    {
        var output = await Task.Run(() =>
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = $"-d {WslService.DistroName} --user root -- bash -l -c \"cat ~/.openclaw/openclaw.json\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            });
            if (p == null) return "";

            string stdout = "";
            var outTask = Task.Run(() => stdout = p.StandardOutput.ReadToEnd());
            var errTask = Task.Run(() => p.StandardError.ReadToEnd());

            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(true); } catch { }
                return "";
            }
            outTask.Wait(5_000);
            errTask.Wait(5_000);
            return stdout;
        });

        var config = new OpenClawFullConfig();
        var jsonStart = output.IndexOf('{');
        if (jsonStart < 0) return config;

        try
        {
            using var doc = JsonDocument.Parse(output[jsonStart..]);
            config.Model = ParseModelConfig(doc);
            config.Providers = ParseProviders(doc);
            config.Channels = ParseChannels(doc);
        }
        catch { /* return empty config on parse failure */ }

        return config;
    }

    private static ModelConfig ParseModelConfig(JsonDocument doc)
    {
        var model = new ModelConfig();
        var root = doc.RootElement;

        try
        {
            if (root.TryGetProperty("agents", out var agents) &&
                agents.TryGetProperty("defaults", out var defaults) &&
                defaults.TryGetProperty("model", out var modelEl))
            {
                if (modelEl.TryGetProperty("primary", out var primary))
                    model.PrimaryModel = primary.GetString() ?? "";

                if (modelEl.TryGetProperty("fallbacks", out var fallbacks) &&
                    fallbacks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fallbacks.EnumerateArray())
                    {
                        var val = f.GetString();
                        if (!string.IsNullOrEmpty(val))
                            model.FallbackModels.Add(val);
                    }
                }
            }
        }
        catch { }

        return model;
    }

    private static List<ProviderConfig> ParseProviders(JsonDocument doc)
    {
        var providers = new List<ProviderConfig>();
        var root = doc.RootElement;

        try
        {
            if (root.TryGetProperty("models", out var modelsEl) &&
                modelsEl.TryGetProperty("providers", out var providersEl) &&
                providersEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in providersEl.EnumerateObject())
                {
                    var p = new ProviderConfig { Name = prop.Name };
                    var val = prop.Value;

                    if (val.TryGetProperty("baseUrl", out var baseUrl))
                        p.BaseUrl = baseUrl.GetString() ?? "";
                    if (val.TryGetProperty("apiKey", out var apiKey))
                        p.ApiKey = apiKey.GetString() ?? "";
                    if (val.TryGetProperty("api", out var api))
                        p.Api = api.GetString() ?? "openai";
                    if (val.TryGetProperty("models", out var models) &&
                        models.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in models.EnumerateArray())
                        {
                            var pm = new ProviderModel();
                            if (m.TryGetProperty("name", out var name))
                                pm.Name = name.GetString() ?? "";
                            if (m.TryGetProperty("id", out var id))
                                pm.Id = id.GetString() ?? "";
                            if (!string.IsNullOrEmpty(pm.Name) || !string.IsNullOrEmpty(pm.Id))
                                p.Models.Add(pm);
                        }
                    }

                    providers.Add(p);
                }
            }
        }
        catch { }

        return providers;
    }

    private static List<ChannelConfig> ParseChannels(JsonDocument doc)
    {
        var channels = new List<ChannelConfig>();
        var root = doc.RootElement;
        string[] knownChannels = ["feishu", "dingtalk-connector"];

        try
        {
            if (root.TryGetProperty("channels", out var channelsEl) &&
                channelsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in knownChannels)
                {
                    if (!channelsEl.TryGetProperty(name, out var ch)) continue;
                    var channel = new ChannelConfig { Name = name };

                    if (ch.TryGetProperty("enabled", out var enabled))
                        channel.Enabled = enabled.ValueKind == JsonValueKind.True;

                    foreach (var prop in ch.EnumerateObject())
                    {
                        if (prop.Name == "enabled") continue;
                        channel.Properties[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString() ?? "",
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            JsonValueKind.Number => prop.Value.GetRawText(),
                            _ => prop.Value.GetRawText()
                        };
                    }

                    channels.Add(channel);
                }
            }
        }
        catch { }

        // Ensure all known channels exist (even if not in config)
        foreach (var name in knownChannels)
        {
            if (!channels.Any(c => c.Name == name))
                channels.Add(new ChannelConfig { Name = name });
        }

        return channels;
    }

    /// <summary>Fetch all available model IDs from openclaw models list --all</summary>
    public async Task<List<string>> ListAvailableModelsAsync()
    {
        var models = new List<string>();
        await WslService.RunCommandStreamAsync("wsl",
            $"-d {WslService.DistroName} --user root -- bash -l -c \"openclaw models list --all 2>/dev/null\"",
            line =>
            {
                // Skip header line and parse model ID (first column)
                var clean = System.Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;]*m", "").Trim();
                if (clean.Length == 0 || clean.StartsWith("Model ")) return;
                var parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts[0].Contains('/'))
                    models.Add(parts[0]);
            });
        return models;
    }

    /// <summary>Set a single config key via openclaw config set (base64 pipe to avoid escaping)</summary>
    public async Task<bool> SetConfigAsync(string key, string value)
    {
        var script = $"openclaw config set {key} '{value.Replace("'", "'\"'\"'")}'";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var wrapperCmd = $"echo {b64} | base64 -d | bash -l";

        var exitCode = await WslService.RunCommandStreamAsync("wsl",
            $"-d {WslService.DistroName} --user root -- bash -c \"{wrapperCmd}\"",
            _ => { });
        return exitCode == 0;
    }

    /// <summary>Save model configuration</summary>
    public async Task<List<string>> SaveModelConfigAsync(ModelConfig config)
    {
        var failures = new List<string>();

        if (!string.IsNullOrEmpty(config.PrimaryModel))
        {
            if (!await SetConfigAsync("agents.defaults.model.primary", config.PrimaryModel))
                failures.Add("agents.defaults.model.primary");
        }

        // Save fallbacks as JSON array
        var fallbacksJson = JsonSerializer.Serialize(config.FallbackModels);
        if (!await SetConfigAsync("agents.defaults.model.fallbacks", fallbacksJson))
            failures.Add("agents.defaults.model.fallbacks");

        return failures;
    }

    /// <summary>Save provider as a whole object (openclaw validates all fields together)</summary>
    public async Task<List<string>> SaveProviderAsync(ProviderConfig provider)
    {
        var failures = new List<string>();
        var key = $"models.providers.{provider.Name}";

        // Build the provider object as JSON5 for openclaw config set
        var modelsArray = new StringBuilder("[");
        for (int i = 0; i < provider.Models.Count; i++)
        {
            if (i > 0) modelsArray.Append(", ");
            var m = provider.Models[i];
            var name = m.Name.Replace("\"", "\\\"");
            var id = string.IsNullOrEmpty(m.Id) ? m.Name : m.Id;
            id = id.Replace("\"", "\\\"");
            modelsArray.Append($"{{name: \"{name}\", id: \"{id}\"}}");
        }
        modelsArray.Append(']');

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(provider.BaseUrl))
            parts.Add($"baseUrl: \"{provider.BaseUrl.Replace("\"", "\\\"")}\"");
        if (!string.IsNullOrEmpty(provider.ApiKey))
            parts.Add($"apiKey: \"{provider.ApiKey.Replace("\"", "\\\"")}\"");
        if (!string.IsNullOrEmpty(provider.Api))
            parts.Add($"api: \"{provider.Api}\"");
        parts.Add($"models: {modelsArray}");

        var obj = "{" + string.Join(", ", parts) + "}";

        if (!await SetConfigAsync(key, obj))
            failures.Add(key);

        return failures;
    }

    /// <summary>Save API key for a provider via auth-profiles.json + openclaw.json auth section</summary>
    public async Task<bool> SaveProviderApiKeyAsync(string providerName, string apiKey)
    {
        var profileId = $"{providerName}:manual";

        // 通过 node 脚本写入 auth-profiles.json 和 openclaw.json auth 配置
        // OpenClaw 使用独立的 auth store，而非 config 中的 apiKey 字段
        var script = $$"""
            const fs = require('fs');
            const AGENT_DIR = '/root/.openclaw/agents/main/agent';
            const CONFIG_PATH = '/root/.openclaw/openclaw.json';
            const provider = '{{providerName}}';
            const profileId = '{{profileId}}';
            const token = '{{apiKey}}';

            // 1. Update auth-profiles.json
            fs.mkdirSync(AGENT_DIR, { recursive: true });
            const authPath = AGENT_DIR + '/auth-profiles.json';
            let store = { profiles: {} };
            try { store = JSON.parse(fs.readFileSync(authPath, 'utf8')); if (!store.profiles) store.profiles = {}; } catch {}
            store.profiles[profileId] = { type: 'token', provider, token };
            fs.writeFileSync(authPath, JSON.stringify(store, null, 2));
            fs.chmodSync(authPath, 0o600);

            // 2. Update openclaw.json auth section
            const cfg = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8'));
            if (!cfg.auth) cfg.auth = {};
            if (!cfg.auth.profiles) cfg.auth.profiles = {};
            cfg.auth.profiles[profileId] = { provider, mode: 'token' };
            if (!cfg.auth.order) cfg.auth.order = {};
            if (!cfg.auth.order[provider]) cfg.auth.order[provider] = [];
            if (!cfg.auth.order[provider].includes(profileId))
                cfg.auth.order[provider].unshift(profileId);
            fs.writeFileSync(CONFIG_PATH, JSON.stringify(cfg, null, 2));

            console.log('OK');
            """;

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var wrapperCmd = $"echo {b64} | base64 -d | node -";

        var ok = false;
        var exitCode = await WslService.RunCommandStreamAsync("wsl",
            $"-d {WslService.DistroName} --user root -- bash -l -c \"{wrapperCmd}\"",
            line => { if (line.Trim() == "OK") ok = true; });
        return exitCode == 0 && ok;
    }

    /// <summary>Save channel configuration</summary>
    public async Task<List<string>> SaveChannelAsync(ChannelConfig channel)
    {
        var failures = new List<string>();
        var prefix = $"channels.{channel.Name}";

        if (!await SetConfigAsync($"{prefix}.enabled", channel.Enabled ? "true" : "false"))
            failures.Add($"{prefix}.enabled");

        foreach (var kv in channel.Properties)
        {
            if (!await SetConfigAsync($"{prefix}.{kv.Key}", kv.Value))
                failures.Add($"{prefix}.{kv.Key}");
        }

        return failures;
    }

    /// <summary>Check if the DingTalk plugin is installed with dependencies</summary>
    public async Task<bool> IsDingTalkPluginInstalledAsync()
    {
        var found = false;
        await WslService.RunCommandStreamAsync("wsl",
            $"-d {WslService.DistroName} --user root -- bash -l -c \"test -d /root/.openclaw/extensions/dingtalk-connector/node_modules/dingtalk-stream && echo OK\"",
            line =>
            {
                if (line.Trim() == "OK")
                    found = true;
            });
        return found;
    }

    /// <summary>Install the official DingTalk plugin and configure plugins.allow</summary>
    public async Task<bool> InstallDingTalkPluginAsync(Action<string>? onLog = null)
    {
        onLog ??= _ => { };

        // 1. Remove existing plugin directory if present (avoids "plugin already exists" error)
        onLog("正在安装钉钉插件...");
        await WslService.RunCommandStreamAsync("wsl",
            $"-d {WslService.DistroName} --user root -- bash -l -c \"rm -rf /root/.openclaw/extensions/dingtalk-connector\"",
            _ => { });

        // 2. Install the official plugin package
        var exitCode = await WslService.RunCommandStreamAsync("wsl",
            $"-d {WslService.DistroName} --user root -- bash -l -c \"openclaw plugins install @dingtalk-real-ai/dingtalk-connector\"",
            line => onLog(line));

        if (exitCode != 0)
        {
            onLog("钉钉插件安装失败");
            return false;
        }

        // 3. Enable plugins and add to allow list via node script
        var script = """
            const fs = require('fs');
            const CONFIG_PATH = '/root/.openclaw/openclaw.json';
            const cfg = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8'));
            if (!cfg.plugins) cfg.plugins = {};
            cfg.plugins.enabled = true;
            if (!cfg.plugins.allow) cfg.plugins.allow = [];
            if (!cfg.plugins.allow.includes('dingtalk-connector'))
                cfg.plugins.allow.push('dingtalk-connector');
            if (!cfg.gateway) cfg.gateway = {};
            if (!cfg.gateway.http) cfg.gateway.http = {};
            if (!cfg.gateway.http.endpoints) cfg.gateway.http.endpoints = {};
            if (!cfg.gateway.http.endpoints.chatCompletions) cfg.gateway.http.endpoints.chatCompletions = {};
            cfg.gateway.http.endpoints.chatCompletions.enabled = true;
            fs.writeFileSync(CONFIG_PATH, JSON.stringify(cfg, null, 2));
            console.log('OK');
            """;

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var wrapperCmd = $"echo {b64} | base64 -d | node -";

        var ok = false;
        await WslService.RunCommandStreamAsync("wsl",
            $"-d {WslService.DistroName} --user root -- bash -l -c \"{wrapperCmd}\"",
            line => { if (line.Trim() == "OK") ok = true; });

        if (ok)
            onLog("钉钉插件安装完成");
        else
            onLog("钉钉插件白名单配置失败");

        return ok;
    }
}
