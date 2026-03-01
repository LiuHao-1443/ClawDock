# ClawDock for Windows — 设计文档

## 1. 项目目标

为 OpenClaw（https://github.com/openclaw/openclaw）提供一个 Windows 一键安装 + 启动器方案：

- 用户双击 `ClawDockSetup.exe`，全自动完成所有依赖安装
- 安装完成后，桌面/开始菜单出现 `ClawDock.exe`
- 打开 `ClawDock.exe` 即可通过内置浏览器使用 OpenClaw

## 2. 技术选型

### 2.1 核心框架：C# + WPF + WebView2

| 组件 | 技术 | 说明 |
|------|------|------|
| 安装向导 + 主程序 | C# 12 + .NET 8 + WPF | 单个项目，兼顾安装和运行 |
| 内置浏览器 | Microsoft.Web.WebView2 | Edge 内核，Win10/11 预装 |
| 系统托盘 | WPF + NotifyIcon | 最小化到托盘，后台运行 |

**为什么选 C# + WPF：**
- .NET 8 可以「自包含发布」，无需目标机器预装运行时
- WebView2 随 Microsoft Edge 预装在所有 Win10 1803+ 和 Win11 上
- WPF 原生 Windows UI，速度快
- 直接调用 Windows API，管理 WSL2 最方便

### 2.2 打包工具：Inno Setup

- 将 `ClawDock.exe` 打包成 `ClawDockSetup.exe`
- 自动申请管理员权限（UAC）
- 处理安装路径、桌面快捷方式、开始菜单
- 支持卸载

## 3. 解决方案结构

```
ClawDock/
├── docs/
│   └── design.md                  # 本文档
├── src/
│   └── ClawDock/                  # 主 C# 项目
│       ├── ClawDock.csproj
│       ├── App.xaml / App.xaml.cs  # 启动入口：判断安装状态
│       ├── Views/
│       │   ├── InstallWindow.xaml(.cs)    # 安装向导窗口
│       │   ├── MainWindow.xaml(.cs)       # 主程序窗口（内置浏览器 + 控制台）
│       │   └── UninstallWindow.xaml(.cs)  # 卸载对话框
│       ├── Services/
│       │   ├── WslService.cs             # WSL2 检测、安装、wsl.conf 配置
│       │   ├── OpenClawService.cs        # Node.js + OpenClaw 安装
│       │   ├── GatewayService.cs         # Gateway 启停控制
│       │   ├── InstallStateService.cs    # 安装状态持久化
│       │   └── UninstallService.cs       # 卸载逻辑
│       └── Assets/
│           ├── icon.ico
│           ├── logo.png
│           └── ubuntu-base.tar.gz  # 内嵌 Ubuntu rootfs（构建时下载，不入 git）
├── installer/
│   └── setup.iss                  # Inno Setup 脚本
└── build.ps1                      # 一键构建脚本
```

## 4. 应用启动逻辑

```
ClawDock.exe 启动
       │
       ▼
  读取安装状态
  (%APPDATA%\ClawDock\state.json)
       │
  ┌────┴────┐
  │ 已安装? │
  └────┬────┘
       │ 否                    是
       ▼                       ▼
  检测是否 Admin          显示 MainWindow
       │                  (内置浏览器)
  ┌────┴────┐             自动启动 Gateway
  │ Is Admin│
  └────┬────┘
       │ 否                    是
       ▼                       ▼
  以管理员身份          显示 InstallWindow
  重新启动              (安装向导)
```

## 5. 安装向导（InstallWindow）步骤

### Step 1 — Welcome
- 展示 ClawDock 介绍
- 列出将要安装的内容
- [开始安装] 按钮

### Step 2 — 系统检测
自动检测以下项目：

| 检测项 | 通过条件 | 操作 |
|--------|----------|------|
| Windows 版本 | Build 19041+ (Win10 2004) | 不满足则提示退出 |
| 虚拟化支持 | Hyper-V / VT-x 已启用 | 提示在 BIOS 开启 |
| WSL2 已安装 | `wsl --status` 成功 | 已装则跳过 |
| WSL2 发行版 | ClawDock 发行版存在（注册表检测） | 已装则跳过 |

### Step 3 — 安装 WSL2 + 导入 Ubuntu
1. `wsl --install --no-distribution` — 启用 WSL2 功能
2. 从 exe 内嵌资源解压 `ubuntu-base.tar.gz`，通过 `wsl --import ClawDock` 导入为独立发行版
   - 使用独立发行版名 `ClawDock`，避免与用户自己的 Ubuntu 冲突
   - rootfs 内嵌于 exe 中，无需联网下载
3. 配置 apt 国内镜像（USTC）加速后续软件包下载
4. 写入 `/etc/wsl.conf`：
   - `[interop] appendWindowsPath = false` — 禁用 Windows PATH 互通，防止 Windows 的 node/npm 泄漏
   - `[boot] systemd = true` — 启用 systemd（Gateway 依赖）
5. 重启发行版使 wsl.conf 生效
- 若导入失败（首次安装 WSL2 需重启）：写入续装标记，提示用户重启

