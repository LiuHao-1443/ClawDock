<p align="center">
  <img src="assets/banner.png" width="200" alt="ClawDock" />
</p>

<h1 align="center">ClawDock</h1>

<p align="center">OpenClaw 的 Windows 一键安装器 + 启动器</p>

[OpenClaw](https://github.com/openclaw/openclaw) 是一款运行在自己设备上的个人 AI 助手平台，支持接入 WhatsApp、Telegram、Slack、Discord 等主流消息平台。官方暂无 Windows 原生安装方案，本项目提供：

- **一键安装**：自动安装 WSL2、Ubuntu、Node.js 和 OpenClaw，无需任何命令行操作
- **内置浏览器**：安装完成后直接在 App 内使用 OpenClaw Web UI
- **Gateway 管理**：一键启动/停止/重启 OpenClaw Gateway
- **系统托盘**：最小化后台运行，随时唤起
- **完整卸载**：支持一键卸载并可选移除 Ubuntu 环境

## 系统要求

- Windows 10 Build 19041+ 或 Windows 11
- Microsoft Edge（WebView2，现代 Windows 自带）
- 处理器需支持虚拟化（VT-x / AMD-V，绝大多数电脑默认开启）

## 安装流程

安装向导会自动完成以下步骤：

1. **系统检测** — 检查 Windows 版本、WSL2 状态
2. **安装 WSL2** — 启用 WSL2 功能，从内置镜像导入 Ubuntu 22.04（无需联网下载）
3. **安装 OpenClaw** — 在 WSL2 内安装 Node.js 22 + OpenClaw（使用国内镜像加速）
4. **完成** — 进入主界面

## 本地构建

### 依赖

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php)（可选，用于打包 Setup.exe）

### 构建步骤

```powershell
# 克隆项目
git clone https://github.com/LiuHao-1443/ClawDock.git
cd ClawDock

# 一键构建（自动下载 Ubuntu rootfs + 编译 + 打包）
.\build.ps1

# 仅编译，不打包 Inno Setup
.\build.ps1 -SkipInno
```

> `ubuntu-base.tar.gz` 不在 git 仓库中，`build.ps1` 会自动从 USTC 镜像下载。

构建产物：
- `src/ClawDock/bin/publish/ClawDock.exe` — 独立可执行文件（约 184MB，无需安装 .NET）
- `dist/ClawDockSetup.exe` — Inno Setup 打包的安装程序

## 项目结构

```
ClawDock/
├── src/ClawDock/             # C# WPF 主项目
│   ├── Views/                # 安装向导 + 主窗口
│   ├── Services/             # WSL2、Gateway、安装、卸载服务
│   └── Assets/               # 图标等资源
├── installer/setup.iss       # Inno Setup 打包脚本
├── assets/                   # README 资源（banner 等）
└── build.ps1                 # 一键构建脚本
```

## 技术栈

- **C# 12 + .NET 8 + WPF** — 原生 Windows UI
- **Microsoft.Web.WebView2** — 内置浏览器（Edge 内核）
- **WSL2 + Ubuntu 22.04** — OpenClaw 运行环境
- **Inno Setup 6** — 安装包打包

## License

[MIT](LICENSE)

---

> 本项目与 OpenClaw 官方无关，是社区贡献的 Windows 安装方案。
