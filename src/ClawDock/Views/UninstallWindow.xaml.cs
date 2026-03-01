using System.Windows;
using ClawDock.Services;

namespace ClawDock.Views;

public partial class UninstallWindow : Window
{
    private readonly UninstallService _uninstaller;
    private readonly CancellationTokenSource _cts = new();
    private bool _done;

    public UninstallWindow(GatewayService gateway, InstallStateService stateService)
    {
        InitializeComponent();
        _uninstaller = new UninstallService(gateway, stateService);
    }

    private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        // 切换到执行模式
        BtnUninstall.IsEnabled = false;
        BtnCancel.IsEnabled    = false;
        BtnUninstall.Content   = "正在卸载...";
        LogPanel.Visibility    = Visibility.Visible;

        var progress = new Progress<string>(line =>
        {
            LogText.Text += line + "\n";
            LogScroll.ScrollToBottom();
        });

        try
        {
            await _uninstaller.UninstallAsync(
                removeUbuntu: ChkRemoveUbuntu.IsChecked == true,
                onLog: line => ((IProgress<string>)progress).Report(line),
                ct: _cts.Token);

            _done = true;
            BtnUninstall.Content = "✓ 卸载完成，正在退出...";
            BtnCancel.IsEnabled  = false;

            // 短暂显示完成信息后自动退出
            await Task.Delay(1500);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            ((IProgress<string>)progress).Report("\n卸载已取消。");
            BtnCancel.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ((IProgress<string>)progress).Report($"\n❌ 错误: {ex.Message}");
            BtnUninstall.Content = "重试";
            BtnUninstall.IsEnabled = true;
            BtnCancel.IsEnabled    = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_done)
            _cts.Cancel();

        Close();

        // 卸载完成后退出主程序，让用户重新运行进入安装流程
        if (_done)
            Application.Current.Shutdown();
    }
}
