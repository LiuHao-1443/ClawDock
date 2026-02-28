# build.ps1 — OpenClaw Windows 一键构建脚本
# 依赖：.NET 8 SDK、Inno Setup 6（iscc.exe 在 PATH 中）

param(
    [switch]$SkipPublish,   # 跳过 dotnet publish（仅重新打包）
    [switch]$SkipInno       # 跳过 Inno Setup（仅编译 C#）
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectDir = "$PSScriptRoot\src\OpenClawApp"
$PublishDir = "$ProjectDir\bin\publish"
$DistDir    = "$PSScriptRoot\dist"

Write-Host ""
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "  OpenClaw Build Script" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

# 1. 检查依赖
Write-Host "[1/3] 检查构建依赖..." -ForegroundColor Yellow

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "未找到 dotnet CLI，请安装 .NET 8 SDK: https://dotnet.microsoft.com/download"
}
Write-Host "  ✓ .NET SDK: $(dotnet --version)"

if (-not $SkipInno) {
    $iscc = Get-Command iscc -ErrorAction SilentlyContinue
    if (-not $iscc) {
        # 尝试默认安装路径
        $defaultPath = "C:\Program Files (x86)\Inno Setup 6\iscc.exe"
        if (Test-Path $defaultPath) {
            $iscc = $defaultPath
        } else {
            Write-Error "未找到 Inno Setup (iscc.exe)。请安装: https://jrsoftware.org/isinfo.php 或使用 -SkipInno 跳过"
        }
    }
    Write-Host "  ✓ Inno Setup: $iscc"
}

# 2. dotnet publish（自包含，单文件）
if (-not $SkipPublish) {
    Write-Host ""
    Write-Host "[2/3] 编译并发布..." -ForegroundColor Yellow

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
    Write-Host "[2/3] 跳过 dotnet publish" -ForegroundColor Gray
}

# 3. Inno Setup 打包
if (-not $SkipInno) {
    Write-Host ""
    Write-Host "[3/3] 打包安装程序..." -ForegroundColor Yellow

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

    $output = "$DistDir\OpenClawSetup.exe"
    $size   = [math]::Round((Get-Item $output).Length / 1MB, 1)
    Write-Host "  ✓ 安装包: $output ($size MB)" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[3/3] 跳过 Inno Setup" -ForegroundColor Gray
}

Write-Host ""
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "  构建完成！" -ForegroundColor Green
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""
