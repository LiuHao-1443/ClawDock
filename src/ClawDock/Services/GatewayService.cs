using System.Diagnostics;

using System.Text;
using System.Text.Json;

namespace ClawDock.Services;

public enum GatewayStatus { Stopped, Starting, Running, Error }

public class GatewayService
{
    private const string GatewayUrl = "http://127.0.0.1:18789";
    private const int Port = 18789;

    private volatile bool _gatewayReady;
    private volatile bool _stopping;
    private volatile bool _starting;

    public event Action<GatewayStatus>? StatusChanged;
    /// <summary>Gateway 进程的 stdout/stderr 输出</summary>
    public event Action<string>? LogReceived;

    public string LastError { get; private set; } = string.Empty;

    /// <summary>带 auth token 的 Gateway URL，供 WebView2 使用</summary>
    public string DashboardUrl => string.IsNullOrEmpty(_authToken)
        ? GatewayUrl
        : $"{GatewayUrl}#token={_authToken}";

    private string _authToken = string.Empty;
    public string AuthToken => _authToken;

    // ── 启动 ─────────────────────────────────────────────────────────────

    private Process? _gatewayProcess;

    public void Start()
    {
        if (_gatewayReady || _starting) return;

        LastError = string.Empty;
        _gatewayReady = false;
        _stopping = false;
        _starting = true;
        StatusChanged?.Invoke(GatewayStatus.Starting);

        // 后台线程启动进程，避免阻塞 UI
        _ = Task.Run(async () =>
        {
            try
            {
                LogReceived?.Invoke("[START] 启动 Gateway 进程...");
                _gatewayProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "wsl",
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

                _gatewayProcess.Exited += (_, _) =>
                {
                    _starting = false;
                    if (_stopping) return;
                    if (!_gatewayReady)
                    {
                        LastError = $"Gateway 进程意外退出（退出码 {_gatewayProcess?.ExitCode}）";
                        LogReceived?.Invoke($"[START] {LastError}");
                        StatusChanged?.Invoke(GatewayStatus.Error);
                    }
                    else
                    {
                        LogReceived?.Invoke("[START] Gateway 进程已退出");
                        StatusChanged?.Invoke(GatewayStatus.Stopped);
                    }
                };

                _gatewayProcess.Start();
                LogReceived?.Invoke("[START] 进程已创建，读取输出...");

                // 读取 stdout/stderr，检测就绪
                _ = ReadStreamAsync(_gatewayProcess.StandardOutput);
                _ = ReadStreamAsync(_gatewayProcess.StandardError);

                // 超时保护
                await Task.Delay(60_000);
                if (!_gatewayReady && _gatewayProcess != null && !_gatewayProcess.HasExited)
                {
                    _starting = false;
                    LastError = "等待 Gateway 响应超时（60 秒）";
                    LogReceived?.Invoke($"[START] {LastError}");
                    StatusChanged?.Invoke(GatewayStatus.Error);
                }
            }
            catch (Exception ex)
            {
                _starting = false;
                LastError = $"启动失败: {ex.Message}";
                LogReceived?.Invoke($"[START] 异常: {ex.Message}");
                StatusChanged?.Invoke(GatewayStatus.Error);
            }
        });
    }

    private async Task ReadStreamAsync(System.IO.StreamReader reader)
    {
        try
        {
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line is not { Length: > 0 }) continue;

                var clean = WslService.CleanLine(line);
                if (clean.Length == 0) continue;

                // 检测 Gateway 就绪
                if (!_gatewayReady &&
                    (clean.Contains("listening on") || clean.Contains($":{Port}")))
                {
                    _gatewayReady = true;
                    _starting = false;
                    ReadAuthToken();
                    LogReceived?.Invoke($"[START] Gateway 就绪! token={(_authToken.Length > 0 ? _authToken[..8] + "..." : "(empty)")}");
                    StatusChanged?.Invoke(GatewayStatus.Running);
                }

                if (!WslService.IsGarbledWslMessage(clean))
                {
                    LastError = clean;
                    LogReceived?.Invoke(clean);
                }
            }
        }
        catch { /* 进程被杀时 stream 会抛异常，忽略 */ }
    }

    // ── 停止 ─────────────────────────────────────────────────────────────

    public Task StopAsync()
    {
        _stopping = true;
        _starting = false;
        _gatewayReady = false;
        LogReceived?.Invoke("[STOP] 开始停止 Gateway...");

        // 杀 Windows 侧的 wsl.exe 进程树
        try { _gatewayProcess?.Kill(entireProcessTree: true); } catch { }
        _gatewayProcess = null;
        LogReceived?.Invoke("[STOP] 已杀死进程树");

        StatusChanged?.Invoke(GatewayStatus.Stopped);

        // 后台清理 WSL 内残留进程
        _ = Task.Run(() =>
        {
            try
            {
                LogReceived?.Invoke("[STOP] 后台清理 WSL 残留进程...");
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "wsl",
                    Arguments = $"-d {WslService.DistroName} -- bash -c \"pkill -f openclaw || true\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                p?.WaitForExit(10_000);
                if (p is { HasExited: false }) try { p.Kill(true); } catch { }
                LogReceived?.Invoke("[STOP] WSL 清理完成");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[STOP] 清理异常: {ex.Message}");
            }
        });

        LogReceived?.Invoke("[STOP] 状态已切换为 Stopped");
        return Task.CompletedTask;
    }

    public async Task RestartAsync()
    {
        LogReceived?.Invoke("[RESTART] 开始重启...");
        StopAsync();
        LogReceived?.Invoke("[RESTART] 等待 2 秒...");
        await Task.Delay(2000);
        LogReceived?.Invoke("[RESTART] 重新启动 Gateway...");
        Start();
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
                await connectTask;
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
    {
        // 启动过程中保持 Starting，不让轮询干扰
        if (_starting) return GatewayStatus.Starting;
        // 主动停止后，不通过端口检测
        if (_stopping) return GatewayStatus.Stopped;
        return await IsRunningAsync() ? GatewayStatus.Running : GatewayStatus.Stopped;
    }

    /// <summary>从 WSL 内读取 auth token（同步，带超时保护）</summary>
    public void ReadAuthToken()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = $"-d {WslService.DistroName} --user root -- bash -c \"cat ~/.openclaw/openclaw.json\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            });
            if (p == null) { _authToken = string.Empty; return; }

            // 必须并行读 stdout/stderr 避免死锁
            string output = "";
            var outTask = Task.Run(() => output = p.StandardOutput.ReadToEnd());
            var errTask = Task.Run(() => p.StandardError.ReadToEnd());

            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(true); } catch { }
                _authToken = string.Empty;
                return;
            }
            outTask.Wait(5_000);
            errTask.Wait(5_000);

            // 从输出中提取 JSON（跳过 WSL 乱码前缀行）
            var jsonStart = output.IndexOf('{');
            if (jsonStart < 0) { _authToken = string.Empty; return; }

            using var json = JsonDocument.Parse(output[jsonStart..]);
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
