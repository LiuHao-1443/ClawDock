using System.Diagnostics;

using System.Text;
using System.Text.Json;

namespace ClawDock.Services;

public enum GatewayStatus { Stopped, Starting, Running, Error }

public class GatewayService
{
    private const string GatewayUrl = "http://localhost:18789";
    private const int Port = 18789;

    private Process? _gatewayProcess;

    public event Action<GatewayStatus>? StatusChanged;
    /// <summary>Gateway 进程的 stdout/stderr 输出</summary>
    public event Action<string>? LogReceived;

    public string LastError { get; private set; } = string.Empty;

    /// <summary>带 auth token 的 Gateway URL，供 WebView2 使用</summary>
    public string DashboardUrl => string.IsNullOrEmpty(_authToken)
        ? GatewayUrl
        : $"{GatewayUrl}?token={_authToken}";

    private string _authToken = string.Empty;

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
                // --allow-unconfigured 允许未配置时启动（首次安装后无需额外 setup）
                Arguments = $"-d {WslService.DistroName} -- bash -l -c \"openclaw gateway --port {Port} --allow-unconfigured\"",
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
                // Gateway 已启动，此时再读取 auth token（Gateway 启动时会重新生成 token）
                await ReadAuthTokenAsync();
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
                var clean = WslService.CleanLine(line);
                if (clean.Length > 0 && !WslService.IsGarbledWslMessage(clean))
                {
                    LastError = clean;
                    LogReceived?.Invoke(clean);
                }
            }
        }
    }

    // ── 停止 ─────────────────────────────────────────────────────────────

    public async Task StopAsync()
    {
        await WslService.RunCommandStreamAsync(
            "wsl",
            $"-d {WslService.DistroName} -- bash -c \"pkill -f 'openclaw gateway' || true\"",
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
            using var tcp = new System.Net.Sockets.TcpClient();
            var connectTask = tcp.ConnectAsync("127.0.0.1", Port);
            var timeoutTask = Task.Delay(2000);
            if (await Task.WhenAny(connectTask, timeoutTask) == connectTask)
            {
                await connectTask; // 抛出连接异常（如果有）
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<GatewayStatus> GetStatusAsync()
        => await IsRunningAsync() ? GatewayStatus.Running : GatewayStatus.Stopped;

    /// <summary>从 WSL 内的 openclaw 配置文件读取 auth token</summary>
    private async Task ReadAuthTokenAsync()
    {
        try
        {
            var output = new StringBuilder();
            await WslService.RunCommandStreamAsync("wsl",
                $"-d {WslService.DistroName} --user root -- cat /root/.openclaw/openclaw.json",
                line => output.AppendLine(line));
            var json = JsonDocument.Parse(output.ToString());
            _authToken = json.RootElement
                .GetProperty("gateway")
                .GetProperty("auth")
                .GetProperty("token")
                .GetString() ?? string.Empty;
        }
        catch
        {
            _authToken = string.Empty;
        }
    }
}
