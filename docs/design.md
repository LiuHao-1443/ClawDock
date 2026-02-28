# OpenClaw for Windows — 设计文档

## 1. 项目目标

为 OpenClaw（https://github.com/openclaw/openclaw）提供一个 Windows 一键安装 + 启动器方案：

- 用户双击 `OpenClawSetup.exe`，全自动完成所有依赖安装
- 安装完成后，桌面/开始菜单出现 `OpenClaw.exe`
- 打开 `OpenClaw.exe` 即可通过内置浏览器使用 OpenClaw

## 2. 技术选型

### 2.1 核心框架：C# + WPF + WebView2

| 组件 | 技术 | 说明 |
|------|------|------|
| 安装向导 + 主程序 | C# 12 + .NET 8 + WPF | 单个项目，兼顾安装和运行 |
| 内置浏览器 | Microsoft.Web.WebView2 | Edge 内核，Win10/11 预装 |
| Windows 服务注册 | System.ServiceProcess | 管理 openclaw gateway 后台服务 |
| 系统托盘 | WPF + NotifyIcon | 最小化到托盘，后台运行 |

**为什么选 C# + WPF：**
- .NET 8 可以「自包含发布」，无需目标机器预装运行时，exe 约 60-80MB
- WebView2 随 Microsoft Edge 预装在所有 Win10 1803+ 和 Win11 上
- WPF 原生 Windows UI，速度快，界面原生
- 直接调用 Windows API，管理 WSL2 和 Windows 服务最方便

### 2.2 打包工具：Inno Setup

- 将 `OpenClaw.exe` 及相关文件打包成 `OpenClawSetup.exe`
- 自动申请管理员权限（UAC）
- 处理安装路径、桌面快捷方式、开始菜单
- 支持卸载

## 3. 解决方案结构

```
openclaw-package/
├── docs/
│   └── design.md                  # 本文档
├── src/
│   └── OpenClawApp/               # 主 C# 项目
│       ├── OpenClawApp.csproj
│       ├── App.xaml
│       ├── App.xaml.cs            # 启动入口：判断安装状态
│       ├── Views/
│       │   ├── InstallWindow.xaml        # 安装向导窗口
│       │   ├── InstallWindow.xaml.cs
│       │   ├── MainWindow.xaml           # 主程序窗口（内置浏览器）
│       │   └── MainWindow.xaml.cs
│       ├── Services/
│       │   ├── WslService.cs             # WSL2 检测与安装
│       │   ├── OpenClawService.cs        # OpenClaw 安装与管理
│       │   ├── GatewayService.cs         # Gateway 启停控制
│       │   └── InstallStateService.cs    # 安装状态持久化
│       └── Assets/
│           └── icon.ico
├── installer/
│   └── setup.iss                  # Inno Setup 脚本
└── build.ps1                      # 一键构建脚本
```

## 4. 应用启动逻辑

```
OpenClaw.exe 启动
       │
       ▼
  读取安装状态
  (%APPDATA%\OpenClaw\state.json)
       │
  ┌────┴────┐
  │ 已安装? │
  └────┬────┘
       │ 否                    是
       ▼                       ▼
  检测是否 Admin          显示 MainWindow
       │                  (内置浏览器)
  ┌────┴────┐
  │ Is Admin│
  └────┬────┘
       │ 否                    是
       ▼                       ▼
  以管理员身份          显示 InstallWindow
  重新启动              (安装向导)
```

## 5. 安装向导（InstallWindow）步骤

### Step 1 — Welcome
- 展示 OpenClaw 介绍
- 列出将要安装的内容
- [开始安装] 按钮

### Step 2 — 系统检测
自动检测以下项目：

| 检测项 | 通过条件 | 操作 |
|--------|----------|------|
| Windows 版本 | Build 19041+ (Win10 2004) | 不满足则提示退出 |
| 虚拟化支持 | Hyper-V / VT-x 已启用 | 提示在 BIOS 开启 |
| WSL2 已安装 | `wsl --status` 成功 | 已装则跳过安装步骤 |
| WSL2 发行版 | Ubuntu 存在 | 已装则跳过 |

### Step 3 — 安装 WSL2
```powershell
wsl --install -d Ubuntu
```
- 实时显示输出日志
- 若需重启：写入注册表续装标记，提示用户重启
- 重启后自动打开 OpenClaw 并继续安装

### Step 4 — 安装 OpenClaw
在 WSL2 Ubuntu 内依次执行：
```bash
# 1. 更新包列表
sudo apt-get update -y

# 2. 安装 Node.js 22
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
sudo apt-get install -y nodejs

# 3. 安装 OpenClaw
sudo npm install -g openclaw@latest

# 4. 启动 daemon
openclaw onboard --install-daemon
```
每一步实时流式显示日志。

### Step 5 — 完成
- 写入安装状态文件
- 显示「安装完成」
- [立即打开 OpenClaw] → 关闭向导，打开 MainWindow

## 6. 主窗口（MainWindow）

### 布局
```
┌─────────────────────────────────────────────────┐
│  🦞 OpenClaw        [启动][停止][重启]  ● 运行中 │  ← 工具栏 (40px)
├─────────────────────────────────────────────────┤
│                                                 │
│              WebView2                           │  ← 主内容区
│         localhost:18789                         │
│                                                 │
└─────────────────────────────────────────────────┘
```

### 功能
- **WebView2**：加载 `http://localhost:18789`，Gateway 未运行时显示等待页
- **启动 Gateway**：`wsl -- openclaw gateway --port 18789`
- **停止 Gateway**：结束对应 WSL 进程
- **重启 Gateway**：停止 + 启动
- **状态指示器**：轮询 localhost:18789，显示运行/停止状态
- **系统托盘**：关闭按钮最小化到托盘，右键菜单提供开/关/退出
- **开机自启**：写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

## 7. 安装状态文件格式

路径：`%APPDATA%\OpenClaw\state.json`

```json
{
  "installed": true,
  "installDate": "2025-01-01T00:00:00Z",
  "version": "1.0.0",
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
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\OpenClawResume = "C:\...\OpenClaw.exe"`
3. 提示用户重启
4. 重启后 App.xaml.cs 读取 `phase != complete`，自动跳到对应步骤继续

## 9. 打包产物

| 文件 | 说明 |
|------|------|
| `OpenClaw.exe` | 主程序（自包含，无需安装 .NET） |
| `OpenClawSetup.exe` | Inno Setup 打包的安装程序 |

## 10. 实现顺序

1. **[x] 建立项目结构** — 仓库 + 文档
2. **[ ] 创建 C# 项目** — .csproj + App.xaml + 基本框架
3. **[ ] 实现 InstallStateService** — 读写 state.json
4. **[ ] 实现 WslService** — 检测 + 安装 WSL2
5. **[ ] 实现 InstallWindow** — 安装向导 UI + 逻辑
6. **[ ] 实现 GatewayService** — 启停 openclaw gateway
7. **[ ] 实现 MainWindow** — WebView2 + 工具栏 + 托盘
8. **[ ] 实现 OpenClawService** — OpenClaw 安装逻辑
9. **[ ] 编写 Inno Setup 脚本** — 打包成 Setup.exe
10. **[ ] 编写 build.ps1** — 一键构建

## 11. 依赖版本

| 依赖 | 版本 |
|------|------|
| .NET | 8.0 (LTS) |
| Microsoft.Web.WebView2 | 1.0.2792.45 |
| Target OS | Windows 10 Build 19041+ / Windows 11 |
| Inno Setup | 6.x |
