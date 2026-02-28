namespace ClawDock.Services;

public class UninstallService
{
    private readonly GatewayService _gateway;
    private readonly InstallStateService _stateService;

    public UninstallService(GatewayService gateway, InstallStateService stateService)
    {
        _gateway = gateway;
        _stateService = stateService;
    }

    /// <summary>
    /// 完整卸载：停止 Gateway → 卸载 ClawDock → 可选移除 Ubuntu → 清理本地状态
    /// </summary>
    public async Task UninstallAsync(
        bool removeUbuntu,
        Action<string> onLog,
        CancellationToken ct = default)
    {
        // 1. 停止 Gateway
        onLog("▶ 停止 ClawDock Gateway...");
        await _gateway.StopAsync();
        onLog("  ✓ Gateway 已停止");
        onLog("");

        // 2. 卸载 WSL2 内的 OpenClaw
        onLog("▶ 卸载 ClawDock (npm uninstall -g)...");
        await WslService.RunCommandStreamAsync(
            "wsl", "-d Ubuntu --user root -- bash -c \"npm uninstall -g openclaw 2>&1 || true\"",
            line => onLog("  " + line), ct);
        onLog("  ✓ OpenClaw 已从 WSL2 中卸载");
        onLog("");

        // 3. 可选：移除整个 Ubuntu 发行版
        if (removeUbuntu)
        {
            onLog("▶ 移除 Ubuntu WSL2 发行版...");
            await WslService.RunCommandStreamAsync(
                "wsl", "--unregister Ubuntu",
                line => onLog("  " + line), ct);
            onLog("  ✓ Ubuntu 已移除");
            onLog("");
        }

        // 4. 清理注册表（开机自启 + 续装标记）
        onLog("▶ 清理注册表...");
        RemoveRegistryEntries();
        onLog("  ✓ 注册表已清理");
        onLog("");

        // 5. 删除状态文件（让 App 下次重新触发安装流程）
        onLog("▶ 清除安装状态...");
        DeleteStateFile();
        onLog("  ✓ 安装状态已重置");
        onLog("");

        onLog("✓ 卸载完成！重新运行程序即可重新安装。");
    }

    private static void RemoveRegistryEntries()
    {
        using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        runKey?.DeleteValue("ClawDock",        throwOnMissingValue: false);
        runKey?.DeleteValue("OpenClawResume",  throwOnMissingValue: false);
    }

    private static void DeleteStateFile()
    {
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClawDock", "state.json");

        if (System.IO.File.Exists(path))
            System.IO.File.Delete(path);
    }
}
