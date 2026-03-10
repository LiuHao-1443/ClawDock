use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::sync::Mutex;

/// Serializes all read-modify-write operations on the install state file.
static STATE_LOCK: Mutex<()> = Mutex::new(());

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum InstallPhase {
    NotStarted,
    Wsl2Enabled,
    Wsl2Reboot,
    DistroImported,
    DistroConfigured,
    OpenClawInstalling,
    Complete,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallState {
    pub installed: bool,
    pub phase: InstallPhase,
    pub wsl2_installed: bool,
    pub distro_installed: bool,
    pub openclaw_installed: bool,
    pub install_date: Option<String>,
    pub version: String,
}

impl Default for InstallState {
    fn default() -> Self {
        Self {
            installed: false,
            phase: InstallPhase::NotStarted,
            wsl2_installed: false,
            distro_installed: false,
            openclaw_installed: false,
            install_date: None,
            version: "1.0.0".to_string(),
        }
    }
}

pub struct InstallStateService;

impl InstallStateService {
    fn state_path() -> PathBuf {
        let base = dirs::config_dir().unwrap_or_else(|| PathBuf::from("."));
        base.join("ClawDock").join("state.json")
    }

    pub fn load() -> InstallState {
        let _lock = STATE_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        Self::load_inner()
    }

    /// Read state from disk without acquiring STATE_LOCK.
    /// Callers must already hold the lock.
    fn load_inner() -> InstallState {
        let path = Self::state_path();
        if !path.exists() {
            return InstallState::default();
        }

        match std::fs::read_to_string(&path) {
            Ok(json) => serde_json::from_str(&json).unwrap_or_default(),
            Err(_) => InstallState::default(),
        }
    }

    pub fn save(state: &InstallState) -> Result<(), String> {
        let _lock = STATE_LOCK.lock().map_err(|e| e.to_string())?;
        Self::save_inner(state)
    }

    /// Write state to disk without acquiring STATE_LOCK.
    /// Callers must already hold the lock.
    fn save_inner(state: &InstallState) -> Result<(), String> {
        let path = Self::state_path();
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
        }
        let json = serde_json::to_string_pretty(state).map_err(|e| e.to_string())?;
        let tmp_path = path.with_extension("json.tmp");
        std::fs::write(&tmp_path, &json).map_err(|e| e.to_string())?;
        std::fs::rename(&tmp_path, &path).map_err(|e| e.to_string())
    }

    pub fn save_phase(phase: InstallPhase) -> Result<(), String> {
        let _lock = STATE_LOCK.lock().map_err(|e| e.to_string())?;
        let mut state = Self::load_inner();
        state.phase = phase;
        Self::save_inner(&state)
    }

    pub fn mark_wsl2_done() -> Result<(), String> {
        let _lock = STATE_LOCK.lock().map_err(|e| e.to_string())?;
        let mut state = Self::load_inner();
        state.wsl2_installed = true;
        state.distro_installed = true;
        state.phase = InstallPhase::DistroConfigured;
        Self::save_inner(&state)
    }

    pub fn mark_openclaw_done() -> Result<(), String> {
        let _lock = STATE_LOCK.lock().map_err(|e| e.to_string())?;
        let mut state = Self::load_inner();
        state.openclaw_installed = true;
        state.phase = InstallPhase::Complete;
        state.installed = true;
        state.install_date = Some(chrono::Utc::now().to_rfc3339());
        Self::save_inner(&state)
    }

    pub fn reset() -> Result<(), String> {
        let _lock = STATE_LOCK.lock().map_err(|e| e.to_string())?;
        let path = Self::state_path();
        if path.exists() {
            std::fs::remove_file(&path).map_err(|e| e.to_string())?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_install_state_default() {
        let state = InstallState::default();
        assert!(!state.installed);
        assert_eq!(state.phase, InstallPhase::NotStarted);
        assert!(!state.wsl2_installed);
        assert!(!state.distro_installed);
        assert!(!state.openclaw_installed);
        assert!(state.install_date.is_none());
        assert_eq!(state.version, "1.0.0");
    }

    #[test]
    fn test_install_phase_serde_roundtrip() {
        let phases = vec![
            InstallPhase::NotStarted,
            InstallPhase::Wsl2Enabled,
            InstallPhase::Wsl2Reboot,
            InstallPhase::DistroImported,
            InstallPhase::DistroConfigured,
            InstallPhase::OpenClawInstalling,
            InstallPhase::Complete,
        ];
        for phase in phases {
            let json = serde_json::to_string(&phase).unwrap();
            let decoded: InstallPhase = serde_json::from_str(&json).unwrap();
            assert_eq!(decoded, phase);
        }
    }

    #[test]
    fn test_install_state_serde_roundtrip() {
        let state = InstallState {
            installed: true,
            phase: InstallPhase::Complete,
            wsl2_installed: true,
            distro_installed: true,
            openclaw_installed: true,
            install_date: Some("2025-01-01T00:00:00Z".to_string()),
            version: "1.0.0".to_string(),
        };

        let json = serde_json::to_string_pretty(&state).unwrap();
        let decoded: InstallState = serde_json::from_str(&json).unwrap();

        assert!(decoded.installed);
        assert_eq!(decoded.phase, InstallPhase::Complete);
        assert!(decoded.wsl2_installed);
        assert!(decoded.openclaw_installed);
        assert_eq!(decoded.install_date, Some("2025-01-01T00:00:00Z".to_string()));
    }

    #[test]
    fn test_install_state_deserialize_camel_case() {
        let json = r#"{
            "installed": false,
            "phase": "notStarted",
            "wsl2Installed": false,
            "distroInstalled": false,
            "openclawInstalled": false,
            "installDate": null,
            "version": "1.0.0"
        }"#;
        let state: InstallState = serde_json::from_str(json).unwrap();
        assert!(!state.installed);
        assert_eq!(state.phase, InstallPhase::NotStarted);
    }

    #[test]
    fn test_install_state_save_and_load() {
        // Use a temp dir to avoid polluting the real config
        let tmp = std::env::temp_dir().join("clawdock-test-state");
        let _ = std::fs::create_dir_all(&tmp);
        let path = tmp.join("state.json");

        let state = InstallState {
            installed: true,
            phase: InstallPhase::Complete,
            wsl2_installed: true,
            distro_installed: true,
            openclaw_installed: true,
            install_date: Some("2025-06-01T12:00:00Z".to_string()),
            version: "1.0.0".to_string(),
        };

        let json = serde_json::to_string_pretty(&state).unwrap();
        std::fs::write(&path, &json).unwrap();

        let loaded: InstallState = serde_json::from_str(
            &std::fs::read_to_string(&path).unwrap()
        ).unwrap();

        assert!(loaded.installed);
        assert_eq!(loaded.phase, InstallPhase::Complete);

        // Cleanup
        let _ = std::fs::remove_dir_all(&tmp);
    }
}
