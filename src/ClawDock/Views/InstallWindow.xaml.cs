using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ClawDock.Services;

namespace ClawDock.Views;

public partial class InstallWindow : Window
{
    private readonly InstallStateService _stateService;
    private readonly WslService _wslService = new();
    private readonly OpenClawService _openClawService = new();
    private readonly CancellationTokenSource _cts = new();

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClawDock", "logs");

    private StreamWriter? _installLog;
    private string? _logFilePath;

    private int _currentStep = 1;
    private bool _wslAlreadyInstalled;
    private bool _needsReboot;
    private bool _openingMainWindow;

    public InstallWindow(InstallStateService stateService)
    {
        InitializeComponent();
        _stateService = stateService;

        // WSL2 重启后自动续装（唯一允许续装的场景）
        var state = stateService.Load();
        if (state.Phase == InstallPhase.Wsl2Reboot)
        {
            NavigateTo(3);
            _ = RunWsl2InstallAsync();
        }
    }

    // ── 导航 ─────────────────────────────────────────────────────────────

    private void NavigateTo(int step)
    {
        _currentStep = step;

        PageWelcome.Visibility  = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageCheck.Visibility    = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageWsl.Visibility      = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageInstall.Visibility  = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        PageDone.Visibility     = step == 5 ? Visibility.Visible : Visibility.Collapsed;

        UpdateStepIndicators(step);
        UpdateButtons(step);
    }

    private void UpdateStepIndicators(int activeStep)
    {
        var indicators = new[]
        {
            (Step1Circle, Step1Num, Step1Label),
            (Step2Circle, Step2Num, Step2Label),
            (Step3Circle, Step3Num, Step3Label),
            (Step4Circle, Step4Num, Step4Label),
            (Step5Circle, Step5Num, Step5Label),
        };

        for (int i = 0; i < indicators.Length; i++)
        {
            var (circle, num, label) = indicators[i];
            bool isActive   = i + 1 == activeStep;
            bool isDone     = i + 1 < activeStep;

            circle.Fill = isActive
                ? (SolidColorBrush)FindResource("AccentBrush")
                : isDone
                    ? (SolidColorBrush)FindResource("SuccessBrush")
                    : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x45));

            num.Text = isDone ? "✓" : (i + 1).ToString();
            num.Foreground = isActive || isDone
                ? Brushes.White
                : (SolidColorBrush)FindResource("TextSecondaryBrush");

            label.Foreground = isActive
                ? (SolidColorBrush)FindResource("TextPrimaryBrush")
                : (SolidColorBrush)FindResource("TextSecondaryBrush");
            label.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void UpdateButtons(int step)
    {
        BtnBack.Visibility = step > 1 && step < 5 ? Visibility.Visible : Visibility.Hidden;

        BtnNext.Content = step switch
        {
            1 => "开始安装 →",
            2 => "继续 →",
            5 => "立即打开 OpenClaw",
            _ => "继续 →"
        };

        BtnNext.IsEnabled = step != 3 && step != 4;
    }

