#[cfg(target_os = "windows")]
pub mod windows;

#[cfg(any(target_os = "macos", target_os = "linux"))]
pub mod unix;

use crate::services::shell_backend::ShellBackend;

pub fn create_shell_backend() -> Box<dyn ShellBackend> {
    #[cfg(target_os = "windows")]
    {
        Box::new(windows::WslShellBackend::new())
    }
    #[cfg(any(target_os = "macos", target_os = "linux"))]
    {
        Box::new(unix::DirectShellBackend::new())
    }
}
