using System.Diagnostics;
using System.Net.Http;

namespace OpenClawApp.Services;

public enum GatewayStatus { Stopped, Starting, Running, Error }

public class GatewayService
{
    private const string GatewayUrl = "http://localhost:18789";
    private const int Port = 18789;

    private Process? _gatewayProcess;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public event Action<GatewayStatus>? StatusChanged;

    // ── 启动 ─────────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        if (await IsRunningAsync()) return;

        StatusChanged?.Invoke(GatewayStatus.Starting);

        _gatewayProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = $"-d Ubuntu -- bash -c \"openclaw gateway --port {Port}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        _gatewayProcess.Exited += (_, _) => StatusChanged?.Invoke(GatewayStatus.Stopped);
        _gatewayProcess.Start();

        // 等待 Gateway 响应（最多 30 秒）
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(1000);
            if (await IsRunningAsync())
            {
                StatusChanged?.Invoke(GatewayStatus.Running);
                return;
            }
        }

        StatusChanged?.Invoke(GatewayStatus.Error);
    }

    // ── 停止 ─────────────────────────────────────────────────────────────

    public async Task StopAsync()
    {
        // 停止通过 WSL 启动的 openclaw 进程
        await WslService.RunCommandStreamAsync(
            "wsl",
            "-d Ubuntu -- bash -c \"pkill -f 'openclaw gateway' || true\"",
            _ => { });

        _gatewayProcess?.Kill(entireProcessTree: true);
        _gatewayProcess = null;

        StatusChanged?.Invoke(GatewayStatus.Stopped);
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(1000);
        await StartAsync();
    }

    // ── 状态检测 ─────────────────────────────────────────────────────────

    public async Task<bool> IsRunningAsync()
    {
        try
        {
            var response = await _http.GetAsync(GatewayUrl);
            return response.IsSuccessStatusCode || (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    public async Task<GatewayStatus> GetStatusAsync()
        => await IsRunningAsync() ? GatewayStatus.Running : GatewayStatus.Stopped;
}