### Step 4 — 安装 OpenClaw
在 WSL2 ClawDock 发行版内依次执行：
```bash
# 1. 更新包列表
apt-get update

# 2. 安装基础工具
apt-get install -y curl ca-certificates git xz-utils

# 3. 从淘宝 Node.js 镜像下载二进制包（避免依赖境外 NodeSource 源）
NODE_VER=$(curl -fsSL https://npmmirror.com/mirrors/node/latest-v22.x/SHASUMS256.txt | ...)
curl -fsSL https://npmmirror.com/mirrors/node/v$NODE_VER/node-v$NODE_VER-linux-x64.tar.xz \
  | tar -xJ --strip-components=1 -C /usr/local

# 4. 验证 Node.js 22 安装成功
/usr/local/bin/node --version  # 确认 v22.x

# 5. 配置 npm 淘宝镜像
/usr/local/bin/npm config set registry https://registry.npmmirror.com

# 6. 全局安装 OpenClaw
/usr/local/bin/npm install -g openclaw@latest
```

**网络依赖全部使用国内镜像：**
| 步骤 | 镜像源 |
|------|--------|
| apt 软件包 | mirrors.ustc.edu.cn |
| Node.js 二进制包 | npmmirror.com/mirrors/node |
| npm 包 | registry.npmmirror.com |

每一步实时流式显示日志。

### Step 5 — 完成
- 写入安装状态文件
- 显示「安装完成」
- [立即打开 ClawDock] → 关闭向导，打开 MainWindow

## 6. 主窗口（MainWindow）

### 布局
```
┌──────────────────────────────────────────────────────────┐
│  🦞 ClawDock  [启动][停止][重启] [浏览器] [控制台] ● 运行中 │  ← 工具栏
├──────────────────────────────────────────────────────────┤
│                                                          │
│                     WebView2                             │  ← 主内容区
│                localhost:18789                            │
│                                                          │
├──────────────────────────────────────────────────────────┤
│  Console                                    [Clear] [✕]  │  ← 日志控制台
│  [gateway] listening on ws://127.0.0.1:18789             │    （可折叠）
│  [gateway] auth token was missing...                     │
└──────────────────────────────────────────────────────────┘
```

### 功能
- **WebView2**：加载 `http://localhost:18789`，UserDataFolder 在 `%LocalAppData%\ClawDock\WebView2\`
- **启动 Gateway**：`wsl -d ClawDock -- bash -l -c "openclaw gateway --port 18789 --allow-unconfigured"`
- **停止 Gateway**：pkill + 终止进程树
- **重启 Gateway**：停止 + 启动
- **状态指示器**：轮询 localhost:18789，颜色状态（绿=运行/橙=启动中/红=错误/灰=停止）
- **日志控制台**：底部可折叠面板，实时显示 Gateway stdout/stderr，支持清空
- **系统托盘**：关闭按钮最小化到托盘，右键菜单提供开/关/退出
- **开机自启**：可选写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

## 7. 安装状态文件格式

路径：`%APPDATA%\ClawDock\state.json`

```json
{
  "installed": true,
  "installDate": "2025-01-01T00:00:00Z",
  "version": "0.6.0",
  "phase": "complete",
  "wsl2Installed": true,
  "ubuntuInstalled": true,
  "openclawInstalled": true
}
```

`phase` 取值：`wsl2` | `openclaw` | `complete`（用于重启续装）

## 8. 重启续装机制

安装 WSL2 需要重启时：
1. 将 `phase` 写为 `openclaw`（已完成 WSL2，下次继续安装 OpenClaw）
2. 在注册表写入开机启动：
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ClawDockResume = "C:\...\ClawDock.exe"`
3. 提示用户重启
4. 重启后 App.xaml.cs 读取 `phase != complete`，自动跳到对应步骤继续

## 9. WSL 环境隔离策略

为避免与用户已有的 WSL 环境冲突：

| 措施 | 说明 |
|------|------|
| 独立发行版名 `ClawDock` | 不影响用户的 Ubuntu/Debian 等发行版 |
| 独立安装目录 | `%LocalAppData%\ClawDock\WSL\` |
| 内嵌 rootfs | exe 内含 Ubuntu 22.04 base，`wsl --import` 导入，无需联网 |
| 禁用 Windows PATH | `wsl.conf` 中 `appendWindowsPath = false`，确保使用 Linux 本地 Node.js |
| 启用 systemd | `wsl.conf` 中 `systemd = true`，Gateway 正常运行所需 |

## 10. 打包产物

| 文件 | 说明 | 大小 |
|------|------|------|
| `ClawDock.exe` | 主程序（自包含 .NET 8 + 内嵌 Ubuntu rootfs） | ~184 MB |
| `ClawDockSetup.exe` | Inno Setup 打包的安装程序 | ~120 MB |

## 11. 依赖版本

| 依赖 | 版本 |
|------|------|
| .NET | 8.0 (LTS) |
| Microsoft.Web.WebView2 | 1.0.2792.45 |
| Target OS | Windows 10 Build 19041+ / Windows 11 |
| Inno Setup | 6.x |
| Node.js (WSL 内) | 22.x LTS（npmmirror 二进制包） |
| Ubuntu (WSL 内) | 22.04 base |

## 12. 版本信息

| 字段 | 值 |
|------|-----|
| 当前版本 | 0.6.0 |
| 发布者 | 野生刘同学 |
