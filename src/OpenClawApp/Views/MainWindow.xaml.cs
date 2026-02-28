using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OpenClawApp.Services;
using WpfApplication = System.Windows.Application;

namespace OpenClawApp.Views;

public partial class MainWindow : Window
{
    private readonly GatewayService _gateway = new();
    private readonly InstallStateService _stateService = new();
    private readonly DispatcherTimer _statusTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    private const string GatewayUrl = "http://localhost:18789";

    public MainWindow()
    {
        InitializeComponent();

        _gateway.StatusChanged += OnGatewayStatusChanged;
        _gateway.LogReceived   += line => System.Diagnostics.Debug.WriteLine("[Gateway] " + line);

        // 定时轮询状态（每 5 秒）
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        _statusTimer.Start();

        InitTrayIcon();
        InitWebView();

        // 启动时自动检测并启动 Gateway
        _ = AutoStartAsync();
    }

    // ── WebView2 初始化 ────────────────────────────────────────────────────

    private async void InitWebView()
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
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

    private void OnGatewayStatusChanged(GatewayStatus status)
    {
        Dispatcher.Invoke(() => ApplyStatus(status));
    }

    private async Task PollStatusAsync()
    {
        var status = await _gateway.GetStatusAsync();
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
    }

    private void ShowBrowser()
    {
        Browser.Visibility       = Visibility.Visible;
        PageNotRunning.Visibility = Visibility.Collapsed;
        PageStarting.Visibility  = Visibility.Collapsed;

        if (Browser.Source?.ToString() != GatewayUrl)
            Browser.Source = new Uri(GatewayUrl);
    }

    private void ShowStarting()
    {
        Browser.Visibility       = Visibility.Collapsed;
        PageNotRunning.Visibility = Visibility.Collapsed;
        PageStarting.Visibility  = Visibility.Visible;
    }

    private void ShowNotRunning()
    {
        Browser.Visibility       = Visibility.Collapsed;
        PageNotRunning.Visibility = Visibility.Visible;
        PageStarting.Visibility  = Visibility.Collapsed;
    }

    // ── 自动启动 ──────────────────────────────────────────────────────────

    private async Task AutoStartAsync()
    {
        var isRunning = await _gateway.IsRunningAsync();
        if (isRunning)
            ApplyStatus(GatewayStatus.Running);
        else
            ApplyStatus(GatewayStatus.Stopped);
    }

    // ── 按钮事件 ──────────────────────────────────────────────────────────

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
        => await _gateway.StartAsync();

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
        => await _gateway.StopAsync();

    private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        => await _gateway.RestartAsync();

    private void BtnOpenBrowser_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(GatewayUrl) { UseShellExecute = true });

    private void BtnUninstall_Click(object sender, RoutedEventArgs e)
        => OpenUninstallWindow();

    // ── 系统托盘 ──────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "OpenClaw",
            Visible = true
        };

        // 从资源加载图标（如果有）
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (System.IO.File.Exists(iconPath))
                _trayIcon.Icon = new System.Drawing.Icon(iconPath);
            else
                _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }
        catch
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => ShowWindow());
        menu.Items.Add("-");
        menu.Items.Add("启动 Gateway", null, async (_, _) => await _gateway.StartAsync());
        menu.Items.Add("停止 Gateway", null, async (_, _) => await _gateway.StopAsync());
        menu.Items.Add("-");
        menu.Items.Add("卸载 OpenClaw", null, (_, _) => OpenUninstallWindow());
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
        _trayIcon?.ShowBalloonTip(2000, "OpenClaw",
            "已最小化到系统托盘，双击图标可重新打开", System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        _trayIcon?.Dispose();
        _statusTimer.Stop();
        _ = _gateway.StopAsync();
        WpfApplication.Current.Shutdown();
    }
}
