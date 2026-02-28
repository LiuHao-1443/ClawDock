using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace ClawDock.Services;

public enum GatewayStatus { Stopped, Starting, Running, Error }

public class GatewayService
{
    private const string GatewayUrl = "http://localhost:18789";
    private const int Port = 18789;

    private Process? _gatewayProcess;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public event Action<GatewayStatus>? StatusChanged;
    /// <summary>Gateway 进程的 stdout/stderr 输出</summary>
    public event Action<string>? LogReceived;

    public string LastError { get; private set; } = string.Empty;

    // ── 启动 ─────────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        if (await IsRunningAsync()) return;

        LastError = string.Empty;
        StatusChanged?.Invoke(GatewayStatus.Starting);

        _gatewayProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "wsl",
                // 用 bash -l 登录 shell，确保 /usr/local/bin 在 PATH 中
                Arguments = $"-d Ubuntu -- bash -l -c \"openclaw gateway --port {Port}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            },
            EnableRaisingEvents = true
        };

        _gatewayProcess.Exited += (_, _) => StatusChanged?.Invoke(GatewayStatus.Stopped);
        _gatewayProcess.Start();

        // 异步读取 stdout/stderr，输出到 LogReceived 事件
        _ = ReadStreamAsync(_gatewayProcess.StandardOutput);
        _ = ReadStreamAsync(_gatewayProcess.StandardError);

        // 等待 Gateway 响应（最多 30 秒）
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(1000);
            if (await IsRunningAsync())
            {
                StatusChanged?.Invoke(GatewayStatus.Running);
                return;
            }

            // 如果进程已提前退出，立刻报错
            if (_gatewayProcess.HasExited)
            {
                LastError = $"Gateway 进程意外退出（退出码 {_gatewayProcess.ExitCode}）";
                StatusChanged?.Invoke(GatewayStatus.Error);
                return;
            }
        }

        LastError = $"等待 Gateway 响应超时（30 秒），请检查 openclaw 是否正确安装。";
        StatusChanged?.Invoke(GatewayStatus.Error);
    }

    private async Task ReadStreamAsync(System.IO.StreamReader reader)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line is { Length: > 0 })
            {
                LastError = line;          // 保留最后一行用于错误提示
                LogReceived?.Invoke(line);
            }
        }
    }

    // ── 停止 ─────────────────────────────────────────────────────────────

    public async Task StopAsync()
    {
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
