; OpenClaw Windows Installer
; 使用 Inno Setup 6.x 编译
; https://jrsoftware.org/isinfo.php

#define AppName "OpenClaw"
#define AppVersion "1.0.0"
#define AppPublisher "OpenClaw"
#define AppURL "https://github.com/openclaw/openclaw"
#define AppExeName "OpenClaw.exe"
#define PublishDir "..\src\OpenClawApp\bin\publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=no
; 需要管理员权限（安装 WSL2）
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=OpenClawSetup
SetupIconFile=..\src\OpenClawApp\Assets\icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; 最低 Windows 10 版本
MinVersion=10.0.19041
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
CloseApplications=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked
Name: "startupicon"; Description: "开机自动启动"; GroupDescription: "启动选项:"; Flags: unchecked

[Files]
; 应用程序主文件（自包含发布目录中的所有文件）
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 开始菜单
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
; 桌面（可选）
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启（可选）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startupicon

[Run]
; 安装完成后可选择立即启动
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前停止正在运行的实例
Filename: "taskkill"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
// 检查 Windows 版本
function InitializeSetup(): Boolean;
var
  Build: Cardinal;
begin
  Result := True;
  if not RegQueryDWordValue(HKEY_LOCAL_MACHINE,
    'SOFTWARE\Microsoft\Windows NT\CurrentVersion',
    'CurrentBuildNumber', Build) then
  begin
    MsgBox('无法读取 Windows 版本信息。', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if Build < 19041 then
  begin
    MsgBox(Format(
      'OpenClaw 需要 Windows 10 Build 19041 或更高版本 / Windows 11。' + #13#10 +
      '您当前的版本: Build %d。' + #13#10 +
      '请先升级 Windows 再安装。', [Build]),
      mbError, MB_OK);
    Result := False;
  end;
end;
