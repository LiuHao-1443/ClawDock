use std::sync::{Arc, LazyLock};

use serde::{Deserialize, Serialize};

use crate::services::shell_backend::ShellBackend;

static RE_CONFIG_KEY: LazyLock<regex::Regex> =
    LazyLock::new(|| regex::Regex::new(r"^[\w][\w\-]*(?:\.[\w][\w\-]*)*$").unwrap());
static RE_ANSI_CONFIG: LazyLock<regex::Regex> =
    LazyLock::new(|| regex::Regex::new(r"\x1b\[[0-9;]*m").unwrap());
/// A single config key segment: alphanumeric, underscores, hyphens. No dots.
static RE_NAME_SEGMENT: LazyLock<regex::Regex> =
    LazyLock::new(|| regex::Regex::new(r"^[\w][\w\-]*$").unwrap());

// ── Data Models ────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ModelConfig {
    pub primary_model: String,
    pub fallback_models: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ProviderModel {
    pub name: String,
    pub id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub context_window: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_tokens: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reasoning: Option<bool>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ProviderConfig {
    pub name: String,
    pub base_url: String,
    pub api_key: String,
    pub api: String,
    pub models: Vec<ProviderModel>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct ChannelConfig {
    pub name: String,
    pub enabled: bool,
    pub properties: std::collections::HashMap<String, String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct OpenClawFullConfig {
    pub model: ModelConfig,
    pub providers: Vec<ProviderConfig>,
    pub channels: Vec<ChannelConfig>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub parse_error: Option<String>,
}

// ── Service ────────────────────────────────────────────────────────────────

pub struct ConfigService {
    shell: Arc<dyn ShellBackend>,
}

impl ConfigService {
    pub fn new(shell: Arc<dyn ShellBackend>) -> Self {
        Self { shell }
    }

    /// Read and parse the full openclaw.json config
    pub async fn read_full_config(&self) -> OpenClawFullConfig {
        let mut config = OpenClawFullConfig::default();

        let cmd = format!("cat '{}/openclaw.json'", self.shell.config_path());
        let output = match self
            .shell
            .run_command(&cmd)
            .await
        {
            Ok(o) => o.stdout,
            Err(e) => {
                config.parse_error = Some(format!("Failed to read config: {}", e));
                return config;
            }
        };

        // Find JSON start (skip any WSL garbled prefix)
        let json_start = match output.find('{') {
            Some(i) => i,
            None => {
                config.parse_error =
                    Some("Config file not found or empty output".to_string());
                return config;
            }
        };

        let json_slice = &output[json_start..];
        let doc: serde_json::Value = match serde_json::Deserializer::from_str(json_slice)
            .into_iter::<serde_json::Value>()
            .next()
        {
            Some(Ok(v)) => v,
            Some(Err(e)) => {
                config.parse_error = Some(format!("JSON parse error: {}", e));
                return config;
            }
            None => {
                config.parse_error = Some("Config file empty".to_string());
                return config;
            }
        };

        config.model = parse_model_config(&doc);
        config.providers = parse_providers(&doc);
        config.channels = parse_channels(&doc);

        config
    }

    /// Set a single config key via `openclaw config set` (base64 pipe)
    pub async fn set_config(&self, key: &str, value: &str) -> Result<(), String> {
        // Validate key: only dotted identifiers
        if !RE_CONFIG_KEY.is_match(key) {
            return Err(format!("Invalid config key: {}", key));
        }

        // Escape value for POSIX single-quotes
        let escaped_value = value.replace('\'', "'\"'\"'");
        let script = format!("openclaw config set {} '{}'", key, escaped_value);
        let b64 = base64_encode(&script);
        let wrapper = format!("node -e \"process.stdout.write(Buffer.from('{}','base64').toString())\" | bash -l", b64);

        let result = self.shell.run_command(&wrapper).await;
        match result {
            Ok(output) if output.status.success() => Ok(()),
            Ok(output) => Err(format!(
                "openclaw config set failed: {}",
                output.stderr.trim()
            )),
            Err(e) => Err(format!("Command failed: {}", e)),
        }
    }

    /// Save model configuration (primary + fallbacks)
    pub async fn save_model_config(&self, config: &ModelConfig) -> Vec<String> {
        let mut failures = Vec::new();

        if !config.primary_model.is_empty() {
            if let Err(_) = self
                .set_config("agents.defaults.model.primary", &config.primary_model)
                .await
            {
                failures.push("agents.defaults.model.primary".to_string());
            }
        }

        let fallbacks_json = serde_json::to_string(&config.fallback_models).unwrap_or_default();
        if let Err(_) = self
            .set_config("agents.defaults.model.fallbacks", &fallbacks_json)
            .await
        {
            failures.push("agents.defaults.model.fallbacks".to_string());
        }

        failures
    }

    /// Save a provider configuration (as JSON5 object)
    pub async fn save_provider(&self, provider: &ProviderConfig) -> Vec<String> {
        let mut failures = Vec::new();
        if !RE_NAME_SEGMENT.is_match(&provider.name) {
            failures.push(format!("Invalid provider name: {}", provider.name));
            return failures;
        }
        let key = format!("models.providers.{}", provider.name);

        // Build JSON5 object
        let mut parts = Vec::new();
        if !provider.base_url.is_empty() {
            parts.push(format!(
                "baseUrl: \"{}\"",
                escape_json5(&provider.base_url)
            ));
        }
        if !provider.api_key.is_empty() {
            parts.push(format!(
                "apiKey: \"{}\"",
                escape_json5(&provider.api_key)
            ));
        }
        if !provider.api.is_empty() {
            parts.push(format!("api: \"{}\"", escape_json5(&provider.api)));
        }

        let models_array: Vec<String> = provider
            .models
            .iter()
            .map(|m| {
                let name = escape_json5(&m.name);
                let id = escape_json5(if m.id.is_empty() { &m.name } else { &m.id });
                let mut extras = String::new();
                if let Some(cw) = m.context_window {
                    extras.push_str(&format!(", contextWindow: {}", cw));
                }
                if let Some(mt) = m.max_tokens {
                    extras.push_str(&format!(", maxTokens: {}", mt));
                }
                if let Some(r) = m.reasoning {
                    extras.push_str(&format!(", reasoning: {}", r));
                }
                format!("{{name: \"{}\", id: \"{}\"{}}}", name, id, extras)
            })
            .collect();
        parts.push(format!("models: [{}]", models_array.join(", ")));

        let obj = format!("{{{}}}", parts.join(", "));

        if let Err(_) = self.set_config(&key, &obj).await {
            failures.push(key);
        }

        failures
    }

    /// Delete a custom provider via node script
    pub async fn delete_provider(&self, provider_name: &str) -> Result<bool, String> {
        if !RE_NAME_SEGMENT.is_match(provider_name) {
            return Err(format!("Invalid provider name: {}", provider_name));
        }
        let safe_name = escape_js_string(provider_name);
        let config_path = escape_js_path(&format!("{}/openclaw.json", self.shell.config_path()));

        let script = format!(
            r#"const fs = require('fs');
const CONFIG_PATH = '{}';
const cfg = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8'));
if (cfg.models?.providers?.['{}']) {{
    delete cfg.models.providers['{}'];
    const tmp = CONFIG_PATH + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(cfg, null, 2));
    fs.renameSync(tmp, CONFIG_PATH);
    console.log('OK');
}} else {{
    console.log('NOT_FOUND');
}}"#,
            config_path, safe_name, safe_name
        );

        let b64 = base64_encode(&script);
        let wrapper = format!("node -e \"eval(Buffer.from('{}','base64').toString())\"", b64);

        let output = self
            .shell
            .run_command(&wrapper)
            .await
            .map_err(|e| e.to_string())?;
        Ok(output.stdout.contains("OK"))
    }

    /// Save API key for a provider via auth-profiles.json
    pub async fn save_provider_api_key(
        &self,
        provider_name: &str,
        api_key: &str,
    ) -> Result<bool, String> {
        if !RE_NAME_SEGMENT.is_match(provider_name) {
            return Err(format!("Invalid provider name: {}", provider_name));
        }
        let safe_provider = escape_js_string(provider_name);
        let safe_profile = escape_js_string(&format!("{}:manual", provider_name));
        let safe_token = escape_js_string(api_key);
        let config_path = escape_js_path(&format!("{}/openclaw.json", self.shell.config_path()));

        let script = format!(
            r#"const fs = require('fs');
const AGENT_DIR = '{}/agents/main/agent';
const CONFIG_PATH = '{}';
const provider = '{}';
const profileId = '{}';
const token = '{}';

fs.mkdirSync(AGENT_DIR, {{ recursive: true }});
const authPath = AGENT_DIR + '/auth-profiles.json';
let store = {{ profiles: {{}} }};
try {{ store = JSON.parse(fs.readFileSync(authPath, 'utf8')); if (!store.profiles) store.profiles = {{}}; }} catch {{}}
store.profiles[profileId] = {{ type: 'token', provider, token }};
const authTmp = authPath + '.tmp';
fs.writeFileSync(authTmp, JSON.stringify(store, null, 2));
fs.renameSync(authTmp, authPath);
fs.chmodSync(authPath, 0o600);

const cfg = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8'));
if (!cfg.auth) cfg.auth = {{}};
if (!cfg.auth.profiles) cfg.auth.profiles = {{}};
cfg.auth.profiles[profileId] = {{ provider, mode: 'token' }};
if (!cfg.auth.order) cfg.auth.order = {{}};
if (!cfg.auth.order[provider]) cfg.auth.order[provider] = [];
if (!cfg.auth.order[provider].includes(profileId))
    cfg.auth.order[provider].unshift(profileId);
const cfgTmp = CONFIG_PATH + '.tmp';
fs.writeFileSync(cfgTmp, JSON.stringify(cfg, null, 2));
fs.renameSync(cfgTmp, CONFIG_PATH);
console.log('OK');"#,
            escape_js_path(&self.shell.config_path()),
            config_path,
            safe_provider,
            safe_profile,
            safe_token
        );

        let b64 = base64_encode(&script);
        let wrapper = format!("node -e \"eval(Buffer.from('{}','base64').toString())\"", b64);

        let output = self
            .shell
            .run_command(&wrapper)
            .await
            .map_err(|e| e.to_string())?;
        Ok(output.stdout.contains("OK"))
    }

    /// Save channel configuration
    pub async fn save_channel(&self, channel: &ChannelConfig) -> Vec<String> {
        let mut failures = Vec::new();
        if !RE_NAME_SEGMENT.is_match(&channel.name) {
            failures.push(format!("Invalid channel name: {}", channel.name));
            return failures;
        }
        let prefix = format!("channels.{}", channel.name);

        let enabled_str = if channel.enabled { "true" } else { "false" };
        if let Err(_) = self
            .set_config(&format!("{}.enabled", prefix), enabled_str)
            .await
        {
            failures.push(format!("{}.enabled", prefix));
        }

        for (k, v) in &channel.properties {
            if !RE_NAME_SEGMENT.is_match(k) {
                failures.push(format!("Invalid property key: {}", k));
                continue;
            }
            if let Err(_) = self.set_config(&format!("{}.{}", prefix, k), v).await {
                failures.push(format!("{}.{}", prefix, k));
            }
        }

        failures
    }

    /// List available models via `openclaw models list --all`
    pub async fn list_available_models(&self) -> Vec<String> {
        let mut models = Vec::new();
        let output = self
            .shell
            .run_command("openclaw models list --all 2>/dev/null")
            .await;

        if let Ok(output) = output {
            for line in output.stdout.lines() {
                let clean = RE_ANSI_CONFIG.replace_all(line, "").trim().to_string();
                if clean.is_empty() || clean.starts_with("Model ") {
                    continue;
                }
                let parts: Vec<&str> = clean.split_whitespace().collect();
                if !parts.is_empty() && parts[0].contains('/') {
                    models.push(parts[0].to_string());
                }
            }
        }

        models
    }

    /// Install the DingTalk plugin
    pub async fn install_dingtalk_plugin(
        &self,
        on_log: &(dyn Fn(String) + Send + Sync),
    ) -> Result<bool, String> {
        on_log("Installing DingTalk plugin...".to_string());

        // Remove existing
        let rm_cmd = format!("rm -rf '{}/extensions/dingtalk-connector'", self.shell.config_path());
        let _ = self
            .shell
            .run_command(&rm_cmd)
            .await;

        // Install
        let exit_code = self
            .shell
            .run_command_stream(
                "openclaw plugins install @dingtalk-real-ai/dingtalk-connector",
                on_log,
            )
            .await
            .map_err(|e| e.to_string())?;

        if exit_code != 0 {
            return Ok(false);
        }

        // Enable in config via node script
        let config_path = escape_js_path(&format!("{}/openclaw.json", self.shell.config_path()));
        let script = format!(
            r#"const fs = require('fs');
const CONFIG_PATH = '{}';
const cfg = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8'));
if (!cfg.plugins) cfg.plugins = {{}};
cfg.plugins.enabled = true;
if (!cfg.plugins.allow) cfg.plugins.allow = [];
if (!cfg.plugins.allow.includes('dingtalk-connector'))
    cfg.plugins.allow.push('dingtalk-connector');
if (!cfg.gateway) cfg.gateway = {{}};
if (!cfg.gateway.http) cfg.gateway.http = {{}};
if (!cfg.gateway.http.endpoints) cfg.gateway.http.endpoints = {{}};
if (!cfg.gateway.http.endpoints.chatCompletions) cfg.gateway.http.endpoints.chatCompletions = {{}};
cfg.gateway.http.endpoints.chatCompletions.enabled = true;
const tmp = CONFIG_PATH + '.tmp';
fs.writeFileSync(tmp, JSON.stringify(cfg, null, 2));
fs.renameSync(tmp, CONFIG_PATH);
console.log('OK');"#,
            config_path
        );

        let b64 = base64_encode(&script);
        let wrapper = format!("node -e \"eval(Buffer.from('{}','base64').toString())\"", b64);
        let output = self
            .shell
            .run_command(&wrapper)
            .await
            .map_err(|e| e.to_string())?;

        Ok(output.stdout.contains("OK"))
    }
}

// ── Parsing helpers ────────────────────────────────────────────────────────

fn parse_model_config(doc: &serde_json::Value) -> ModelConfig {
    let mut model = ModelConfig::default();

    if let Some(primary) = doc
        .get("agents")
        .and_then(|a| a.get("defaults"))
        .and_then(|d| d.get("model"))
        .and_then(|m| m.get("primary"))
        .and_then(|p| p.as_str())
    {
        model.primary_model = primary.to_string();
    }

    if let Some(fallbacks) = doc
        .get("agents")
        .and_then(|a| a.get("defaults"))
        .and_then(|d| d.get("model"))
        .and_then(|m| m.get("fallbacks"))
        .and_then(|f| f.as_array())
    {
        for f in fallbacks {
            if let Some(s) = f.as_str() {
                if !s.is_empty() {
                    model.fallback_models.push(s.to_string());
                }
            }
        }
    }

    model
}

fn parse_providers(doc: &serde_json::Value) -> Vec<ProviderConfig> {
    let mut providers = Vec::new();

    if let Some(providers_obj) = doc
        .get("models")
        .and_then(|m| m.get("providers"))
        .and_then(|p| p.as_object())
    {
        for (name, val) in providers_obj {
            let mut p = ProviderConfig {
                name: name.clone(),
                ..Default::default()
            };

            if let Some(s) = val.get("baseUrl").and_then(|v| v.as_str()) {
                p.base_url = s.to_string();
            }
            if let Some(s) = val.get("apiKey").and_then(|v| v.as_str()) {
                p.api_key = s.to_string();
            }
            if let Some(s) = val.get("api").and_then(|v| v.as_str()) {
                p.api = s.to_string();
            } else {
                p.api = "openai".to_string();
            }

            if let Some(models) = val.get("models").and_then(|m| m.as_array()) {
                for m in models {
                    let mut pm = ProviderModel::default();
                    if let Some(s) = m.get("name").and_then(|v| v.as_str()) {
                        pm.name = s.to_string();
                    }
                    if let Some(s) = m.get("id").and_then(|v| v.as_str()) {
                        pm.id = s.to_string();
                    }
                    if let Some(n) = m.get("contextWindow").and_then(|v| v.as_i64()) {
                        pm.context_window = Some(n);
                    }
                    if let Some(n) = m.get("maxTokens").and_then(|v| v.as_i64()) {
                        pm.max_tokens = Some(n);
                    }
                    if let Some(b) = m.get("reasoning").and_then(|v| v.as_bool()) {
                        pm.reasoning = Some(b);
                    }
                    if !pm.name.is_empty() || !pm.id.is_empty() {
                        p.models.push(pm);
                    }
                }
            }

            providers.push(p);
        }
    }

    providers
}

fn parse_channels(doc: &serde_json::Value) -> Vec<ChannelConfig> {
    let mut channels = Vec::new();
    let known_channels = ["feishu", "dingtalk-connector"];

    if let Some(channels_obj) = doc.get("channels").and_then(|c| c.as_object()) {
        for name in &known_channels {
            if let Some(ch) = channels_obj.get(*name) {
                let mut channel = ChannelConfig {
                    name: name.to_string(),
                    ..Default::default()
                };

                if let Some(enabled) = ch.get("enabled").and_then(|v| v.as_bool()) {
                    channel.enabled = enabled;
                }

                if let Some(obj) = ch.as_object() {
                    for (k, v) in obj {
                        if k == "enabled" {
                            continue;
                        }
                        let val_str = match v {
                            serde_json::Value::String(s) => s.clone(),
                            serde_json::Value::Bool(b) => b.to_string(),
                            serde_json::Value::Number(n) => n.to_string(),
                            other => other.to_string(),
                        };
                        channel.properties.insert(k.clone(), val_str);
                    }
                }

                channels.push(channel);
            }
        }
    }

    // Ensure all known channels exist
    for name in &known_channels {
        if !channels.iter().any(|c| c.name == *name) {
            channels.push(ChannelConfig {
                name: name.to_string(),
                ..Default::default()
            });
        }
    }

    channels
}

// ── String helpers ─────────────────────────────────────────────────────────

fn escape_json5(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('"', "\\\"")
        .replace('\n', "\\n")
        .replace('\r', "\\r")
        .replace('\t', "\\t")
        .replace('\0', "\\0")
        .replace('\u{2028}', "\\u2028")
        .replace('\u{2029}', "\\u2029")
}

/// Escape a path for embedding in a JavaScript single-quoted string literal.
fn escape_js_path(path: &str) -> String {
    path.replace('\\', "\\\\").replace('\'', "\\'")
}

fn escape_js_string(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('\'', "\\'")
        .replace('"', "\\\"")
        .replace('\n', "\\n")
        .replace('\r', "\\r")
        .replace('\0', "\\0")
        .replace('\u{2028}', "\\u2028")
        .replace('\u{2029}', "\\u2029")
}

fn base64_encode(input: &str) -> String {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.encode(input.as_bytes())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_model_config_full() {
        let doc: serde_json::Value = serde_json::from_str(r#"{
            "agents": {
                "defaults": {
                    "model": {
                        "primary": "gpt-4o",
                        "fallbacks": ["gpt-4o-mini", "claude-3-haiku"]
                    }
                }
            }
        }"#).unwrap();

        let model = parse_model_config(&doc);
        assert_eq!(model.primary_model, "gpt-4o");
        assert_eq!(model.fallback_models, vec!["gpt-4o-mini", "claude-3-haiku"]);
    }

    #[test]
    fn test_parse_model_config_empty() {
        let doc: serde_json::Value = serde_json::from_str("{}").unwrap();
        let model = parse_model_config(&doc);
        assert_eq!(model.primary_model, "");
        assert!(model.fallback_models.is_empty());
    }

    #[test]
    fn test_parse_model_config_skips_empty_fallbacks() {
        let doc: serde_json::Value = serde_json::from_str(r#"{
            "agents": {
                "defaults": {
                    "model": {
                        "primary": "gpt-4o",
                        "fallbacks": ["gpt-4o-mini", "", "claude-3-haiku"]
                    }
                }
            }
        }"#).unwrap();

        let model = parse_model_config(&doc);
        assert_eq!(model.fallback_models.len(), 2);
        assert!(!model.fallback_models.contains(&String::new()));
    }

    #[test]
    fn test_parse_providers_full() {
        let doc: serde_json::Value = serde_json::from_str(r#"{
            "models": {
                "providers": {
                    "my-openai": {
                        "baseUrl": "https://api.example.com/v1",
                        "apiKey": "sk-xxx",
                        "api": "openai",
                        "models": [
                            {
                                "name": "gpt-4o",
                                "id": "gpt-4o-2024",
                                "contextWindow": 128000,
                                "maxTokens": 4096,
                                "reasoning": false
                            }
                        ]
                    }
                }
            }
        }"#).unwrap();

        let providers = parse_providers(&doc);
        assert_eq!(providers.len(), 1);
        assert_eq!(providers[0].name, "my-openai");
        assert_eq!(providers[0].base_url, "https://api.example.com/v1");
        assert_eq!(providers[0].api_key, "sk-xxx");
        assert_eq!(providers[0].api, "openai");
        assert_eq!(providers[0].models.len(), 1);
        assert_eq!(providers[0].models[0].name, "gpt-4o");
        assert_eq!(providers[0].models[0].id, "gpt-4o-2024");
        assert_eq!(providers[0].models[0].context_window, Some(128000));
        assert_eq!(providers[0].models[0].max_tokens, Some(4096));
        assert_eq!(providers[0].models[0].reasoning, Some(false));
    }

    #[test]
    fn test_parse_providers_defaults_api_to_openai() {
        let doc: serde_json::Value = serde_json::from_str(r#"{
            "models": {
                "providers": {
                    "custom": {
                        "baseUrl": "https://example.com"
                    }
                }
            }
        }"#).unwrap();

        let providers = parse_providers(&doc);
        assert_eq!(providers[0].api, "openai");
    }

    #[test]
    fn test_parse_providers_empty() {
        let doc: serde_json::Value = serde_json::from_str("{}").unwrap();
        let providers = parse_providers(&doc);
        assert!(providers.is_empty());
    }

    #[test]
    fn test_parse_providers_skips_empty_models() {
        let doc: serde_json::Value = serde_json::from_str(r#"{
            "models": {
                "providers": {
                    "p1": {
                        "models": [
                            {"name": "good", "id": "good-id"},
                            {"name": "", "id": ""}
                        ]
                    }
                }
            }
        }"#).unwrap();

        let providers = parse_providers(&doc);
        assert_eq!(providers[0].models.len(), 1);
        assert_eq!(providers[0].models[0].name, "good");
    }

    #[test]
    fn test_parse_channels_known() {
        let doc: serde_json::Value = serde_json::from_str(r#"{
            "channels": {
                "feishu": {
                    "enabled": true,
                    "appId": "cli_xxx",
                    "appSecret": "secret"
                },
                "dingtalk-connector": {
                    "enabled": false,
                    "clientId": "did_xxx"
                }
            }
        }"#).unwrap();

        let channels = parse_channels(&doc);
        assert_eq!(channels.len(), 2);

        let feishu = channels.iter().find(|c| c.name == "feishu").unwrap();
        assert!(feishu.enabled);
        assert_eq!(feishu.properties.get("appId").unwrap(), "cli_xxx");
        assert_eq!(feishu.properties.get("appSecret").unwrap(), "secret");

        let dt = channels.iter().find(|c| c.name == "dingtalk-connector").unwrap();
        assert!(!dt.enabled);
        assert_eq!(dt.properties.get("clientId").unwrap(), "did_xxx");
    }

    #[test]
    fn test_parse_channels_fills_missing() {
        let doc: serde_json::Value = serde_json::from_str("{}").unwrap();
        let channels = parse_channels(&doc);
        assert_eq!(channels.len(), 2);
        assert!(channels.iter().any(|c| c.name == "feishu"));
        assert!(channels.iter().any(|c| c.name == "dingtalk-connector"));
        assert!(channels.iter().all(|c| !c.enabled));
    }

    #[test]
    fn test_parse_channels_bool_number_values() {
        let doc: serde_json::Value = serde_json::from_str(r#"{
            "channels": {
                "feishu": {
                    "enabled": true,
                    "mode": "websocket",
                    "retries": 3,
                    "debug": true
                }
            }
        }"#).unwrap();

        let channels = parse_channels(&doc);
        let feishu = channels.iter().find(|c| c.name == "feishu").unwrap();
        assert_eq!(feishu.properties.get("mode").unwrap(), "websocket");
        assert_eq!(feishu.properties.get("retries").unwrap(), "3");
        assert_eq!(feishu.properties.get("debug").unwrap(), "true");
    }

    #[test]
    fn test_escape_json5_basic() {
        assert_eq!(escape_json5("hello"), "hello");
        assert_eq!(escape_json5(r#"say "hi""#), r#"say \"hi\""#);
        assert_eq!(escape_json5("line1\nline2"), r#"line1\nline2"#);
        assert_eq!(escape_json5("path\\to\\file"), r#"path\\to\\file"#);
    }

    #[test]
    fn test_escape_json5_unicode_separators() {
        assert_eq!(escape_json5("a\u{2028}b"), r#"a\u2028b"#);
        assert_eq!(escape_json5("a\u{2029}b"), r#"a\u2029b"#);
    }

    #[test]
    fn test_escape_js_string_basic() {
        assert_eq!(escape_js_string("hello"), "hello");
        assert_eq!(escape_js_string("it's"), r#"it\'s"#);
        assert_eq!(escape_js_string("path\\to"), r#"path\\to"#);
    }

    #[test]
    fn test_base64_encode() {
        assert_eq!(base64_encode("hello"), "aGVsbG8=");
        assert_eq!(base64_encode("openclaw config set key 'val'"), "b3BlbmNsYXcgY29uZmlnIHNldCBrZXkgJ3ZhbCc=");
    }
}
