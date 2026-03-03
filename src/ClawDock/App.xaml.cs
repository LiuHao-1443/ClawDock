using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using ClawDock.Services;
using ClawDock.Views;

namespace ClawDock;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var stateService = new InstallStateService();
        var state = stateService.Load();

        if (state.IsInstallComplete)
        {
            // 校验 WSL 发行版是否真的存在
            var wslService = new WslService();
            var check = wslService.Check();

            if (check.DistroInstalled)
            {
                // 已安装，直接打开主窗口
                new MainWindow().Show();
                return;
            }

            // 发行版丢失，需要管理员权限来修复 — 先提权再弹窗，避免双重提示
            if (!IsRunningAsAdmin())
            {
                RelaunchAsAdmin();
                return;
            }

            var result = MessageBox.Show(
                "检测到 ClawDock 的 WSL 发行版已被卸载或损坏，需要重新安装。\n\n" +
                "• 选择「是」重新安装\n" +
                "• 选择「否」退出应用",
                "环境异常",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
            {
                Shutdown();
                return;
            }

            UnregisterDistro();
            stateService.Reset();

            new InstallWindow(stateService).Show();
        }
        else
        {
            // 未安装，需要管理员权限 — 先提权再弹窗，避免双重提示
            if (!IsRunningAsAdmin())
            {
                RelaunchAsAdmin();
                return;
            }

            // WSL2 重启后自动续装，不弹窗
            if (state.Phase == InstallPhase.Wsl2Reboot)
            {
                // 直接继续安装
            }
            else if (state.Phase != InstallPhase.NotStarted)
            {
                // 其他中间状态：中断的安装无法续装，只能重新安装
                var phaseDesc = state.Phase switch
                {
                    InstallPhase.Wsl2Enabled => "WSL2 功能已启用",
                    InstallPhase.DistroImported => "发行版已导入",
                    InstallPhase.DistroConfigured => "发行版环境已配置",
                    InstallPhase.OpenClawInstalling => "OpenClaw 安装中",
                    _ => state.Phase.ToString()
                };

                var result = MessageBox.Show(
                    $"上次安装在「{phaseDesc}」阶段后中断。\n" +
                    "中断的安装无法继续，需要重新安装。\n\n" +
                    "• 选择「是」重新安装\n" +
                    "• 选择「否」退出应用",
                    "安装未完成",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    Shutdown();
                    return;
                }

                UnregisterDistro();
                stateService.Reset();
            }

            new InstallWindow(stateService).Show();
        }
    }

    private static void UnregisterDistro()
    {
        try
        {
            WslService.RunCommand("wsl", $"--unregister {WslService.DistroName}");
        }
        catch
        {
            // 卸载失败不阻塞重装流程
        }
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchAsAdmin()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Process.GetCurrentProcess().MainModule?.FileName ?? "OpenClaw.exe",
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            Process.Start(psi);
        }
        catch (Exception)
        {
            MessageBox.Show(
                "ClawDock 安装需要管理员权限，请右键点击程序并选择「以管理员身份运行」。",
                "需要管理员权限",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Application.Current.Shutdown();
    }
}
