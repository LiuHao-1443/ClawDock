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

        var url = _gateway.DashboardUrl;

        // token 已就绪 → 直接导航
        if (url.Contains("token="))
        {
            NavigateIfNeeded(url);
            return;
        }

        // token 未就绪 → 后台读取，读完再导航（避免阻塞 UI 线程）
        _ = Task.Run(async () =>
        {
            _gateway.ReadAuthToken();
            Dispatcher.Invoke(() => NavigateIfNeeded(_gateway.DashboardUrl));
            // 每次 Gateway 启动后同步 token 到钉钉渠道配置
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
            _ = LoadOpenClawVersionAsync();
            _ = LoadModelNameAsync();
            _ = EnsureDingtalkPluginInstalledAsync();
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
                _ = LoadOpenClawVersionAsync();
                _ = LoadModelNameAsync();
                _ = EnsureDingtalkPluginInstalledAsync();
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
                Dispatcher.Invoke(() => VersionText.Text = $"OpenClaw v{version}");
        }
        catch { }
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
        => _gateway.StopAsync();

    private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        => await _gateway.RestartAsync();

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
    private string _activeSettingsTab = "models";
    private Dictionary<string, List<string>> _modelsByProvider = new();
    private Dictionary<string, string> _providerApiKeys = new();

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
            _ = LoadSettingsAsync();
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
            var configTask = _configService.ReadFullConfigAsync();
            var modelsTask = _configService.ListAvailableModelsAsync();
            await Task.WhenAll(configTask, modelsTask);
            var config = configTask.Result;
            var availableModels = modelsTask.Result;

            Dispatcher.Invoke(() =>
            {
                // Group models by provider
                _modelsByProvider.Clear();
                foreach (var m in availableModels)
                {
                    var slash = m.IndexOf('/');
                    if (slash <= 0) continue;
                    var provider = m[..slash];
                    if (!_modelsByProvider.ContainsKey(provider))
                        _modelsByProvider[provider] = new List<string>();
                    _modelsByProvider[provider].Add(m[(slash + 1)..]);
                }

                // Cache provider API keys from config
                _providerApiKeys.Clear();
                foreach (var p in config.Providers)
                {
                    if (!string.IsNullOrEmpty(p.ApiKey))
                        _providerApiKeys[p.Name] = p.ApiKey;
                }

                // Populate provider dropdown
                CboProvider.SelectionChanged -= CboProvider_SelectionChanged;
                CboProvider.Items.Clear();
                foreach (var p in _modelsByProvider.Keys.OrderBy(p => p))
                    CboProvider.Items.Add(p);

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

                // Provider (load first one if exists)
                if (config.Providers.Count > 0)
                {
                    var p = config.Providers[0];
                    TxtProviderName.Text = p.Name;
                    TxtProviderBaseUrl.Text = p.BaseUrl;
                    PwdProviderApiKey.Password = p.ApiKey;
                    TxtProviderApiKeyVisible.Text = p.ApiKey;

                    // Select API type
                    foreach (ComboBoxItem item in CboProviderApi.Items)
                    {
                        if (item.Content?.ToString() == p.Api)
                        {
                            CboProviderApi.SelectedItem = item;
                            break;
                        }
                    }

                    // Provider models
                    ProviderModelsList.Children.Clear();
                    foreach (var m in p.Models)
                        AddProviderModelRow(m.Name, m.Id);
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
                _ = CheckDingtalkPluginStatusAsync();
            });
        }
        catch (Exception ex)
        {
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

    private void AddProviderModelRow(string name = "", string id = "")
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tbName = new WpfTextBox
        {
            Text = name,
            Style = (Style)FindResource("DarkTextBox"),
            Tag = "name"
        };
        tbName.SetValue(System.Windows.Controls.Primitives.TextBoxBase.TabIndexProperty, 0);
        Grid.SetColumn(tbName, 0);
        row.Children.Add(tbName);

        var tbId = new WpfTextBox
        {
            Text = id,
            Style = (Style)FindResource("DarkTextBox"),
            Margin = new Thickness(6, 0, 0, 0),
            Tag = "id"
        };
        Grid.SetColumn(tbId, 1);
        row.Children.Add(tbId);

        var btn = new WpfButton
        {
            Content = "✕",
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 12
        };
        btn.Click += (_, _) => ProviderModelsList.Children.Remove(row);
        Grid.SetColumn(btn, 2);
        row.Children.Add(btn);

        ProviderModelsList.Children.Add(row);
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

    // ── 保存模型配置 ────────────────────────────────────────────────────────

    private async void BtnSaveModels_Click(object sender, RoutedEventArgs e)
    {
        BtnSaveModels.IsEnabled = false;
        TxtModelSaveStatus.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
        TxtModelSaveStatus.Text = "保存中...";

        try
        {
            var selectedProvider = CboProvider.SelectedItem?.ToString() ?? "";
            var modelName = CboPrimaryModel.Text?.Trim() ?? "";
            var fullModel = !string.IsNullOrEmpty(selectedProvider) && !string.IsNullOrEmpty(modelName) && !modelName.Contains("/")
                ? $"{selectedProvider}/{modelName}" : modelName;

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

            var failures = await _configService.SaveModelConfigAsync(modelConfig);

            // Save API key for selected provider
            var providerKeyValue = _providerKeyVisible
                ? TxtSelectedProviderKey.Text?.Trim() ?? ""
                : PwdSelectedProviderKey.Password ?? "";
            if (!string.IsNullOrEmpty(selectedProvider) && !string.IsNullOrEmpty(providerKeyValue))
            {
                try
                {
                    if (!await _configService.SaveProviderApiKeyAsync(selectedProvider, providerKeyValue))
                        failures.Add($"models.providers.{selectedProvider}.apiKey");
                    else
                        _providerApiKeys[selectedProvider] = providerKeyValue;
                }
                catch (Exception ex2)
                {
                    failures.Add($"[EX] {ex2.Message}");
                }
            }

            // Save custom provider if name is specified
            var providerName = TxtProviderName.Text?.Trim();
            if (!string.IsNullOrEmpty(providerName))
            {
                var provider = new ProviderConfig
                {
                    Name = providerName,
                    BaseUrl = TxtProviderBaseUrl.Text?.Trim() ?? "",
                    ApiKey = _apiKeyVisible
                        ? TxtProviderApiKeyVisible.Text?.Trim() ?? ""
                        : PwdProviderApiKey.Password ?? "",
                    Api = (CboProviderApi.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "openai"
                };

                // Collect provider models (each row has Name + ID textboxes)
                foreach (var child in ProviderModelsList.Children)
                {
                    if (child is Grid row)
                    {
                        string? mName = null, mId = null;
                        foreach (var c in row.Children)
                        {
                            if (c is WpfTextBox tb)
                            {
                                if (tb.Tag?.ToString() == "name") mName = tb.Text?.Trim();
                                else if (tb.Tag?.ToString() == "id") mId = tb.Text?.Trim();
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(mName))
                            provider.Models.Add(new ProviderModel { Name = mName!, Id = mId ?? mName! });
                    }
                }

                var pf = await _configService.SaveProviderAsync(provider);
                failures.AddRange(pf);
            }

            if (failures.Count == 0)
            {
                TxtModelSaveStatus.Foreground = (SolidColorBrush)FindResource("SuccessBrush");
                TxtModelSaveStatus.Text = "保存成功";
                ModelText.Text = string.IsNullOrEmpty(fullModel) ? "" : $"· {fullModel}";
            }
            else
            {
                TxtModelSaveStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
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
        }
    }

    // ── 保存渠道配置 ────────────────────────────────────────────────────────

    private async void BtnSaveChannels_Click(object sender, RoutedEventArgs e)
    {
        BtnSaveChannels.IsEnabled = false;
        TxtChannelSaveStatus.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
        TxtChannelSaveStatus.Text = "保存中...";

        try
        {
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

                // Gateway 重启后 token 会变，同步到钉钉渠道配置
                await _configService.SyncGatewayTokenToDingtalkAsync();
            }
            else
            {
                TxtChannelSaveStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
                TxtChannelSaveStatus.Text = $"部分保存失败: {string.Join(", ", allFailures)}";
            }
        }
        catch (Exception ex)
        {
            TxtChannelSaveStatus.Foreground = (SolidColorBrush)FindResource("ErrorBrush");
            TxtChannelSaveStatus.Text = $"保存失败: {ex.Message}";
        }
        finally
        {
            BtnSaveChannels.IsEnabled = true;
        }
    }
}
