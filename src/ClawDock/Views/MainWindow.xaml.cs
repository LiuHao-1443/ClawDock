using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ClawDock.Services;
using WpfApplication = System.Windows.Application;

namespace ClawDock.Views;

public partial class MainWindow : Window
{
    private readonly GatewayService _gateway = new();
    private readonly InstallStateService _stateService = new();
    private readonly DispatcherTimer _statusTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();

        _gateway.StatusChanged += OnGatewayStatusChanged;
        _gateway.LogReceived   += OnGatewayLogReceived;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();

        // 启动时禁用所有按钮，等 WSL 就绪后再启用
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = false;
        BtnRestart.IsEnabled = false;

        InitTrayIcon();
        InitWebView();

        // 先等 WSL 就绪，再检测 Gateway 状态
        _ = InitializeAndAutoStartAsync();
    }

    // ── WebView2 初始化 ────────────────────────────────────────────────────

    private async void InitWebView()
    {
        try
        {
            var userDataDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClawDock", "WebView2");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataDir);
            await Browser.EnsureCoreWebView2Async(env);
            Browser.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // 新窗口请求在同一个 WebView 内打开
                e.Handled = true;
                Browser.CoreWebView2.Navigate(e.Uri);
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 初始化失败: {ex.Message}\n\n请确保已安装 Microsoft Edge。",
                            "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Gateway 状态 ──────────────────────────────────────────────────────

    private GatewayStatus _lastStatus = GatewayStatus.Stopped;

    private void OnGatewayStatusChanged(GatewayStatus status)
    {
        Dispatcher.Invoke(() => ApplyStatus(status));
    }

    private bool _operationInProgress;

    private async Task PollStatusAsync()
    {
        // 操作进行中不轮询，避免干扰启动/停止/重启的状态转换
        if (_operationInProgress) return;

        var status = await _gateway.GetStatusAsync();
        // 轮询只在状态变化时更新 UI，避免重复刷新 WebView2
        if (status != _lastStatus)
            ApplyStatus(status);
    }

    private void ApplyStatus(GatewayStatus status)
    {
        switch (status)
        {
            case GatewayStatus.Running:
                StatusDot.Fill  = (SolidColorBrush)FindResource("SuccessBrush");
                StatusText.Text = "运行中";
                BtnStart.IsEnabled   = false;
                BtnStop.IsEnabled    = true;
                BtnRestart.IsEnabled = true;
                ShowBrowser();
                break;

            case GatewayStatus.Starting:
                StatusDot.Fill  = (SolidColorBrush)FindResource("WarningBrush");
                StatusText.Text = "启动中...";
                BtnStart.IsEnabled   = false;
                BtnStop.IsEnabled    = false;
                BtnRestart.IsEnabled = false;
                ShowStarting();
                break;

            case GatewayStatus.Stopped:
                StatusDot.Fill  = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
                StatusText.Text = "未运行";
                BtnStart.IsEnabled   = true;
                BtnStop.IsEnabled    = false;
                BtnRestart.IsEnabled = false;
                ShowNotRunning();
                break;

            case GatewayStatus.Error:
                StatusDot.Fill  = (SolidColorBrush)FindResource("ErrorBrush");
                StatusText.Text = "启动失败";
                BtnStart.IsEnabled   = true;
                BtnStop.IsEnabled    = false;
                BtnRestart.IsEnabled = false;
                ShowNotRunning();
                if (!string.IsNullOrEmpty(_gateway.LastError))
                {
                    MessageBox.Show(
                        $"Gateway 启动失败：\n\n{_gateway.LastError}\n\n请检查 OpenClaw 是否已正确安装，或尝试重新安装。",
                        "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                break;
        }

        _lastStatus = status;
    }

    private bool _browserNavigated;

    private void ShowBrowser()
    {
        Browser.Visibility         = Visibility.Visible;
        PageNotRunning.Visibility  = Visibility.Collapsed;
        PageStarting.Visibility    = Visibility.Collapsed;
        PageInitializing.Visibility = Visibility.Collapsed;

        // 只在首次切到运行状态时导航，避免反复刷新页面
        if (!_browserNavigated)
        {
            Browser.Source = new Uri(_gateway.DashboardUrl);
            _browserNavigated = true;
        }
    }

    private void ShowStarting()
    {
        Browser.Visibility         = Visibility.Collapsed;
        PageNotRunning.Visibility  = Visibility.Collapsed;
        PageStarting.Visibility    = Visibility.Visible;
        PageInitializing.Visibility = Visibility.Collapsed;
    }

    private void ShowNotRunning()
    {
        Browser.Visibility         = Visibility.Collapsed;
        PageNotRunning.Visibility  = Visibility.Visible;
        PageStarting.Visibility    = Visibility.Collapsed;
        PageInitializing.Visibility = Visibility.Collapsed;
        _browserNavigated = false;
    }

    // ── 初始化 + 自动启动 ──────────────────────────────────────────────────

    private async Task InitializeAndAutoStartAsync()
    {
        // 先检查 Gateway 是否已经在运行（WSL 可能已就绪）
        var isRunning = await _gateway.IsRunningAsync();
        if (isRunning)
        {
            _statusTimer.Start();
            ApplyStatus(GatewayStatus.Running);
            return;
        }

        // Gateway 未运行，需要等 WSL 子系统就绪
        ShowInitializing();

        var ready = await Task.Run(async () =>
        {
            for (int i = 0; i < 60; i++) // 最多等 60 秒
            {
                try
                {
                    var output = "";
                    var exitCode = await WslService.RunCommandStreamAsync("wsl",
                        $"-d {WslService.DistroName} --user root -- echo ok",
                        line => output += line);
                    if (exitCode == 0 && output.Contains("ok"))
                        return true;
                }
                catch { }

                await Task.Delay(1000);
            }
            return false;
        });

        Dispatcher.Invoke(() =>
        {
            _statusTimer.Start();

            if (ready)
            {
                ApplyStatus(GatewayStatus.Stopped);
            }
            else
            {
                StatusDot.Fill = (SolidColorBrush)FindResource("WarningBrush");
                StatusText.Text = "WSL 未就绪";
                BtnStart.IsEnabled = true; // 仍允许手动尝试
                BtnStop.IsEnabled = false;
                BtnRestart.IsEnabled = false;
                ShowNotRunning();
            }
        });
    }

    private void ShowInitializing()
    {
        Browser.Visibility = Visibility.Collapsed;
        PageNotRunning.Visibility = Visibility.Collapsed;
        PageStarting.Visibility = Visibility.Collapsed;
        PageInitializing.Visibility = Visibility.Visible;

        StatusDot.Fill = (SolidColorBrush)FindResource("WarningBrush");
        StatusText.Text = "初始化中...";
    }

    // ── 按钮事件 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 执行 Gateway 操作，操作期间暂停状态轮询，避免干扰状态转换
    /// </summary>
    private async Task RunGatewayOperationAsync(Func<Task> operation)
    {
        _operationInProgress = true;
        try
        {
            await Task.Run(operation);
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
        => await RunGatewayOperationAsync(() => _gateway.StartAsync());

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
        => await RunGatewayOperationAsync(() => _gateway.StopAsync());

    private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        => await RunGatewayOperationAsync(() => _gateway.RestartAsync());

    private void BtnOpenBrowser_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(_gateway.DashboardUrl) { UseShellExecute = true });

    private void BtnUninstall_Click(object sender, RoutedEventArgs e)
        => OpenUninstallWindow();

    // ── 系统托盘 ──────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "ClawDock",
            Visible = true
        };

        // 从内嵌资源加载图标（单文件 exe，磁盘上没有 Assets 目录）
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/icon.ico");
            var info = System.Windows.Application.GetResourceStream(uri);
            _trayIcon.Icon = info != null
                ? new System.Drawing.Icon(info.Stream)
                : System.Drawing.SystemIcons.Application;
        }
        catch
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => ShowWindow());
        menu.Items.Add("-");
        menu.Items.Add("启动 Gateway", null, (_, _) => _ = RunGatewayOperationAsync(() => _gateway.StartAsync()));
        menu.Items.Add("停止 Gateway", null, (_, _) => _ = RunGatewayOperationAsync(() => _gateway.StopAsync()));
        menu.Items.Add("-");
        menu.Items.Add("卸载 ClawDock", null, (_, _) => OpenUninstallWindow());
        menu.Items.Add("-");
        menu.Items.Add("退出", null, (_, _) => ExitApp());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OpenUninstallWindow()
    {
        ShowWindow();
        var win = new UninstallWindow(_gateway, _stateService) { Owner = this };
        win.ShowDialog();
    }

    // ── 窗口关闭 → 最小化到托盘 ──────────────────────────────────────────

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        _trayIcon?.ShowBalloonTip(2000, "ClawDock",
            "已最小化到系统托盘，双击图标可重新打开", System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        _trayIcon?.Dispose();
        _statusTimer.Stop();
        _ = _gateway.StopAsync();
        WpfApplication.Current.Shutdown();
    }

    // ── 日志控制台 ──────────────────────────────────────────────────────

    private const int MaxConsoleLines = 500;
    private int _consoleLineCount;

    private void OnGatewayLogReceived(string line)
    {
        Dispatcher.Invoke(() =>
        {
            // 超出行数时截断前半部分
            if (_consoleLineCount >= MaxConsoleLines)
            {
                var text = ConsoleText.Text;
                var half = text.IndexOf('\n', text.Length / 2);
                if (half > 0)
                {
                    ConsoleText.Text = text[(half + 1)..];
                    _consoleLineCount = MaxConsoleLines / 2;
                }
            }

            ConsoleText.Text += (_consoleLineCount > 0 ? "\n" : "") + line;
            _consoleLineCount++;
            ConsoleScroll.ScrollToEnd();
        });
    }

    private void BtnConsoleToggle_Click(object sender, RoutedEventArgs e)
    {
        ConsolePanel.Visibility = ConsolePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BtnConsoleClear_Click(object sender, RoutedEventArgs e)
    {
        ConsoleText.Text = "";
        _consoleLineCount = 0;
    }
}
