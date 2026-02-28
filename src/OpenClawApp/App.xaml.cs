using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using OpenClawApp.Services;
using OpenClawApp.Views;

namespace OpenClawApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var stateService = new InstallStateService();
        var state = stateService.Load();

        if (state.IsInstallComplete)
        {
            // 已安装，直接打开主窗口
            new MainWindow().Show();
        }
        else
        {
            // 未安装，需要管理员权限
            if (!IsRunningAsAdmin())
            {
                RelaunchAsAdmin();
                return;
            }

            new InstallWindow(stateService).Show();
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
                "OpenClaw 安装需要管理员权限，请右键点击程序并选择「以管理员身份运行」。",
                "需要管理员权限",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Application.Current.Shutdown();
    }
}
