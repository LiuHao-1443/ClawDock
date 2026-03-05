using System.Net.Http;
using System.Text.Json;

namespace ClawDock.Services;

public class OpenClawService
{
    private readonly WslService _wsl = new();
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Node.js 安装脚本（会 base64 编码后传入 WSL，避免引号/转义问题）
    private const string NodeInstallScript =
        "#!/bin/bash\n" +
        "set -e\n" +
        "NODE_VER=$(curl -fsSL https://npmmirror.com/mirrors/node/latest-v22.x/SHASUMS256.txt" +
        " | head -1 | sed 's/.*node-v//' | sed 's/-.*//')\n" +
        "if [ -z \"$NODE_VER\" ]; then echo 'ERROR: Failed to determine Node.js version'; exit 1; fi\n" +
        "echo \"Installing Node.js v$NODE_VER...\"\n" +
        "curl -fsSL \"https://npmmirror.com/mirrors/node/v$NODE_VER/node-v$NODE_VER-linux-x64.tar.xz\"" +
        " | tar -xJ --strip-components=1 -C /usr/local\n";

    /// <summary>
    /// 在 WSL2 Ubuntu 内安装 Node.js 22 + OpenClaw
    /// </summary>
    public async Task InstallAsync(Action<string> onLog, Action<InstallPhase>? onPhase = null, CancellationToken ct = default)
    {
        onPhase?.Invoke(InstallPhase.OpenClawInstalling);

        // LANG=C 强制 apt 输出英文，避免中文编码问题；DEBIAN_FRONTEND 禁止交互提示
        const string Env = "LANG=C DEBIAN_FRONTEND=noninteractive";

        // 将安装脚本 base64 编码，避免 $() 和单引号经过 Windows→wsl→bash 链路时被破坏
        var scriptB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(NodeInstallScript));

        var steps = new (string Label, string Command)[]
        {
            ("更新软件包列表",
             $"{Env} apt-get update -q"),

            ("安装基础工具",
             $"{Env} apt-get install -y -q curl ca-certificates git xz-utils"),

            // 从淘宝 Node.js 镜像直接下载二进制包，避免依赖境外 NodeSource 源
            // 脚本通过 base64 传入，绕开 wsl.exe 的引号/转义问题
            ("下载并安装 Node.js 22",
             $"echo {scriptB64} | base64 -d | bash"),

            // 验证安装的 Node.js 版本确实为 v22.x
            ("验证 Node.js 版本",
             "/usr/local/bin/node --version"),

            // 配置 npm 使用淘宝镜像加速 openclaw 下载
            ("配置 npm 镜像",
             "/usr/local/bin/npm config set registry https://registry.npmmirror.com"),

            ("全局安装 OpenClaw",
             "/usr/local/bin/npm install -g openclaw@latest"),
        };

        // 先验证 Ubuntu 可用
        var testCode = await WslService.RunCommandStreamAsync(
            "wsl", $"-d {WslService.DistroName} --user root -- echo ok",
            _ => { }, ct);
        if (testCode != 0)
            throw new InvalidOperationException(
                "无法连接 ClawDock WSL 发行版，请确认 WSL2 已正确安装。");

        foreach (var (label, cmd) in steps)
        {
            ct.ThrowIfCancellationRequested();
            onLog($"▶ {label}...");

            var exitCode = await WslService.RunCommandStreamAsync(
                "wsl",
                $"-d {WslService.DistroName} --user root -- bash -c \"{EscapeForBash(cmd)}\"",
                line => onLog("  " + line),
                ct);

            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"「{label}」失败（退出码 {exitCode}）。请检查网络连接后重试。");

            onLog($"  ✓ {label} 完成");
            onLog("");
        }

        onLog("✓ OpenClaw 安装完成");
        onLog("");

        // 自动安装钉钉插件（非致命，失败不阻断）
        try
        {
            onLog("▶ 安装钉钉渠道插件...");
            var configService = new OpenClawConfigService();
            var pluginOk = await configService.InstallDingTalkPluginAsync(line => onLog("  " + line));
            if (pluginOk)
                onLog("  ✓ 钉钉插件安装完成");
            else
                onLog("  ⚠ 钉钉插件安装失败，可稍后在设置中手动安装");
        }
        catch (Exception ex)
        {
            onLog($"  ⚠ 钉钉插件安装失败: {ex.Message}，可稍后在设置中手动安装");
        }
    }

    /// <summary>
    /// 从 npm 镜像获取 OpenClaw 最新版本号
    /// </summary>
    public async Task<string?> GetLatestVersionAsync()
    {
        try
        {
            var json = await s_http.GetStringAsync("https://registry.npmmirror.com/openclaw/latest");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var ver))
                return ver.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 在 WSL 中执行 npm install -g openclaw@latest 更新 OpenClaw
    /// </summary>
    public async Task<bool> UpdateAsync(Action<string> onLog, CancellationToken ct = default)
    {
        onLog("▶ 更新 OpenClaw...");
        var exitCode = await WslService.RunCommandStreamAsync(
            "wsl",
            $"-d {WslService.DistroName} --user root -- bash -c \"{EscapeForBash("/usr/local/bin/npm install -g openclaw@latest")}\"",
            line => onLog("  " + line),
            ct);

        if (exitCode != 0)
        {
            onLog($"  ✗ 更新失败（退出码 {exitCode}）");
            return false;
        }

        onLog("  ✓ OpenClaw 更新完成");
        return true;
    }

    private static string EscapeForBash(string cmd)
        => cmd.Replace("\"", "\\\"");
}
