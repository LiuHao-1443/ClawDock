namespace OpenClawApp.Services;

public class OpenClawService
{
    private readonly WslService _wsl = new();

    /// <summary>
    /// 在 WSL2 Ubuntu 内安装 Node.js 22 + OpenClaw
    /// </summary>
    public async Task InstallAsync(Action<string> onLog, CancellationToken ct = default)
    {
        // LANG=C 强制 apt 输出英文，避免中文编码问题；DEBIAN_FRONTEND 禁止交互提示
        const string Env = "LANG=C DEBIAN_FRONTEND=noninteractive";

        var steps = new (string Label, string Command)[]
        {
            ("更新软件包列表",
             $"{Env} apt-get update -q"),

            ("安装 curl 和基础工具",
             $"{Env} apt-get install -y -q curl ca-certificates"),

            // 使用阿里云镜像加速 Node.js 安装脚本
            ("添加 Node.js 22 源",
             $"LANG=C curl -fsSL https://mirrors.aliyun.com/nodesource/deb/setup_22.x | LANG=C bash -"),

            ("安装 Node.js 22",
             $"{Env} apt-get install -y -q nodejs"),

            // 配置 npm 使用淘宝镜像加速 openclaw 下载
            ("配置 npm 镜像",
             "npm config set registry https://registry.npmmirror.com"),

            ("全局安装 OpenClaw",
             "npm install -g openclaw@latest"),
        };

        foreach (var (label, cmd) in steps)
        {
            ct.ThrowIfCancellationRequested();
            onLog($"▶ {label}...");

            await WslService.RunCommandStreamAsync(
                "wsl",
                $"-d Ubuntu --user root -- bash -c \"{EscapeForBash(cmd)}\"",
                line => onLog("  " + line),
                ct);

            onLog($"  ✓ {label} 完成");
            onLog("");
        }

        onLog("✓ OpenClaw 安装完成");
    }

    private static string EscapeForBash(string cmd)
        => cmd.Replace("\"", "\\\"");
}
