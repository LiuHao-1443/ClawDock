using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClawDock.Services;
using WpfApplication = System.Windows.Application;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;

namespace ClawDock.Views;

public partial class MainWindow : Window
{
    private readonly GatewayService _gateway = new();
    private readonly InstallStateService _stateService = new();
    private readonly OpenClawConfigService _configService = new();
    private readonly DispatcherTimer _statusTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();

        _gateway.StatusChanged += OnGatewayStatusChanged;
        _gateway.LogReceived   += OnGatewayLogReceived;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => _ = SafeFireAndForget(PollStatusAsync());

        // 启动时禁用所有按钮，等 WSL 就绪后再启用
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = false;
        BtnRestart.IsEnabled = false;

        InitTrayIcon();
        InitWebView();

        // 先等 WSL 就绪，再检测 Gateway 状态
        _ = SafeFireAndForget(InitializeAndAutoStartAsync());
    }

    /// <summary>Observe exceptions from fire-and-forget tasks to prevent unobserved task exceptions</summary>
    private async Task SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (Exception ex) { OnGatewayLogReceived($"[错误] 后台任务异常: {ex.Message}"); }
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
        // 使用 BeginInvoke 避免死锁（StatusChanged 可能从线程池触发）
        Dispatcher.BeginInvoke(() => ApplyStatus(status));
    }

    private async Task PollStatusAsync()
    {
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
        PageSettings.Visibility    = Visibility.Collapsed;

        // 后台读取最新 token，导航，并同步到钉钉配置
        _ = Task.Run(async () =>
        {
            _gateway.ReadAuthToken();
            Dispatcher.Invoke(() => NavigateIfNeeded(_gateway.DashboardUrl));
            await _configService.SyncGatewayTokenToDingtalkAsync();
        });
    }

    private void NavigateIfNeeded(string url)
    {
        if (!_browserNavigated ||
            (url.Contains("token=") && Browser.Source?.Query?.Contains("token=") != true))
        {
            Browser.Source = new Uri(url);
            _browserNavigated = true;
        }
    }

    private void ShowStarting()
    {
        Browser.Visibility         = Visibility.Collapsed;
        PageNotRunning.Visibility  = Visibility.Collapsed;
        PageStarting.Visibility    = Visibility.Visible;
        PageInitializing.Visibility = Visibility.Collapsed;
        PageSettings.Visibility    = Visibility.Collapsed;
    }

    private void ShowNotRunning()
    {
        Browser.Visibility         = Visibility.Collapsed;
        PageNotRunning.Visibility  = Visibility.Visible;
        PageStarting.Visibility    = Visibility.Collapsed;
        PageInitializing.Visibility = Visibility.Collapsed;
        PageSettings.Visibility    = Visibility.Collapsed;
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
            _ = SafeFireAndForget(LoadOpenClawVersionAndCheckUpdateAsync());
            _ = SafeFireAndForget(LoadModelNameAsync());
            _ = SafeFireAndForget(EnsureDingtalkPluginInstalledAsync());
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
                _ = SafeFireAndForget(LoadOpenClawVersionAndCheckUpdateAsync());
                _ = SafeFireAndForget(LoadModelNameAsync());
                _ = SafeFireAndForget(EnsureDingtalkPluginInstalledAsync());
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

    private async Task LoadModelNameAsync()
    {
        try
        {
            var config = await _configService.ReadFullConfigAsync();
            Dispatcher.Invoke(() =>
            {
                var model = config.Model.PrimaryModel;
                ModelText.Text = string.IsNullOrEmpty(model) ? "" : $"· {model}";
            });
        }
        catch { }
    }

    private string? _installedVersion;

    private async Task LoadOpenClawVersionAsync()
    {
        try
        {
            var version = "";
            await Task.Run(async () =>
            {
                await WslService.RunCommandStreamAsync("wsl",
                    $"-d {WslService.DistroName} --user root -- openclaw --version",
                    line => version = line.Trim());
            });

            if (!string.IsNullOrEmpty(version))
            {
                _installedVersion = version;
                Dispatcher.Invoke(() => VersionText.Text = $"OpenClaw v{version}");
            }
        }
        catch { }
    }

    private string? _latestVersion;

    private async Task CheckForUpdateAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_installedVersion)) return;

            var openClawService = new OpenClawService();
            _latestVersion = await openClawService.GetLatestVersionAsync();
            if (string.IsNullOrEmpty(_latestVersion)) return;

            // 比较版本号（去掉可能的 v 前缀）
            var installed = _installedVersion.TrimStart('v');
            var latest = _latestVersion.TrimStart('v');

            if (Version.TryParse(installed, out var cur) &&
                Version.TryParse(latest, out var remote) &&
                remote > cur)
            {
                Dispatcher.Invoke(() =>
                {
                    BtnUpdate.Visibility = Visibility.Visible;
                    BtnUpdate.ToolTip = $"v{installed} → v{latest}";
                });
            }
        }
        catch { }
    }

    private async Task LoadOpenClawVersionAndCheckUpdateAsync()
    {
        await LoadOpenClawVersionAsync();
        _ = SafeFireAndForget(CheckForUpdateAsync());
    }

    private void ShowInitializing()
    {
        Browser.Visibility = Visibility.Collapsed;
        PageNotRunning.Visibility = Visibility.Collapsed;
        PageStarting.Visibility = Visibility.Collapsed;
        PageInitializing.Visibility = Visibility.Visible;
        PageSettings.Visibility = Visibility.Collapsed;

        StatusDot.Fill = (SolidColorBrush)FindResource("WarningBrush");
        StatusText.Text = "初始化中...";
    }

    // ── 按钮事件 ──────────────────────────────────────────────────────────

    private void BtnStart_Click(object sender, RoutedEventArgs e)
        => _gateway.Start();

    private void BtnStop_Click(object sender, RoutedEventArgs e)
        => _ = SafeFireAndForget(_gateway.StopAsync());

    private void BtnRestart_Click(object sender, RoutedEventArgs e)
        => _ = SafeFireAndForget(_gateway.RestartAsync());

    private void BtnOpenBrowser_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(_gateway.DashboardUrl) { UseShellExecute = true });

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        var prevStatusText = StatusText.Text;
        StatusText.Text = "更新中...";

        try
        {
            // 记住 Gateway 是否在运行，更新前需要停止
            var wasRunning = _lastStatus == GatewayStatus.Running;
            if (wasRunning)
            {
                OnGatewayLogReceived("停止 Gateway 以进行更新...");
                await _gateway.StopAsync();
                // 等 Gateway 完全停止
                for (int i = 0; i < 30 && await _gateway.IsRunningAsync(); i++)
                    await Task.Delay(500);
            }

            var openClawService = new OpenClawService();
            var success = await Task.Run(async () =>
                await openClawService.UpdateAsync(line => OnGatewayLogReceived(line)));

            if (success)
            {
                // 重新加载版本号
                await LoadOpenClawVersionAsync();
                BtnUpdate.Visibility = Visibility.Collapsed;
                StatusText.Text = "更新完成";

                // 如果之前在运行，自动重启
                if (wasRunning)
                {
                    OnGatewayLogReceived("重新启动 Gateway...");
                    _gateway.Start();
                }
            }
            else
            {
                StatusText.Text = "更新失败";
                BtnUpdate.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            OnGatewayLogReceived($"更新异常: {ex.Message}");
            StatusText.Text = "更新失败";
            BtnUpdate.IsEnabled = true;
        }
    }

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
        menu.Items.Add("启动 Gateway", null, (_, _) => _gateway.Start());
        menu.Items.Add("停止 Gateway", null, (_, _) => _gateway.StopAsync());
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

    // ── 飞书配对批准 ───────────────────────────────────────────────────────

    private async void BtnFeishuPairingApprove_Click(object sender, RoutedEventArgs e)
    {
        var code = TxtFeishuPairingCode.Text?.Trim();
        if (string.IsNullOrEmpty(code) || !System.Text.RegularExpressions.Regex.IsMatch(code, @"^[a-zA-Z0-9\-_]+$"))
        {
            TxtFeishuPairingStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
            TxtFeishuPairingStatus.Text = string.IsNullOrEmpty(code) ? "请输入配对码" : "配对码格式无效";
            return;
        }

        BtnFeishuPairingApprove.IsEnabled = false;
        TxtFeishuPairingStatus.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
        TxtFeishuPairingStatus.Text = "正在批准...";

        try
        {
            var output = "";
            var exitCode = await WslService.RunCommandStreamAsync("wsl",
                $"-d {WslService.DistroName} --user root -- bash -l -c \"openclaw pairing approve feishu {code}\"",
                line => output += line + "\n");

            Dispatcher.Invoke(() =>
            {
                if (exitCode == 0)
                {
                    TxtFeishuPairingStatus.Foreground = (SolidColorBrush)FindResource("SuccessBrush");
                    TxtFeishuPairingStatus.Text = "配对成功";
                    TxtFeishuPairingCode.Text = "";
                }
                else
                {
                    TxtFeishuPairingStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
                    TxtFeishuPairingStatus.Text = $"配对失败: {output.Trim()}";
                }
            });
        }
        catch (Exception ex)
        {
            TxtFeishuPairingStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
            TxtFeishuPairingStatus.Text = $"配对失败: {ex.Message}";
        }
        finally
        {
            BtnFeishuPairingApprove.IsEnabled = true;
        }
    }

    // ── 飞书连接方式切换 ─────────────────────────────────────────────────────

    private void CboFeishuConnectionMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelFeishuVerifyToken == null) return;
        var mode = (CboFeishuConnectionMode.SelectedItem as ComboBoxItem)?.Content?.ToString();
        PanelFeishuVerifyToken.Visibility = mode == "webhook"
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 钉钉事件处理 ─────────────────────────────────────────────────────────

    private async void BtnDingtalkPairingApprove_Click(object sender, RoutedEventArgs e)
    {
        var code = TxtDingtalkPairingCode.Text?.Trim();
        if (string.IsNullOrEmpty(code) || !System.Text.RegularExpressions.Regex.IsMatch(code, @"^[a-zA-Z0-9\-_]+$"))
        {
            TxtDingtalkPairingStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
            TxtDingtalkPairingStatus.Text = string.IsNullOrEmpty(code) ? "请输入配对码" : "配对码格式无效";
            return;
        }

        BtnDingtalkPairingApprove.IsEnabled = false;
        TxtDingtalkPairingStatus.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
        TxtDingtalkPairingStatus.Text = "正在批准...";

        try
        {
            var output = "";
            var exitCode = await WslService.RunCommandStreamAsync("wsl",
                $"-d {WslService.DistroName} --user root -- bash -l -c \"openclaw pairing approve dingtalk-connector {code}\"",
                line => output += line + "\n");

            Dispatcher.Invoke(() =>
            {
                if (exitCode == 0)
                {
                    TxtDingtalkPairingStatus.Foreground = (SolidColorBrush)FindResource("SuccessBrush");
                    TxtDingtalkPairingStatus.Text = "配对成功";
                    TxtDingtalkPairingCode.Text = "";
                }
                else
                {
                    TxtDingtalkPairingStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
                    TxtDingtalkPairingStatus.Text = $"配对失败: {output.Trim()}";
                }
            });
        }
        catch (Exception ex)
        {
            TxtDingtalkPairingStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
            TxtDingtalkPairingStatus.Text = $"配对失败: {ex.Message}";
        }
        finally
        {
            BtnDingtalkPairingApprove.IsEnabled = true;
        }
    }

    private async void BtnDingtalkInstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        BtnDingtalkInstallPlugin.IsEnabled = false;
        TxtDingtalkPluginStatus.Text = "插件状态: 正在安装...";

        try
        {
            var success = await _configService.InstallDingTalkPluginAsync(line =>
                Dispatcher.Invoke(() => OnGatewayLogReceived(line)));

            Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    TxtDingtalkPluginStatus.Text = "插件状态: 已安装";
                    TxtDingtalkPluginStatus.Foreground = (SolidColorBrush)FindResource("SuccessBrush");
                    BtnDingtalkInstallPlugin.Visibility = Visibility.Collapsed;
                    PanelDingtalkConfig.IsEnabled = true;
                }
                else
                {
                    TxtDingtalkPluginStatus.Text = "插件状态: 安装失败";
                    TxtDingtalkPluginStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
                    BtnDingtalkInstallPlugin.IsEnabled = true;
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                TxtDingtalkPluginStatus.Text = $"插件状态: 安装失败 ({ex.Message})";
                TxtDingtalkPluginStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
                BtnDingtalkInstallPlugin.IsEnabled = true;
            });
        }
    }

    private async Task CheckDingtalkPluginStatusAsync()
    {
        try
        {
            var installed = await _configService.IsDingTalkPluginInstalledAsync();
            Dispatcher.Invoke(() =>
            {
                if (installed)
                {
                    TxtDingtalkPluginStatus.Text = "插件状态: 已安装";
                    TxtDingtalkPluginStatus.Foreground = (SolidColorBrush)FindResource("SuccessBrush");
                    BtnDingtalkInstallPlugin.Visibility = Visibility.Collapsed;
                    PanelDingtalkConfig.IsEnabled = true;
                }
                else
                {
                    TxtDingtalkPluginStatus.Text = "插件状态: 未安装";
                    TxtDingtalkPluginStatus.Foreground = (SolidColorBrush)FindResource("WarningBrush");
                    BtnDingtalkInstallPlugin.Visibility = Visibility.Visible;
                    PanelDingtalkConfig.IsEnabled = false;
                }
            });
        }
        catch
        {
            Dispatcher.Invoke(() =>
            {
                TxtDingtalkPluginStatus.Text = "插件状态: 检测失败";
                TxtDingtalkPluginStatus.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
                BtnDingtalkInstallPlugin.Visibility = Visibility.Visible;
                PanelDingtalkConfig.IsEnabled = false;
            });
        }
    }

    // ── 启动时自动补装钉钉插件（覆盖旧版本升级场景） ──────────────────────────

    private async Task EnsureDingtalkPluginInstalledAsync()
    {
        try
        {
            var installed = await _configService.IsDingTalkPluginInstalledAsync();
            if (installed) return;

            await _configService.InstallDingTalkPluginAsync(line =>
                Dispatcher.Invoke(() => OnGatewayLogReceived(line)));
        }
        catch { /* 非致命，静默忽略 */ }
    }

    // ── 设置页 ──────────────────────────────────────────────────────────────

    private bool _settingsLoaded;
    private bool _settingsVisible;
    private bool _apiKeyVisible;
    private bool _providerKeyVisible;
    private bool _isSaving;
    private string _activeSettingsTab = "models";
    private Dictionary<string, List<string>> _modelsByProvider = new();
    private Dictionary<string, string> _providerApiKeys = new();
    private List<ProviderConfig> _customProviders = new();
    private string? _editingProviderName; // tracks original name when editing a provider

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsVisible)
        {
            CloseSettings();
            return;
        }
        ShowSettings();
    }

    private void ShowSettings()
    {
        _settingsVisible = true;
        Browser.Visibility = Visibility.Collapsed;
        PageNotRunning.Visibility = Visibility.Collapsed;
        PageStarting.Visibility = Visibility.Collapsed;
        PageInitializing.Visibility = Visibility.Collapsed;
        PageSettings.Visibility = Visibility.Visible;

        ApplySettingsTab();

        if (!_settingsLoaded)
            _ = SafeFireAndForget(LoadSettingsAsync());
    }

    private void CloseSettings()
    {
        _settingsVisible = false;
        PageSettings.Visibility = Visibility.Collapsed;

        // Restore the previous page based on last known status
        switch (_lastStatus)
        {
            case GatewayStatus.Running:
                ShowBrowser();
                break;
            case GatewayStatus.Starting:
                ShowStarting();
                break;
            default:
                ShowNotRunning();
                break;
        }
    }

    private void BtnSettingsBack_Click(object sender, RoutedEventArgs e) => CloseSettings();

    // ── Tab 切换 ────────────────────────────────────────────────────────────

    private void TabModels_Click(object sender, RoutedEventArgs e)
    {
        _activeSettingsTab = "models";
        ApplySettingsTab();
    }

    private void TabChannels_Click(object sender, RoutedEventArgs e)
    {
        _activeSettingsTab = "channels";
        ApplySettingsTab();
    }

    private void ApplySettingsTab()
    {
        var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
        var secondaryBrush = (SolidColorBrush)FindResource("TextSecondaryBrush");

        if (_activeSettingsTab == "models")
        {
            SettingsTabModels.Visibility = Visibility.Visible;
            SettingsTabChannels.Visibility = Visibility.Collapsed;
            TabModels.Foreground = accentBrush;
            TabChannels.Foreground = secondaryBrush;
        }
        else
        {
            SettingsTabModels.Visibility = Visibility.Collapsed;
            SettingsTabChannels.Visibility = Visibility.Visible;
            TabModels.Foreground = secondaryBrush;
            TabChannels.Foreground = accentBrush;
        }
    }

    // ── 服务商切换 → 更新模型列表 ──────────────────────────────────────────

    private void CboProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var provider = CboProvider.SelectedItem?.ToString();
        CboPrimaryModel.Items.Clear();
        if (provider != null && _modelsByProvider.TryGetValue(provider, out var models))
        {
            foreach (var m in models)
                CboPrimaryModel.Items.Add(m);
        }
        CboPrimaryModel.Text = "";

        // Load API key for selected provider
        var apiKey = "";
        if (provider != null)
            _providerApiKeys.TryGetValue(provider, out apiKey);
        apiKey ??= "";
        PwdSelectedProviderKey.Password = apiKey;
        TxtSelectedProviderKey.Text = apiKey;
        TxtProviderApiKeyHint.Text = string.IsNullOrEmpty(provider)
            ? "当前服务商的 API Key"
            : $"{provider} 的 API Key";
    }

    // ── 加载配置 ────────────────────────────────────────────────────────────

    private async Task LoadSettingsAsync()
    {
        SettingsLoading.Visibility = Visibility.Visible;
        SettingsError.Visibility = Visibility.Collapsed;
        SettingsTabModels.Visibility = Visibility.Collapsed;
        SettingsTabChannels.Visibility = Visibility.Collapsed;

        try
        {
            OnGatewayLogReceived("[设置] 正在加载配置...");
            var configTask = _configService.ReadFullConfigAsync();
            var modelsTask = _configService.ListAvailableModelsAsync();
            await Task.WhenAll(configTask, modelsTask);
            var config = configTask.Result;
            var availableModels = modelsTask.Result;

            if (config.ParseError != null)
                OnGatewayLogReceived($"[设置] 配置解析警告: {config.ParseError}");
            OnGatewayLogReceived($"[设置] 配置加载完成: {config.Providers.Count} 个 Provider, {availableModels.Count} 个可用模型");

            Dispatcher.Invoke(() =>
            {
                // Group models by provider
                _modelsByProvider.Clear();
                foreach (var m in availableModels)
                {
                    var slash = m.IndexOf('/');
                    if (slash <= 0) continue;
                    var provider = m[..slash].Trim();
                    if (string.IsNullOrWhiteSpace(provider)) continue;
                    if (!_modelsByProvider.ContainsKey(provider))
                        _modelsByProvider[provider] = new List<string>();
                    _modelsByProvider[provider].Add(m[(slash + 1)..]);
                }

                // Merge custom providers from config into modelsByProvider
                _providerApiKeys.Clear();
                foreach (var p in config.Providers)
                {
                    if (!string.IsNullOrEmpty(p.ApiKey))
                        _providerApiKeys[p.Name] = p.ApiKey;

                    if (!string.IsNullOrEmpty(p.Name) && p.Models.Count > 0 && !_modelsByProvider.ContainsKey(p.Name))
                        _modelsByProvider[p.Name] = p.Models.Select(m => m.Name).ToList();
                }

                // Populate provider dropdown
                CboProvider.SelectionChanged -= CboProvider_SelectionChanged;
                CboProvider.Items.Clear();
                foreach (var p in _modelsByProvider.Keys.OrderBy(p => p))
                {
                    if (!string.IsNullOrWhiteSpace(p))
                        CboProvider.Items.Add(p);
                }

                // Pre-select provider from current model
                var currentModel = config.Model.PrimaryModel;
                var currentSlash = currentModel.IndexOf('/');
                string curProvider = "";
                if (currentSlash > 0)
                {
                    curProvider = currentModel[..currentSlash];
                    CboProvider.SelectedItem = curProvider;
                    CboPrimaryModel.Text = currentModel[(currentSlash + 1)..];
                }
                else
                {
                    CboPrimaryModel.Text = currentModel;
                }
                CboProvider.SelectionChanged += CboProvider_SelectionChanged;

                // Update status bar model name
                ModelText.Text = string.IsNullOrEmpty(currentModel) ? "" : $"· {currentModel}";

                // Load API key for initially selected provider
                var initKey = "";
                if (!string.IsNullOrEmpty(curProvider))
                    _providerApiKeys.TryGetValue(curProvider, out initKey);
                initKey ??= "";
                PwdSelectedProviderKey.Password = initKey;
                TxtSelectedProviderKey.Text = initKey;
                TxtProviderApiKeyHint.Text = string.IsNullOrEmpty(curProvider)
                    ? "当前服务商的 API Key"
                    : $"{curProvider} 的 API Key";

                // Fallback models
                FallbackModelsList.Children.Clear();
                foreach (var fb in config.Model.FallbackModels)
                    AddFallbackModelRow(fb);

                // Provider list
                _customProviders = config.Providers;
                RebuildCustomProviderList();
                // Restore editing context if the provider still exists, otherwise load first
                var restoreProvider = _editingProviderName != null
                    ? config.Providers.FirstOrDefault(p => p.Name == _editingProviderName)
                    : null;
                if (restoreProvider != null)
                    LoadProviderIntoForm(restoreProvider);
                else
                {
                    _editingProviderName = null;
                    if (config.Providers.Count > 0)
                        LoadProviderIntoForm(config.Providers[0]);
                }

                // Channels
                foreach (var ch in config.Channels)
                {
                    switch (ch.Name)
                    {
                        case "feishu":
                            ChkFeishuEnabled.IsChecked = ch.Enabled;
                            if (ch.Properties.TryGetValue("appId", out var fsAppId))
                                TxtFeishuAppId.Text = fsAppId;
                            if (ch.Properties.TryGetValue("appSecret", out var fsAppSecret))
                                PwdFeishuAppSecret.Password = fsAppSecret;
                            if (ch.Properties.TryGetValue("connectionMode", out var fsMode))
                                SelectComboItem(CboFeishuConnectionMode, fsMode);
                            if (ch.Properties.TryGetValue("verifyToken", out var fsVerify))
                                PwdFeishuVerifyToken.Password = fsVerify;
                            // Show verify token panel if webhook mode
                            var mode = (CboFeishuConnectionMode.SelectedItem as ComboBoxItem)?.Content?.ToString();
                            PanelFeishuVerifyToken.Visibility = mode == "webhook"
                                ? Visibility.Visible : Visibility.Collapsed;
                            break;

                        case "dingtalk-connector":
                            ChkDingtalkEnabled.IsChecked = ch.Enabled;
                            if (ch.Properties.TryGetValue("clientId", out var dtClientId))
                                TxtDingtalkClientId.Text = dtClientId;
                            if (ch.Properties.TryGetValue("clientSecret", out var dtClientSecret))
                                PwdDingtalkClientSecret.Password = dtClientSecret;
                            if (ch.Properties.TryGetValue("dmPolicy", out var dtDmPolicy))
                                SelectComboItem(CboDingtalkDmPolicy, dtDmPolicy);
                            if (ch.Properties.TryGetValue("groupPolicy", out var dtGroupPolicy))
                                SelectComboItem(CboDingtalkGroupPolicy, dtGroupPolicy);
                            break;
                    }
                }

                SettingsLoading.Visibility = Visibility.Collapsed;
                _settingsLoaded = true;
                ApplySettingsTab();

                // Check DingTalk plugin status asynchronously
                _ = SafeFireAndForget(CheckDingtalkPluginStatusAsync());
            });
        }
        catch (Exception ex)
        {
            OnGatewayLogReceived($"[设置] 配置加载失败: {ex.Message}");
            Dispatcher.Invoke(() =>
            {
                SettingsLoading.Visibility = Visibility.Collapsed;
                SettingsError.Text = $"加载配置失败: {ex.Message}\n\n请确保 WSL 已启动且 OpenClaw 已安装。";
                SettingsError.Visibility = Visibility.Visible;
            });
        }
    }

    private static void SelectComboItem(System.Windows.Controls.ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Content?.ToString() == value)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    // ── 动态列表：备选模型 ──────────────────────────────────────────────────

    private void AddFallbackModelRow(string value = "")
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = new WpfTextBox
        {
            Text = value,
            Style = (Style)FindResource("DarkTextBox")
        };
        Grid.SetColumn(tb, 0);
        row.Children.Add(tb);

        var btn = new WpfButton
        {
            Content = "✕",
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 12
        };
        btn.Click += (_, _) => FallbackModelsList.Children.Remove(row);
        Grid.SetColumn(btn, 1);
        row.Children.Add(btn);

        FallbackModelsList.Children.Add(row);
    }

    private void BtnAddFallback_Click(object sender, RoutedEventArgs e) => AddFallbackModelRow();

    // ── 动态列表：Provider 模型 ─────────────────────────────────────────────

    private void AddProviderModelRow(string name = "", string id = "",
        int? contextWindow = null, int? maxTokens = null, bool? reasoning = null)
    {
        var fieldLabelStyle = (Style)FindResource("FieldLabel");
        var darkTextBoxStyle = (Style)FindResource("DarkTextBox");

        var wrapper = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        // ── Labels row 1: Name / ID ──
        var lblRow1 = new Grid();
        lblRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lblRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lblRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lblName = new TextBlock { Text = "Name（显示名）", Style = fieldLabelStyle, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetColumn(lblName, 0);
        lblRow1.Children.Add(lblName);

        var lblId = new TextBlock { Text = "ID（模型标识）", Style = fieldLabelStyle, Margin = new Thickness(6, 0, 0, 4) };
        Grid.SetColumn(lblId, 1);
        lblRow1.Children.Add(lblId);

        // ── Inputs row 1: Name + ID + ✕ ──
        var inputRow1 = new Grid();
        inputRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tbName = new WpfTextBox { Text = name, Style = darkTextBoxStyle, Tag = "name" };
        Grid.SetColumn(tbName, 0);
        inputRow1.Children.Add(tbName);

        var tbId = new WpfTextBox { Text = id, Style = darkTextBoxStyle, Margin = new Thickness(6, 0, 0, 0), Tag = "id" };
        Grid.SetColumn(tbId, 1);
        inputRow1.Children.Add(tbId);

        var btn = new WpfButton
        {
            Content = "✕",
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 12
        };
        btn.Click += (_, _) => ProviderModelsList.Children.Remove(wrapper);
        Grid.SetColumn(btn, 2);
        inputRow1.Children.Add(btn);

        // ── Advanced panel (collapsed by default) ──
        var advancedPanel = new StackPanel { Visibility = Visibility.Collapsed };

        // Labels row 2: Context Window / Max Tokens
        var lblRow2 = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        lblRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lblRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lblRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lblCw = new TextBlock { Text = "Context Window", Style = fieldLabelStyle, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetColumn(lblCw, 0);
        lblRow2.Children.Add(lblCw);

        var lblMt = new TextBlock { Text = "Max Tokens", Style = fieldLabelStyle, Margin = new Thickness(6, 0, 0, 4) };
        Grid.SetColumn(lblMt, 1);
        lblRow2.Children.Add(lblMt);

        // Inputs row 2: Context Window + Max Tokens
        var inputRow2 = new Grid();
        inputRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tbContextWindow = new WpfTextBox { Text = contextWindow?.ToString() ?? "", Style = darkTextBoxStyle, Tag = "contextWindow" };
        Grid.SetColumn(tbContextWindow, 0);
        inputRow2.Children.Add(tbContextWindow);

        var tbMaxTokens = new WpfTextBox { Text = maxTokens?.ToString() ?? "", Style = darkTextBoxStyle, Margin = new Thickness(6, 0, 0, 0), Tag = "maxTokens" };
        Grid.SetColumn(tbMaxTokens, 1);
        inputRow2.Children.Add(tbMaxTokens);

        // Row 3: Reasoning label + ComboBox
        var lblReasoning = new TextBlock { Text = "思考模式（Reasoning）", Style = fieldLabelStyle, Margin = new Thickness(0, 8, 0, 4) };

        var cboReasoning = new System.Windows.Controls.ComboBox
        {
            Style = (Style)FindResource("DarkComboBoxReadOnly"),
            Tag = "reasoning"
        };
        var reasoningItems = new[]
        {
            ("不设置（使用默认）", ""),
            ("开启", "true"),
            ("关闭", "false")
        };
        foreach (var (label, value) in reasoningItems)
        {
            var item = new ComboBoxItem
            {
                Content = label,
                Tag = value,
                Style = (Style)FindResource("DarkComboBoxItem")
            };
            cboReasoning.Items.Add(item);
        }
        // Select based on current value
        var selectedIdx = reasoning switch { true => 1, false => 2, _ => 0 };
        cboReasoning.SelectedIndex = selectedIdx;

        advancedPanel.Children.Add(lblRow2);
        advancedPanel.Children.Add(inputRow2);
        advancedPanel.Children.Add(lblReasoning);
        advancedPanel.Children.Add(cboReasoning);

        // Toggle button
        var hasAdvanced = contextWindow.HasValue || maxTokens.HasValue || reasoning.HasValue;
        if (hasAdvanced) advancedPanel.Visibility = Visibility.Visible;

        var btnAdvanced = new WpfButton
        {
            Content = hasAdvanced ? "▾ 收起高级选项" : "▸ 高级选项",
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent
        };
        btnAdvanced.Click += (_, _) =>
        {
            if (advancedPanel.Visibility == Visibility.Collapsed)
            {
                advancedPanel.Visibility = Visibility.Visible;
                btnAdvanced.Content = "▾ 收起高级选项";
            }
            else
            {
                advancedPanel.Visibility = Visibility.Collapsed;
                btnAdvanced.Content = "▸ 高级选项";
            }
        };

        wrapper.Children.Add(lblRow1);
        wrapper.Children.Add(inputRow1);
        wrapper.Children.Add(btnAdvanced);
        wrapper.Children.Add(advancedPanel);
        ProviderModelsList.Children.Add(wrapper);
    }

    private static void CollectModelFields(System.Windows.Controls.Panel parent,
        ref string? mName, ref string? mId, ref int? mContextWindow, ref int? mMaxTokens, ref bool? mReasoning,
        List<string>? validationErrors = null)
    {
        foreach (var child in parent.Children)
        {
            if (child is System.Windows.Controls.Panel nested)
            {
                CollectModelFields(nested, ref mName, ref mId, ref mContextWindow, ref mMaxTokens, ref mReasoning, validationErrors);
            }
            else if (child is WpfTextBox tb)
            {
                var text = tb.Text?.Trim();
                switch (tb.Tag?.ToString())
                {
                    case "name": mName = text; break;
                    case "id": mId = text; break;
                    case "contextWindow":
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (int.TryParse(text, out var cw) && cw > 0 && cw <= 10_000_000) mContextWindow = cw;
                            else validationErrors?.Add($"Context Window 值无效（需为 1~10000000 的整数）: {text}");
                        }
                        break;
                    case "maxTokens":
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (int.TryParse(text, out var mt) && mt > 0 && mt <= 10_000_000) mMaxTokens = mt;
                            else validationErrors?.Add($"Max Tokens 值无效（需为 1~10000000 的整数）: {text}");
                        }
                        break;
                }
            }
            else if (child is System.Windows.Controls.ComboBox cbo && cbo.Tag?.ToString() == "reasoning")
            {
                var val = (cbo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                if (val == "true") mReasoning = true;
                else if (val == "false") mReasoning = false;
            }
        }
    }

    private void BtnAddProviderModel_Click(object sender, RoutedEventArgs e) => AddProviderModelRow();

    // ── API Key 显示/隐藏 ───────────────────────────────────────────────────

    private void BtnToggleApiKey_Click(object sender, RoutedEventArgs e)
    {
        _apiKeyVisible = !_apiKeyVisible;
        if (_apiKeyVisible)
        {
            TxtProviderApiKeyVisible.Text = PwdProviderApiKey.Password;
            TxtProviderApiKeyVisible.Visibility = Visibility.Visible;
            PwdProviderApiKey.Visibility = Visibility.Collapsed;
            BtnToggleApiKey.Content = "隐藏";
        }
        else
        {
            PwdProviderApiKey.Password = TxtProviderApiKeyVisible.Text;
            PwdProviderApiKey.Visibility = Visibility.Visible;
            TxtProviderApiKeyVisible.Visibility = Visibility.Collapsed;
            BtnToggleApiKey.Content = "显示";
        }
    }

    // ── 服务商 API Key 显示/隐藏 ────────────────────────────────────────────

    private void BtnToggleSelectedProviderKey_Click(object sender, RoutedEventArgs e)
    {
        _providerKeyVisible = !_providerKeyVisible;
        if (_providerKeyVisible)
        {
            TxtSelectedProviderKey.Text = PwdSelectedProviderKey.Password;
            TxtSelectedProviderKey.Visibility = Visibility.Visible;
            PwdSelectedProviderKey.Visibility = Visibility.Collapsed;
            BtnToggleSelectedProviderKey.Content = "隐藏";
        }
        else
        {
            PwdSelectedProviderKey.Password = TxtSelectedProviderKey.Text;
            PwdSelectedProviderKey.Visibility = Visibility.Visible;
            TxtSelectedProviderKey.Visibility = Visibility.Collapsed;
            BtnToggleSelectedProviderKey.Content = "显示";
        }
    }

    // ── 自定义 Provider 列表 ────────────────────────────────────────────────

    private void LoadProviderIntoForm(ProviderConfig p)
    {
        _editingProviderName = p.Name;
        TxtProviderName.Text = p.Name;
        TxtProviderBaseUrl.Text = p.BaseUrl;
        PwdProviderApiKey.Password = p.ApiKey;
        TxtProviderApiKeyVisible.Text = p.ApiKey;

        // Reset API key visibility to hidden state
        _apiKeyVisible = false;
        PwdProviderApiKey.Visibility = Visibility.Visible;
        TxtProviderApiKeyVisible.Visibility = Visibility.Collapsed;
        BtnToggleApiKey.Content = "显示";

        foreach (ComboBoxItem item in CboProviderApi.Items)
        {
            if (item.Tag?.ToString() == p.Api)
            {
                CboProviderApi.SelectedItem = item;
                break;
            }
        }

        ProviderModelsList.Children.Clear();
        foreach (var m in p.Models)
            AddProviderModelRow(m.Name, m.Id, m.ContextWindow, m.MaxTokens, m.Reasoning);
    }

    private void ClearProviderForm()
    {
        _editingProviderName = null;
        TxtProviderName.Text = "";
        TxtProviderBaseUrl.Text = "";
        PwdProviderApiKey.Password = "";
        TxtProviderApiKeyVisible.Text = "";
        CboProviderApi.SelectedIndex = 0;
        ProviderModelsList.Children.Clear();

        // Reset API key visibility to hidden state
        _apiKeyVisible = false;
        PwdProviderApiKey.Visibility = Visibility.Visible;
        TxtProviderApiKeyVisible.Visibility = Visibility.Collapsed;
        BtnToggleApiKey.Content = "显示";
    }

    private void RebuildCustomProviderList()
    {
        CustomProviderList.Children.Clear();
        if (_customProviders.Count == 0) return;

        var textPrimaryBrush = (SolidColorBrush)FindResource("TextPrimaryBrush");
        var secondaryButtonStyle = (Style)FindResource("SecondaryButton");
        var errorBrush = (SolidColorBrush)FindResource("ErrorBrush");

        foreach (var p in _customProviders)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = $"{p.Name}（{p.Models.Count} 个模型）",
                Foreground = textPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13
            };
            Grid.SetColumn(lbl, 0);
            row.Children.Add(lbl);

            var providerRef = p;

            var btnEdit = new WpfButton
            {
                Content = "编辑",
                Style = secondaryButtonStyle,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 11,
                IsEnabled = !_isSaving
            };
            btnEdit.Click += (_, _) => LoadProviderIntoForm(providerRef);
            Grid.SetColumn(btnEdit, 1);
            row.Children.Add(btnEdit);

            var btnDel = new WpfButton
            {
                Content = "删除",
                Style = secondaryButtonStyle,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(4, 0, 0, 0),
                FontSize = 11,
                Foreground = errorBrush,
                IsEnabled = !_isSaving
            };
            btnDel.Click += async (_, _) =>
            {
                try { await DeleteCustomProviderAsync(providerRef.Name); }
                catch (Exception ex)
                {
                    TxtModelSaveStatus.Foreground = errorBrush;
                    TxtModelSaveStatus.Text = $"删除失败: {ex.Message}";
                }
            };
            Grid.SetColumn(btnDel, 2);
            row.Children.Add(btnDel);

            CustomProviderList.Children.Add(row);
        }
    }

    private async Task DeleteCustomProviderAsync(string providerName)
    {
        if (_isSaving) return;

        var result = System.Windows.MessageBox.Show(
            $"确定要删除自定义 Provider「{providerName}」吗？",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _isSaving = true;
        RebuildCustomProviderList();
        try
        {
            OnGatewayLogReceived($"[设置] 删除自定义 Provider: {providerName}");
            var ok = await _configService.DeleteProviderAsync(providerName);
            if (ok)
            {
                OnGatewayLogReceived($"[设置] Provider「{providerName}」已删除");
                _customProviders.RemoveAll(p => p.Name == providerName);
                _modelsByProvider.Remove(providerName);
                _providerApiKeys.Remove(providerName);
                CboProvider.Items.Remove(providerName);

                // Clear form if it was showing the deleted provider
                if (_editingProviderName == providerName)
                    ClearProviderForm();
            }
            else
            {
                OnGatewayLogReceived($"[设置] 删除 Provider「{providerName}」失败");
                TxtModelSaveStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
                TxtModelSaveStatus.Text = $"删除 {providerName} 失败";
            }
        }
        finally
        {
            _isSaving = false;
            RebuildCustomProviderList();
        }
    }

    private void BtnNewProvider_Click(object sender, RoutedEventArgs e) => ClearProviderForm();

    // ── 保存模型配置 ────────────────────────────────────────────────────────

    private async void BtnSaveModels_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;
        _isSaving = true;
        BtnSaveModels.IsEnabled = false;
        RebuildCustomProviderList();
        TxtModelSaveStatus.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
        TxtModelSaveStatus.Text = "保存中...";

        try
        {
            // ── Pre-validate all inputs before any save operations ──
            var providerName = TxtProviderName.Text?.Trim();
            if (!string.IsNullOrEmpty(providerName))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(providerName, @"^[\w\-\u4e00-\u9fff]+$"))
                {
                    OnGatewayLogReceived($"[设置] 验证失败: Provider 名称「{providerName}」格式无效");
                    TxtModelSaveStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
                    TxtModelSaveStatus.Text = "Provider 名称只能包含字母、数字、下划线、连字符和中文";
                    BtnSaveModels.IsEnabled = true;
                    _isSaving = false;
                    RebuildCustomProviderList();
                    return;
                }

                var preValidationErrors = new List<string>();
                foreach (var child in ProviderModelsList.Children)
                {
                    if (child is StackPanel wrapper)
                    {
                        string? pn = null, pi = null;
                        int? pcw = null, pmt = null;
                        bool? pr = null;
                        CollectModelFields(wrapper, ref pn, ref pi, ref pcw, ref pmt, ref pr, preValidationErrors);
                    }
                }
                if (preValidationErrors.Count > 0)
                {
                    OnGatewayLogReceived($"[设置] 验证失败: {string.Join("; ", preValidationErrors)}");
                    TxtModelSaveStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
                    TxtModelSaveStatus.Text = string.Join("; ", preValidationErrors);
                    BtnSaveModels.IsEnabled = true;
                    _isSaving = false;
                    RebuildCustomProviderList();
                    return;
                }
            }

            // ── Capture all UI values before any await to avoid stale reads ──
            var selectedProvider = CboProvider.SelectedItem?.ToString() ?? "";
            var modelName = CboPrimaryModel.Text?.Trim() ?? "";
            var fullModel = !string.IsNullOrEmpty(selectedProvider) && !string.IsNullOrEmpty(modelName)
                && !modelName.StartsWith(selectedProvider + "/")
                ? $"{selectedProvider}/{modelName}" : modelName;
            var providerKeyValue = _providerKeyVisible
                ? TxtSelectedProviderKey.Text?.Trim() ?? ""
                : PwdSelectedProviderKey.Password ?? "";
            var providerApiKeyValue = _apiKeyVisible
                ? TxtProviderApiKeyVisible.Text?.Trim() ?? ""
                : PwdProviderApiKey.Password ?? "";
            var providerBaseUrl = TxtProviderBaseUrl.Text?.Trim() ?? "";
            var providerApi = (CboProviderApi.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "openai-responses";

            var modelConfig = new ModelConfig
            {
                PrimaryModel = fullModel
            };

            // Collect fallback models
            foreach (var child in FallbackModelsList.Children)
            {
                if (child is Grid row)
                {
                    foreach (var c in row.Children)
                    {
                        if (c is WpfTextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
                            modelConfig.FallbackModels.Add(tb.Text.Trim());
                    }
                }
            }

            OnGatewayLogReceived($"[设置] 保存模型配置: primary={fullModel}, fallbacks=[{string.Join(", ", modelConfig.FallbackModels)}]");
            var failures = await _configService.SaveModelConfigAsync(modelConfig);
            if (!string.IsNullOrEmpty(selectedProvider) && !string.IsNullOrEmpty(providerKeyValue))
            {
                OnGatewayLogReceived($"[设置] 保存 {selectedProvider} 的 API Key...");
                try
                {
                    if (!await _configService.SaveProviderApiKeyAsync(selectedProvider, providerKeyValue))
                    {
                        OnGatewayLogReceived($"[设置] {selectedProvider} API Key 保存失败");
                        failures.Add($"models.providers.{selectedProvider}.apiKey");
                    }
                    else
                    {
                        OnGatewayLogReceived($"[设置] {selectedProvider} API Key 保存成功");
                        _providerApiKeys[selectedProvider] = providerKeyValue;
                    }
                }
                catch (Exception ex2)
                {
                    OnGatewayLogReceived($"[设置] {selectedProvider} API Key 保存异常: {ex2.Message}");
                    failures.Add($"[EX] {ex2.Message}");
                }
            }

            // Save custom provider if name is specified
            if (!string.IsNullOrEmpty(providerName))
            {
                var provider = new ProviderConfig
                {
                    Name = providerName,
                    BaseUrl = providerBaseUrl,
                    ApiKey = providerApiKeyValue,
                    Api = providerApi
                };

                // Collect provider models (already validated in pre-validation above)
                foreach (var child in ProviderModelsList.Children)
                {
                    if (child is StackPanel wrapper)
                    {
                        string? mName = null, mId = null;
                        int? mContextWindow = null, mMaxTokens = null;
                        bool? mReasoning = null;

                        CollectModelFields(wrapper, ref mName, ref mId, ref mContextWindow, ref mMaxTokens, ref mReasoning);

                        if (!string.IsNullOrWhiteSpace(mName))
                            provider.Models.Add(new ProviderModel
                            {
                                Name = mName!,
                                Id = mId ?? mName!,
                                ContextWindow = mContextWindow,
                                MaxTokens = mMaxTokens,
                                Reasoning = mReasoning
                            });
                    }
                }

                var modelDetails = string.Join(", ", provider.Models.Select(m =>
                {
                    var parts = new List<string> { $"name={m.Name}", $"id={m.Id}" };
                    if (m.ContextWindow.HasValue) parts.Add($"contextWindow={m.ContextWindow}");
                    if (m.MaxTokens.HasValue) parts.Add($"maxTokens={m.MaxTokens}");
                    if (m.Reasoning.HasValue) parts.Add($"reasoning={m.Reasoning}");
                    return "{" + string.Join(", ", parts) + "}";
                }));
                OnGatewayLogReceived($"[设置] 保存自定义 Provider: {providerName}, baseUrl={provider.BaseUrl}, api={provider.Api}, models=[{modelDetails}]");
                var pf = await _configService.SaveProviderAsync(provider);
                failures.AddRange(pf);

                // Update custom provider list and dropdown
                if (pf.Count == 0)
                {
                    // If provider was renamed, delete old one from config and local state
                    if (_editingProviderName != null && _editingProviderName != providerName)
                    {
                        OnGatewayLogReceived($"[设置] Provider 重命名: {_editingProviderName} → {providerName}，删除旧配置");
                        var deleteOk = await _configService.DeleteProviderAsync(_editingProviderName);
                        if (deleteOk)
                        {
                            _customProviders.RemoveAll(cp => cp.Name == _editingProviderName);
                            _modelsByProvider.Remove(_editingProviderName);
                            _providerApiKeys.Remove(_editingProviderName);
                            CboProvider.Items.Remove(_editingProviderName);
                        }
                        else
                        {
                            OnGatewayLogReceived($"[设置] 警告: 旧 Provider「{_editingProviderName}」删除失败，可能存在残留配置");
                            failures.Add($"delete:{_editingProviderName}");
                        }
                    }

                    _modelsByProvider[providerName] = provider.Models.Select(m => m.Name).ToList();
                    if (!CboProvider.Items.Contains(providerName))
                        CboProvider.Items.Add(providerName);

                    if (!string.IsNullOrEmpty(provider.ApiKey))
                        _providerApiKeys[providerName] = provider.ApiKey;

                    // Update _customProviders list
                    var existing = _customProviders.FindIndex(cp => cp.Name == providerName);
                    if (existing >= 0)
                        _customProviders[existing] = provider;
                    else
                        _customProviders.Add(provider);
                    RebuildCustomProviderList();
                    _editingProviderName = providerName;
                }
            }

            if (failures.Count == 0)
            {
                OnGatewayLogReceived("[设置] 模型配置保存成功");
                TxtModelSaveStatus.Foreground = (SolidColorBrush)FindResource("SuccessBrush");
                TxtModelSaveStatus.Text = "保存成功";
                ModelText.Text = string.IsNullOrEmpty(fullModel) ? "" : $"· {fullModel}";
            }
            else
            {
                var renameFailures = failures.Where(f => f.StartsWith("delete:")).ToList();
                var otherFailures = failures.Where(f => !f.StartsWith("delete:")).ToList();
                OnGatewayLogReceived($"[设置] 模型配置保存失败: {string.Join(", ", failures)}");
                TxtModelSaveStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
                if (renameFailures.Count > 0 && otherFailures.Count == 0)
                    TxtModelSaveStatus.Text = $"保存成功，但旧 Provider 删除失败，请手动删除: {string.Join(", ", renameFailures.Select(f => f[7..]))}";
                else
                    TxtModelSaveStatus.Text = $"部分保存失败: {string.Join(", ", failures)}";
            }
        }
        catch (Exception ex)
        {
            TxtModelSaveStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
            TxtModelSaveStatus.Text = $"保存失败: {ex.Message}";
        }
        finally
        {
            BtnSaveModels.IsEnabled = true;
            _isSaving = false;
            RebuildCustomProviderList();
        }
    }

    // ── 保存渠道配置 ────────────────────────────────────────────────────────

    private async void BtnSaveChannels_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving) return;
        _isSaving = true;
        BtnSaveChannels.IsEnabled = false;
        BtnSaveModels.IsEnabled = false;
        RebuildCustomProviderList();
        TxtChannelSaveStatus.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
        TxtChannelSaveStatus.Text = "保存中...";

        try
        {
            OnGatewayLogReceived("[设置] 保存渠道配置...");
            var allFailures = new List<string>();

            // Feishu
            var feishu = new ChannelConfig { Name = "feishu", Enabled = ChkFeishuEnabled.IsChecked == true };
            if (!string.IsNullOrEmpty(TxtFeishuAppId.Text))
                feishu.Properties["appId"] = TxtFeishuAppId.Text.Trim();
            if (!string.IsNullOrEmpty(PwdFeishuAppSecret.Password))
                feishu.Properties["appSecret"] = PwdFeishuAppSecret.Password;
            var connMode = (CboFeishuConnectionMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "websocket";
            feishu.Properties["connectionMode"] = connMode;
            if (connMode == "webhook" && !string.IsNullOrEmpty(PwdFeishuVerifyToken.Password))
                feishu.Properties["verifyToken"] = PwdFeishuVerifyToken.Password;
            allFailures.AddRange(await _configService.SaveChannelAsync(feishu));

            // DingTalk
            var dingtalk = new ChannelConfig { Name = "dingtalk-connector", Enabled = ChkDingtalkEnabled.IsChecked == true };
            if (!string.IsNullOrEmpty(TxtDingtalkClientId.Text))
                dingtalk.Properties["clientId"] = TxtDingtalkClientId.Text.Trim();
            if (!string.IsNullOrEmpty(PwdDingtalkClientSecret.Password))
                dingtalk.Properties["clientSecret"] = PwdDingtalkClientSecret.Password;
            var dtDmPolicyVal = (CboDingtalkDmPolicy.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "open";
            dingtalk.Properties["dmPolicy"] = dtDmPolicyVal;
            var dtGroupPolicyVal = (CboDingtalkGroupPolicy.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "open";
            dingtalk.Properties["groupPolicy"] = dtGroupPolicyVal;
            allFailures.AddRange(await _configService.SaveChannelAsync(dingtalk));

            if (allFailures.Count == 0)
            {
                OnGatewayLogReceived("[设置] 渠道配置保存成功");
                // Gateway 可能因配置变更而退出，自动重启
                if (_lastStatus == GatewayStatus.Running || await _gateway.IsRunningAsync())
                {
                    TxtChannelSaveStatus.Foreground = (SolidColorBrush)FindResource("SuccessBrush");
                    TxtChannelSaveStatus.Text = "保存成功，正在重启 Gateway...";
                    await _gateway.RestartAsync();
                }
                else
                {
                    TxtChannelSaveStatus.Foreground = (SolidColorBrush)FindResource("SuccessBrush");
                    TxtChannelSaveStatus.Text = "保存成功";
                }
            }
            else
            {
                OnGatewayLogReceived($"[设置] 渠道配置保存失败: {string.Join(", ", allFailures)}");
                TxtChannelSaveStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
                TxtChannelSaveStatus.Text = $"部分保存失败: {string.Join(", ", allFailures)}";
            }
        }
        catch (Exception ex)
        {
            OnGatewayLogReceived($"[设置] 渠道配置保存异常: {ex.Message}");
            TxtChannelSaveStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
            TxtChannelSaveStatus.Text = $"保存失败: {ex.Message}";
        }
        finally
        {
            BtnSaveChannels.IsEnabled = true;
            BtnSaveModels.IsEnabled = true;
            _isSaving = false;
            RebuildCustomProviderList();
        }
    }
}
