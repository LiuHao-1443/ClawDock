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

    private int _currentStep = 1;
    private bool _wslAlreadyInstalled;
    private bool _needsReboot;

    public InstallWindow(InstallStateService stateService)
    {
        InitializeComponent();
        _stateService = stateService;

        // 如果上次安装到一半（WSL2 已启用但需要重启），重启后继续：
        // 先回到 Step 3 完成 Ubuntu 导入，再自动进入 Step 4 安装 OpenClaw
        var state = stateService.Load();
        if (state.Phase == InstallPhase.Wsl2)
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
                // WSL2 安装后需要重启
                if (_needsReboot)
                {
                    var result = MessageBox.Show(
                        "WSL2 安装完成，需要重启计算机才能继续。\n\n重启后请重新打开 ClawDock，安装将自动继续。\n\n是否立即重启？",
                        "重启计算机", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        System.Diagnostics.Process.Start("shutdown", "/r /t 5 /c \"ClawDock: WSL2 安装完成，正在重启...\"");
                        Application.Current.Shutdown();
                    }
                }
                break;

            case 5:
                OpenMainWindow();
                break;
        }
    }

    // ── 系统检测 ─────────────────────────────────────────────────────────

    private async Task RunSystemCheckAsync()
    {
        BtnNext.IsEnabled = false;

        var result = await Task.Run(() => _wslService.Check());

        CheckWinVersionText.Text = $"当前版本: Build {result.WindowsBuild} (需要 Build 19041+)";
        SetStatus(CheckWinVersionStatus, result.WindowsVersionOk ? StatusKind.Ok : StatusKind.Error);

        SetStatus(CheckVirtStatus,   result.VirtualizationEnabled ? StatusKind.Ok : StatusKind.Warn);
        SetStatus(CheckWslStatus,    result.Wsl2Installed    ? StatusKind.Ok : StatusKind.None);
        SetStatus(CheckUbuntuStatus, result.UbuntuInstalled  ? StatusKind.Ok : StatusKind.None);

        _wslAlreadyInstalled = result.Wsl2Installed && result.UbuntuInstalled;

        if (!result.WindowsVersionOk)
        {
            CheckErrorBanner.Visibility = Visibility.Visible;
            CheckErrorText.Text = $"您的 Windows 版本 (Build {result.WindowsBuild}) 过低。WSL2 需要 Windows 10 Build 19041 或更高版本 / Windows 11。请先升级系统。";
            return;
        }

        if (!result.VirtualizationEnabled)
        {
            CheckErrorBanner.Visibility = Visibility.Visible;
            CheckErrorText.Text = "未检测到硬件虚拟化支持。请进入 BIOS 开启 VT-x / AMD-V，然后重试。";
            return;
        }

        CheckErrorBanner.Visibility = Visibility.Collapsed;
        BtnNext.IsEnabled = true;
    }

    // ── WSL2 安装 ─────────────────────────────────────────────────────────

    private async Task RunWsl2InstallAsync()
    {
        BtnNext.IsEnabled = false;
        WslLogText.Text = "";

        var progress = new Progress<string>(line =>
        {
            WslLogText.Text += line + "\n";
            WslLogScroll.ScrollToBottom();
        });

        try
        {
            bool ok = await _wslService.InstallAsync(
                line => ((IProgress<string>)progress).Report(line),
                _cts.Token);

            _needsReboot = !ok;

            if (_needsReboot)
            {
                WslRebootBanner.Visibility = Visibility.Visible;
                _stateService.MarkWsl2Done();
                SetResumeOnReboot();
                BtnNext.IsEnabled = true;
                BtnNext.Content = "立即重启";
            }
            else
            {
                _stateService.MarkWsl2Done();
                BtnNext.IsEnabled = true;
                NavigateTo(4);
                _ = RunOpenClawInstallAsync();
            }
        }
        catch (Exception ex)
        {
            ((IProgress<string>)progress).Report($"\n❌ 错误: {ex.Message}");
        }
    }

    // ── OpenClaw 安装 ─────────────────────────────────────────────────────

    private async Task RunOpenClawInstallAsync()
    {
        BtnNext.IsEnabled = false;
        InstallLogText.Text = "";

        var progress = new Progress<string>(line =>
        {
            InstallLogText.Text += line + "\n";
            InstallLogScroll.ScrollToBottom();
        });

        try
        {
            await _openClawService.InstallAsync(
                line => ((IProgress<string>)progress).Report(line),
                _cts.Token);

            _stateService.MarkOpenClawDone();
            ClearResumeOnReboot();

            NavigateTo(5);
        }
        catch (Exception ex)
        {
            ((IProgress<string>)progress).Report($"\n❌ 错误: {ex.Message}");
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
                tb.Text = "—";
                tb.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                break;
        }
    }

    // ── 打开主窗口 ────────────────────────────────────────────────────────

    private void OpenMainWindow()
    {
        new MainWindow().Show();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
