using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ClawDock.Services;

public record WslCheckResult(
    bool WindowsVersionOk,
    bool VirtualizationEnabled,
    bool Wsl2Installed,
    bool UbuntuInstalled,
    string WindowsBuild
);

public class WslService
{
    private const int MinBuildNumber = 19041; // Windows 10 2004

    // ── 检测 ─────────────────────────────────────────────────────────────

    public WslCheckResult Check()
    {
        var build = GetWindowsBuild();
        var buildOk = int.TryParse(build, out var buildNum) && buildNum >= MinBuildNumber;

        return new WslCheckResult(
            WindowsVersionOk: buildOk,
            VirtualizationEnabled: IsVirtualizationEnabled(),
            Wsl2Installed: IsWsl2Installed(),
            UbuntuInstalled: IsUbuntuInstalled(),
            WindowsBuild: build
        );
    }

    private static string GetWindowsBuild()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("CurrentBuildNumber")?.ToString() ?? "0";
        }
        catch
        {
            return "0";
        }
    }

    private static bool IsVirtualizationEnabled()
    {
        // WSL2 能运行就证明虚拟化已开启，直接返回 true
        if (IsWsl2Installed()) return true;

        try
        {
            var result = RunCommand("powershell",
                "-NoProfile -Command \"(Get-WmiObject Win32_Processor).VirtualizationFirmwareEnabled\"");
            // 检测不确定时乐观估计（虚拟机环境下 WMI 可能返回 False 但实际可用）
            var val = result.Output.Trim();
            return !val.Equals("False", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsWsl2Installed()
    {
        try
        {
            var result = RunCommand("wsl", "--status");
            // wsl --status 成功返回说明 WSL 已安装
            return result.ExitCode == 0 || result.Output.Contains("WSL");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUbuntuInstalled()
    {
        try
        {
            var result = RunCommand("wsl", "--list --quiet");
            return result.Output.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ── 安装 ─────────────────────────────────────────────────────────────

    // Ubuntu 安装目录
    private static readonly string UbuntuInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClawDock", "Ubuntu");

    /// <summary>
    /// 安装 WSL2 + Ubuntu（使用内嵌 rootfs，无需联网下载）
    /// 返回 true = 安装完成；false = 需要重启
    /// </summary>
    public async Task<bool> InstallAsync(Action<string> onLog, CancellationToken ct = default)
    {
        onLog("正在检查系统状态...");

        if (IsWsl2Installed() && IsUbuntuInstalled())
        {
            onLog("✓ WSL2 + Ubuntu 已安装，跳过此步骤");
            return true;
        }

        // Step 1: 确保 WSL2 内核已启用（不下载发行版，仅启用功能）
        if (!IsWsl2Installed())
        {
            onLog("▶ 启用 WSL2 功能...");
            bool rebootRequired = false;

            await RunCommandStreamAsync("wsl", "--install --no-distribution",
                line =>
                {
                    onLog(line);
                    if (line.Contains("restart", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("reboot",  StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("重启") || line.Contains("重新启动"))
                    {
                        rebootRequired = true;
                    }
                }, ct);

            if (rebootRequired)
            {
                onLog("");
                onLog("⚠ WSL2 功能已启用，系统需要重启才能继续");
                return false;
            }

            await Task.Delay(2000, ct);
        }

        // Step 2: 从内嵌资源导入 Ubuntu（本地操作，极快）
        if (!IsUbuntuInstalled())
        {
            onLog("");
            onLog("▶ 正在从内置镜像导入 Ubuntu 22.04...");
            onLog("  （本地解压，无需下载）");

            var tarball = await ExtractEmbeddedRootfsAsync(onLog, ct);
            try
            {
                Directory.CreateDirectory(UbuntuInstallDir);

                await RunCommandStreamAsync("wsl",
                    $"--import Ubuntu \"{UbuntuInstallDir}\" \"{tarball}\" --version 2",
                    line => onLog("  " + line), ct);
            }
            finally
            {
                // 清理临时文件
                if (File.Exists(tarball))
                    File.Delete(tarball);
            }

            onLog("  ✓ Ubuntu 导入完成");
        }

        // Step 3: 配置国内 apt 镜像（加速后续 apt install）
        onLog("");
        onLog("▶ 配置 apt 国内镜像（USTC）...");
        await RunCommandStreamAsync("wsl",
            "-d Ubuntu --user root -- bash -c \"" +
            "LANG=C sed -i 's|http://archive.ubuntu.com|https://mirrors.ustc.edu.cn|g' /etc/apt/sources.list && " +
            "LANG=C sed -i 's|http://security.ubuntu.com|https://mirrors.ustc.edu.cn|g' /etc/apt/sources.list" +
            "\"",
            line => onLog("  " + line), ct);
        onLog("  ✓ 镜像配置完成");

        onLog("");
        onLog("✓ WSL2 + Ubuntu 安装完成");
        return true;
    }

    /// <summary>
    /// 从 exe 内嵌资源中提取 ubuntu-base.tar.gz 到临时文件，返回临时文件路径
    /// </summary>
    private static async Task<string> ExtractEmbeddedRootfsAsync(
        Action<string> onLog, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "openclaw-ubuntu-base.tar.gz");
        onLog($"  正在解压内置镜像到临时目录...");

        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        // 资源名：命名空间.文件路径（点分隔）
        var resourceName = "ClawDock.Assets.ubuntu-base.tar.gz";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到内嵌资源: {resourceName}");

        var total = stream.Length;
        using var file = File.Create(tempPath);
        var buf = new byte[81920];
        long written = 0;
        int read;

        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            await file.WriteAsync(buf.AsMemory(0, read), ct);
            written += read;
            var pct = written * 100 / total;
            onLog($"  解压中... {written / 1024 / 1024:0.0} MB / {total / 1024 / 1024:0.0} MB ({pct}%)");
        }

        onLog($"  ✓ 解压完成");
        return tempPath;
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    public static (int ExitCode, string Output) RunCommand(string exe, string args)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        p.Start();
        var raw = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, CleanLine(raw));
    }

    public static async Task RunCommandStreamAsync(
        string exe, string args,
        Action<string> onLine,
        CancellationToken ct = default)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        p.Start();

        var readOut = Task.Run(async () =>
        {
            while (!p.StandardOutput.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await p.StandardOutput.ReadLineAsync(ct);
                if (line != null)
                {
                    var clean = CleanLine(line);
                    if (clean.Length > 0) onLine(clean);
                }
            }
        }, ct);

        var readErr = Task.Run(async () =>
        {
            while (!p.StandardError.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await p.StandardError.ReadLineAsync(ct);
                if (line != null)
                {
                    var clean = CleanLine(line);
                    if (clean.Length > 0) onLine(clean);
                }
            }
        }, ct);

        await Task.WhenAll(readOut, readErr);
        await p.WaitForExitAsync(ct);
    }

    /// <summary>
    /// 清理 WSL 输出中的乱码/噪音：
    ///   1. 去除 ANSI 转义序列（颜色/光标控制码）
    ///   2. 取最后一段（\r 分隔的进度行只保留末尾完整行）
    ///   3. 去除空字节及不可打印控制字符
    ///   4. 折叠多余空格
    /// </summary>
    private static string CleanLine(string line)
    {
        // 去除 ANSI 转义序列，如 \x1B[32m \x1B[0K 等
        line = Regex.Replace(line, @"\x1B\[[0-9;]*[A-Za-z]", "");
        // apt 用 \r 覆写进度行，只保留最后一段
        var crIdx = line.LastIndexOf('\r');
        if (crIdx >= 0) line = line[(crIdx + 1)..];
        // 去除空字节及其他不可打印控制字符（保留 Tab）
        line = Regex.Replace(line, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");
        // 折叠多余空格
        line = Regex.Replace(line, @" {2,}", " ");
        return line.Trim();
    }
}
