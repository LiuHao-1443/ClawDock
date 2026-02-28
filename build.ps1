# build.ps1 — ClawDock 构建脚本
# 依赖：.NET 8 SDK、Inno Setup 6（iscc.exe 在 PATH 中）

param(
    [switch]$SkipPublish,   # 跳过 dotnet publish（仅重新打包）
    [switch]$SkipInno       # 跳过 Inno Setup（仅编译 C#）
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectDir = "$PSScriptRoot\src\ClawDock"
$PublishDir = "$ProjectDir\bin\publish"
$DistDir    = "$PSScriptRoot\dist"

Write-Host ""
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "  ClawDock Build Script" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

# 1. 检查依赖
Write-Host "[1/4] 检查构建依赖..." -ForegroundColor Yellow

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "未找到 dotnet CLI，请安装 .NET 8 SDK: https://dotnet.microsoft.com/download"
}
Write-Host "  ✓ .NET SDK: $(dotnet --version)"

if (-not $SkipInno) {
    $iscc = Get-Command iscc -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $defaultPath = "C:\Program Files (x86)\Inno Setup 6\iscc.exe"
        if (Test-Path $defaultPath) {
            $iscc = $defaultPath
        } else {
            Write-Error "未找到 Inno Setup (iscc.exe)。请安装: https://jrsoftware.org/isinfo.php 或使用 -SkipInno 跳过"
        }
    }
    Write-Host "  ✓ Inno Setup: $iscc"
}

# 2. 检查并下载 Ubuntu rootfs（构建时自动获取，不入 git）
Write-Host ""
Write-Host "[2/4] 检查 Ubuntu rootfs..." -ForegroundColor Yellow

$RootfsPath = "$PSScriptRoot\src\ClawDock\Assets\ubuntu-base.tar.gz"
if (-not (Test-Path $RootfsPath)) {
    Write-Host "  未找到 ubuntu-base.tar.gz，正在从 USTC 镜像下载..." -ForegroundColor Yellow
    $RootfsUrl = "https://mirrors.ustc.edu.cn/ubuntu-cdimage/ubuntu-base/releases/22.04/release/ubuntu-base-22.04.3-base-amd64.tar.gz"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $RootfsUrl -OutFile $RootfsPath -UseBasicParsing
        $sizeMB = [math]::Round((Get-Item $RootfsPath).Length / 1MB, 1)
        Write-Host "  ✓ 下载完成: $sizeMB MB" -ForegroundColor Green
    } catch {
        Write-Error "下载 Ubuntu rootfs 失败: $_`n请手动下载并放置到: $RootfsPath`n下载地址: $RootfsUrl"
    }
} else {
    $sizeMB = [math]::Round((Get-Item $RootfsPath).Length / 1MB, 1)
    Write-Host "  ✓ 已存在: $sizeMB MB" -ForegroundColor Green
}

# 3. dotnet publish（自包含，单文件）
if (-not $SkipPublish) {
    Write-Host ""
    Write-Host "[3/4] 编译并发布..." -ForegroundColor Yellow

    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
    }

    dotnet publish $ProjectDir `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $PublishDir

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish 失败"
    }

    Write-Host "  ✓ 发布到: $PublishDir" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[3/4] 跳过 dotnet publish" -ForegroundColor Gray
}

# 4. Inno Setup 打包
if (-not $SkipInno) {
    Write-Host ""
    Write-Host "[4/4] 打包安装程序..." -ForegroundColor Yellow

    if (-not (Test-Path $DistDir)) {
        New-Item -ItemType Directory -Path $DistDir | Out-Null
    }

    $issPath = "$PSScriptRoot\installer\setup.iss"

    if ($iscc -is [string]) {
        & $iscc $issPath
    } else {
        & $iscc.Source $issPath
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Inno Setup 编译失败"
    }

    $output = "$DistDir\ClawDockSetup.exe"
    $size   = [math]::Round((Get-Item $output).Length / 1MB, 1)
    Write-Host "  ✓ 安装包: $output ($size MB)" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[4/4] 跳过 Inno Setup" -ForegroundColor Gray
}

Write-Host ""
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "  构建完成！" -ForegroundColor Green
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""
