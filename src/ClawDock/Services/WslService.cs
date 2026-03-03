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
    bool DistroInstalled,
    string WindowsBuild
);

public class WslService
{
    private const int MinBuildNumber = 19041; // Windows 10 2004
    public const string DistroName = "ClawDock";  // WSL 发行版名称，避免与用户自己的 Ubuntu 冲突

    // ── 检测 ─────────────────────────────────────────────────────────────

    public WslCheckResult Check()
    {
        var build = GetWindowsBuild();
        var buildOk = int.TryParse(build, out var buildNum) && buildNum >= MinBuildNumber;

        return new WslCheckResult(
            WindowsVersionOk: buildOk,
            VirtualizationEnabled: IsVirtualizationEnabled(),
            Wsl2Installed: IsWsl2Installed(),
            DistroInstalled: IsDistroInstalled(),
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
        try
        {
            var result = RunCommand("powershell",
                "-NoProfile -Command \"(Get-WmiObject Win32_Processor).VirtualizationFirmwareEnabled\"");
            var val = result.Output.Trim();
            return !val.Equals("False", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true; // 无法检测时默认放行，后续安装阶段会再次验证
        }
    }

    private static bool IsWsl2Installed()
    {
        try
        {
            var result = RunCommand("wsl", "--status", Encoding.Unicode);
            // wsl --status 成功返回说明 WSL 已安装
            return result.ExitCode == 0 || result.Output.Contains("WSL");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDistroInstalled()
    {
        try
        {
            // 通过注册表检测，避免 wsl.exe 重定向时输出丢失的问题
            using var lxss = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss == null) return false;

            foreach (var subName in lxss.GetSubKeyNames())
            {
                using var sub = lxss.OpenSubKey(subName);
                var name = sub?.GetValue("DistributionName")?.ToString();
                if (DistroName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ── 安装 ─────────────────────────────────────────────────────────────

    // WSL 发行版安装目录
    private static readonly string DistroInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClawDock", "WSL");

    /// <summary>
    /// 安装 WSL2 + Ubuntu（使用内嵌 rootfs，无需联网下载）
    /// 返回 true = 安装完成；false = 需要重启
    /// </summary>
    public async Task<bool> InstallAsync(Action<string> onLog, CancellationToken ct = default)
    {
        onLog("正在检查系统状态...");

        if (IsWsl2Installed() && IsDistroInstalled())
        {
            onLog("✓ WSL2 + ClawDock 发行版已安装，跳过此步骤");
            return true;
        }

        // Step 1: 确保 WSL2 内核 + Virtual Machine Platform 已启用
        // 始终运行（幂等），因为 wsl --status 可能成功但 VM Platform 未启用
        onLog("▶ 启用 WSL2 功能...");

        await RunCommandStreamAsync("wsl", "--install --no-distribution",
            line => onLog("  " + line), ct, Encoding.Unicode);

        // Step 2: 从内嵌资源导入 Ubuntu rootfs 为 ClawDock 发行版
        if (!IsDistroInstalled())
        {
            onLog("");
            onLog("▶ 正在从内置镜像导入 Ubuntu 22.04...");
            onLog("  （本地解压，无需下载）");

            var tarball = await ExtractEmbeddedRootfsAsync(onLog, ct);
            var importOutput = new StringBuilder();
            try
            {
                Directory.CreateDirectory(DistroInstallDir);
                var exitCode = await RunCommandStreamAsync("wsl",
                    $"--import {DistroName} \"{DistroInstallDir}\" \"{tarball}\" --version 2",
                    line => { importOutput.AppendLine(line); onLog("  " + line); }, ct,
                    Encoding.Unicode);

                if (exitCode != 0)
                    onLog($"  ⚠ wsl --import 退出码: {exitCode}");
            }
            finally
            {
                if (File.Exists(tarball))
                    File.Delete(tarball);
            }

            // import 失败 → 区分"需要重启"和"硬件不支持虚拟化"
            if (!IsDistroInstalled())
            {
                try { Directory.Delete(DistroInstallDir, true); } catch { }

                var output = importOutput.ToString();

                if (output.Contains("HCS_E_HYPERV", StringComparison.OrdinalIgnoreCase))
                {
                    onLog("");
                    onLog("✗ 此计算机不支持硬件虚拟化（Hyper-V），无法运行 WSL2");
                    throw new InvalidOperationException(
                        "此计算机不支持硬件虚拟化（Hyper-V），无法运行 WSL2。\n" +
                        "请在物理机或支持嵌套虚拟化的虚拟机上使用 ClawDock。");
                }

                onLog("");
                onLog("⚠ 导入失败，可能需要重启计算机以激活 WSL2 虚拟化组件");
                return false;  // 触发重启流程
            }

            onLog("  ✓ 导入完成");
        }
        else
        {
            onLog("  ✓ WSL2 功能已就绪");
        }

        // Step 3: 配置国内 apt 镜像（加速后续 apt install）
        onLog("");
        onLog("▶ 配置 apt 国内镜像（USTC）...");
        await RunCommandStreamAsync("wsl",
            $"-d {DistroName} --user root -- bash -c \"" +
            "LANG=C sed -i 's|http://archive.ubuntu.com|http://mirrors.ustc.edu.cn|g' /etc/apt/sources.list && " +
            "LANG=C sed -i 's|http://security.ubuntu.com|http://mirrors.ustc.edu.cn|g' /etc/apt/sources.list" +
            "\"",
            line => onLog("  " + line), ct);
        onLog("  ✓ 镜像配置完成");

        // Step 4: 写入 wsl.conf — 禁用 Windows PATH 互通，启用 systemd
        // 防止 WSL 把 Windows 的 node/npm 混入 PATH 导致版本冲突
        onLog("");
        onLog("▶ 配置 wsl.conf（禁用 Windows PATH 互通）...");
        await RunCommandStreamAsync("wsl",
            $"-d {DistroName} --user root -- bash -c \"" +
            "cat > /etc/wsl.conf << 'WSLCONF'\n" +
            "[interop]\n" +
            "appendWindowsPath = false\n" +
            "\n" +
            "[boot]\n" +
            "systemd = true\n" +
            "WSLCONF" +
            "\"",
            line => onLog("  " + line), ct);
        onLog("  ✓ wsl.conf 配置完成");

        // 重启发行版使 wsl.conf 生效
        onLog("  正在重启发行版以应用配置...");
        await RunCommandStreamAsync("wsl", $"--terminate {DistroName}",
            line => onLog("  " + line), ct, Encoding.Unicode);
        await Task.Delay(3000, ct);
        // 验证发行版可正常启动（启用 systemd 后冷启动较慢，最多重试 30 秒）
        bool verified = false;
        for (int i = 0; i < 15; i++)
        {
            ct.ThrowIfCancellationRequested();
            var verifyCode = await RunCommandStreamAsync("wsl",
                $"-d {DistroName} --user root -- echo ok",
                _ => { }, ct);
            if (verifyCode == 0) { verified = true; break; }
            onLog($"  等待发行版就绪... ({i + 1}/15)");
            await Task.Delay(2000, ct);
        }
        if (!verified)
            throw new InvalidOperationException(
                "重启发行版后无法连接（已等待 30 秒），请确认 WSL2 已正确安装，" +
                "并尝试手动运行: wsl -d ClawDock --user root -- echo ok");
        onLog("  ✓ 发行版重启完成");

        onLog("");
        onLog("✓ WSL2 + ClawDock 环境安装完成");
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

    public static (int ExitCode, string Output) RunCommand(string exe, string args,
        Encoding? encoding = null)
    {
        var enc = encoding ?? Encoding.UTF8;
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding  = enc,
        };
        p.Start();
        var raw = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, CleanLine(raw));
    }

    public static async Task<int> RunCommandStreamAsync(
        string exe, string args,
        Action<string> onLine,
        CancellationToken ct = default,
        Encoding? encoding = null)
    {
        var enc = encoding ?? Encoding.UTF8;
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding  = enc,
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
                    if (clean.Length > 0 && !IsGarbledWslMessage(clean))
                        onLine(clean);
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
                    if (clean.Length > 0 && !IsGarbledWslMessage(clean))
                        onLine(clean);
                }
            }
        }, ct);

        await Task.WhenAll(readOut, readErr);
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    /// <summary>
    /// 清理 WSL 输出中的乱码/噪音：
    ///   1. 去除 ANSI 转义序列（颜色/光标控制码）
    ///   2. 取最后一段（\r 分隔的进度行只保留末尾完整行）
    ///   3. 去除空字节及不可打印控制字符
    ///   4. 折叠多余空格
    /// </summary>
    internal static string CleanLine(string line)
    {
        // 去除 ANSI 转义序列，如 \x1B[32m \x1B[0K 等
        line = Regex.Replace(line, @"\x1B\[[0-9;]*[A-Za-z]", "");
        // 先将 Windows 换行符 \r\n 归一化为 \n，避免误触进度行逻辑
        line = line.Replace("\r\n", "\n");
        // apt 用 \r 覆写进度行，只保留最后一段
        var crIdx = line.LastIndexOf('\r');
        if (crIdx >= 0) line = line[(crIdx + 1)..];
        // 去除空字节及其他不可打印控制字符（保留 Tab）
        line = Regex.Replace(line, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", "");
        // 折叠多余空格
        line = Regex.Replace(line, @" {2,}", " ");
        return line.Trim();
    }

    /// <summary>
    /// 判断一行文本是否为 WSL 基础设施的乱码消息
    /// （wsl -d 执行 Linux 命令时，WSL 的网络/locale 等提示用 UTF-16LE 输出，
    ///  被 UTF-8 读取后变成含大量 Unicode 替换字符的乱码行）
    /// </summary>
    internal static bool IsGarbledWslMessage(string line)
    {
        if (line.Length < 4) return false;
        // 统计非 ASCII 且非 CJK 的字符数量
        // UTF-16LE 被当 UTF-8 读取后会产生 ◆(U+25C6)、替换字符(U+FFFD) 等
        // 阈值设为 3：少量 unicode 符号（emoji、箭头等）是合法日志内容，
        // 真正的 WSL 乱码会大量出现替换字符
        int garbled = 0;
        foreach (var c in line)
        {
            if (c > '\u007F'                         // 非 ASCII
                && !(c >= '\u4E00' && c <= '\u9FFF') // 排除 CJK 汉字（合法中文）
                && !(c >= '\u3000' && c <= '\u303F') // 排除 CJK 标点
                && !(c >= '\uFF00' && c <= '\uFFEF') // 排除全角字符
                && c != '\u2713' && c != '\u2717'    // 排除 ✓ ✗
                && !char.IsSurrogate(c))             // 排除 emoji 代理对
                garbled++;
        }
        return garbled >= 3;  // 3+ 乱码字符才判定为 WSL 乱码行
    }
}