    // ── 按钮事件 ─────────────────────────────────────────────────────────

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
            NavigateTo(_currentStep - 1);
    }

    private async void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case 1:
                NavigateTo(2);
                await RunSystemCheckAsync();
                break;

            case 2:
                // 跳过已安装的步骤
                if (_wslAlreadyInstalled)
                    NavigateTo(4);
                else
                    NavigateTo(3);

                if (_currentStep == 3)
                    _ = RunWsl2InstallAsync();
                else
                    _ = RunOpenClawInstallAsync();
                break;

            case 3:
                if (_needsReboot)
                {
                    // WSL2 安装后需要重启
                    var result = MessageBox.Show(
                        "WSL2 安装完成，需要重启计算机才能继续。\n\n重启后请重新打开 ClawDock，安装将自动继续。\n\n是否立即重启？",
                        "重启计算机", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        System.Diagnostics.Process.Start("shutdown", "/r /t 5 /c \"ClawDock: WSL2 安装完成，正在重启...\"");
                        Application.Current.Shutdown();
                    }
                    else
                    {
                        // 用户暂不重启，允许稍后重试
                        _needsReboot = false;
                        BtnNext.Content = "重试";
                    }
                }
                else
                {
                    // 重试 WSL2 安装
                    _ = RunWsl2InstallAsync();
                }
                break;

            case 4:
                // 重试 OpenClaw 安装
                _ = RunOpenClawInstallAsync();
                break;

            case 5:
                OpenMainWindow();
                break;
        }
    }

    // ── 安装日志 ─────────────────────────────────────────────────────────

    private void InitInstallLog()
    {
        if (_installLog != null) return;

        Directory.CreateDirectory(LogDir);
        _logFilePath = Path.Combine(LogDir, $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _installLog = new StreamWriter(_logFilePath, append: false) { AutoFlush = true };

        _installLog.WriteLine($"ClawDock Install Log");
        _installLog.WriteLine($"Time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _installLog.WriteLine($"OS   : {Environment.OSVersion}");
        _installLog.WriteLine(new string('─', 60));
    }

    private void WriteLog(string line)
    {
        _installLog?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    }

    // ── 系统检测 ─────────────────────────────────────────────────────────

    private async Task RunSystemCheckAsync()
    {
        InitInstallLog();
        BtnNext.IsEnabled = false;

        WriteLog("── 系统检测 ──");
        var result = await Task.Run(() => _wslService.Check());

        WriteLog($"Windows Build: {result.WindowsBuild} (ok={result.WindowsVersionOk})");
        WriteLog($"Virtualization: {result.VirtualizationEnabled}");
        WriteLog($"WSL2 Installed: {result.Wsl2Installed}");
        WriteLog($"Distro Installed: {result.DistroInstalled}");

        CheckWinVersionText.Text = $"当前版本: Build {result.WindowsBuild} (需要 Build 19041+)";
        SetStatus(CheckWinVersionStatus, result.WindowsVersionOk ? StatusKind.Ok : StatusKind.Error);

        SetStatus(CheckVirtStatus,   result.VirtualizationEnabled ? StatusKind.Ok : StatusKind.Error);
        SetStatus(CheckWslStatus,    result.Wsl2Installed    ? StatusKind.Ok : StatusKind.None);
        SetStatus(CheckUbuntuStatus, result.DistroInstalled  ? StatusKind.Ok : StatusKind.None);

        _wslAlreadyInstalled = result.Wsl2Installed && result.DistroInstalled;

        if (!result.WindowsVersionOk)
        {
            CheckErrorBanner.Visibility = Visibility.Visible;
            CheckErrorText.Text = $"您的 Windows 版本 (Build {result.WindowsBuild}) 过低。WSL2 需要 Windows 10 Build 19041 或更高版本 / Windows 11。请先升级系统。";
            return;
        }

        if (!result.VirtualizationEnabled)
        {
            CheckErrorBanner.Visibility = Visibility.Visible;
            CheckErrorText.Text = "未检测到硬件虚拟化（Hyper-V）支持。WSL2 需要硬件虚拟化才能运行。\n" +
                "• 物理机：请进入 BIOS 开启 VT-x / AMD-V\n" +
                "• 虚拟机：需要宿主机开启嵌套虚拟化";
            return;
        }

        CheckErrorBanner.Visibility = Visibility.Collapsed;
        BtnNext.IsEnabled = true;
    }

    // ── WSL2 安装 ─────────────────────────────────────────────────────────

    private async Task RunWsl2InstallAsync()
    {
        InitInstallLog();
        BtnNext.IsEnabled = false;
        WslLogText.Text = "";

        WriteLog("── WSL2 安装 ──");
        var progress = new Progress<string>(line =>
        {
            WslLogText.Text += line + "\n";
            WslLogScroll.ScrollToBottom();
            WriteLog(line);
        });

        try
        {
            bool ok = await Task.Run(() => _wslService.InstallAsync(
                line => ((IProgress<string>)progress).Report(line),
                phase => _stateService.SavePhase(phase),
                _cts.Token));

            _needsReboot = !ok;

            if (_needsReboot)
            {
                WriteLog("WSL2 安装完成，需要重启");
                WslRebootBanner.Visibility = Visibility.Visible;
                _stateService.SavePhase(InstallPhase.Wsl2Reboot);
                SetResumeOnReboot();
                BtnNext.IsEnabled = true;
                BtnNext.Content = "立即重启";
            }
            else
            {
                WriteLog("WSL2 安装完成，无需重启");
                _stateService.MarkWsl2Done();
                ClearResumeOnReboot(); // WSL 阶段已过，立即清除自启动项，避免 OpenClaw 失败后每次开机弹窗
                BtnNext.IsEnabled = true;
                NavigateTo(4);
                _ = RunOpenClawInstallAsync();
            }
        }
        catch (Exception ex)
        {
            WriteLog($"❌ WSL2 安装异常: {ex}");
            ((IProgress<string>)progress).Report($"\n❌ 错误: {ex.Message}");
            ((IProgress<string>)progress).Report($"详细日志: {_logFilePath}");
            BtnNext.IsEnabled = true;
            BtnNext.Content = "重试";
        }
    }

    // ── OpenClaw 安装 ─────────────────────────────────────────────────────

    private async Task RunOpenClawInstallAsync()
    {
        InitInstallLog();
        BtnNext.IsEnabled = false;
        InstallLogText.Text = "";

        WriteLog("── OpenClaw 安装 ──");
        var progress = new Progress<string>(line =>
        {
            InstallLogText.Text += line + "\n";
            InstallLogScroll.ScrollToBottom();
            WriteLog(line);
        });

        try
        {
            await Task.Run(() => _openClawService.InstallAsync(
                line => ((IProgress<string>)progress).Report(line),
                phase => _stateService.SavePhase(phase),
                _cts.Token));

            WriteLog("OpenClaw 安装完成");
            _stateService.MarkOpenClawDone();
            ClearResumeOnReboot();
            CreateDesktopShortcut();

            NavigateTo(5);
        }
        catch (Exception ex)
        {
            WriteLog($"❌ OpenClaw 安装异常: {ex}");
            ((IProgress<string>)progress).Report($"\n❌ 错误: {ex.Message}");
            ((IProgress<string>)progress).Report($"详细日志: {_logFilePath}");
            BtnNext.IsEnabled = true;
            BtnNext.Content = "重试";
        }
    }

    // ── 重启续装 ──────────────────────────────────────────────────────────

    private static void SetResumeOnReboot()
    {
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (exe == null) return;

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.SetValue("ClawDockResume", $"\"{exe}\"");
    }

    private static void ClearResumeOnReboot()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue("ClawDockResume", throwOnMissingValue: false);
    }

    // ── 桌面快捷方式 ──────────────────────────────────────────────────────

    private void CreateDesktopShortcut()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null) return;

            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var lnkPath = Path.Combine(desktopPath, "ClawDock.lnk");

            if (File.Exists(lnkPath)) return;

            var ps = $"$s=(New-Object -COM WScript.Shell).CreateShortcut('{lnkPath}');" +
                     $"$s.TargetPath='{exePath}';" +
                     $"$s.IconLocation='{exePath}';" +
                     "$s.Save()";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"{ps}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi)?.WaitForExit(5000);

            WriteLog("桌面快捷方式已创建");
        }
        catch (Exception ex)
        {
            WriteLog($"创建桌面快捷方式失败: {ex.Message}");
        }
    }

    // ── 状态图标辅助 ──────────────────────────────────────────────────────

    private enum StatusKind { Ok, Error, Warn, None }

    private void SetStatus(System.Windows.Controls.TextBlock tb, StatusKind kind)
    {
        switch (kind)
        {
            case StatusKind.Ok:
                tb.Text = "✓";
                tb.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                break;
            case StatusKind.Error:
                tb.Text = "✗";
                tb.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
                break;
            case StatusKind.Warn:
                tb.Text = "!";
                tb.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
                break;
            case StatusKind.None:
                tb.Text = "–";
                tb.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                break;
        }
    }

    // ── 打开主窗口 ────────────────────────────────────────────────────────

    private void OpenMainWindow()
    {
        _openingMainWindow = true;
        new MainWindow().Show();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _installLog?.Dispose();
        base.OnClosed(e);
        if (!_openingMainWindow)
            Application.Current.Shutdown();
    }
}
