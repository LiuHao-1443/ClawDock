namespace ClawDock.Services;

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

            ("安装基础工具",
             $"{Env} apt-get install -y -q curl ca-certificates git xz-utils"),

            // 从淘宝 Node.js 镜像直接下载二进制包，避免依赖境外 NodeSource 源
            ("下载并安装 Node.js 22",
             "NODE_VER=$(curl -fsSL https://npmmirror.com/mirrors/node/latest-v22.x/SHASUMS256.txt | head -1 | sed 's/.*node-v//' | sed 's/-.*//') && " +
             "echo Installing Node.js v$NODE_VER && " +
             "curl -fsSL https://npmmirror.com/mirrors/node/v$NODE_VER/node-v$NODE_VER-linux-x64.tar.xz | tar -xJ --strip-components=1 -C /usr/local"),

            // 验证安装的 Node.js 版本确实为 v22.x
            ("验证 Node.js 版本",
             "/usr/local/bin/node --version | grep -q '^v22' || { echo 'ERROR: Node.js 22 未正确安装'; exit 1; }"),

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
    }

    private static string EscapeForBash(string cmd)
        => cmd.Replace("\"", "\\\"");
}
