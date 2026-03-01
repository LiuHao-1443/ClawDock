namespace ClawDock.Services;

public class OpenClawService
{
    private readonly WslService _wsl = new();

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
    public async Task InstallAsync(Action<string> onLog, CancellationToken ct = default)
    {
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
    }

    private static string EscapeForBash(string cmd)
        => cmd.Replace("\"", "\\\"");
}
