; OSS 拖拽上传 —— 安装包（Inno Setup）
;
; 构建：powershell -File build-installer.ps1   →  dist\FileUploader-<版本>-setup.exe
;
; 设计要点：
;   * 每用户安装（%LOCALAPPDATA%\Programs\FileUploader）：安装和静默升级都不需要管理员权限，
;     这是"点一下就更新"能真正无感的前提（装在 Program Files 每次升级都会弹 UAC）。
;   * 写 HKCU\Software\FileUploader\InstallPath：应用靠它判断"我是安装版"，
;     exe 被拷到别处（或旁边有 portable.txt）时自然退化成便携版，不会被自动替换。
;   * 静默升级：应用下载本安装包后带 /VERYSILENT 运行；[Run] 里 Check: WizardSilent 的那条
;     会在安装完成后把程序重新拉起来，否则用户点完「立即更新」面前就什么都不剩了。
;   * 交互式安装时不自动启动（那个勾选项默认不勾），尊重用户的节奏。

#ifndef MyVersion
  #define MyVersion "0.0.0"
#endif

#ifndef MySourceExe
  #define MySourceExe "..\dist-portable\FileUploader.exe"
#endif

#ifndef MyIcon
  #define MyIcon "..\src\FileUploader.App\Assets\app.ico"
#endif

#define MyAppName "OSS 拖拽上传"
#define MyAppExeName "FileUploader.exe"
#define MyAppPublisher "qianshipoi"
#define MyAppUrl "https://github.com/qianshipoi/FileUploader"

[Setup]
; AppId 一成不变：升级时靠它识别"这是同一个程序的另一个版本"，而不是装成第二份。
AppId={{8A6C1B4E-4F1D-4E2B-9C5A-1F3D6E7A9B21}
AppName={#MyAppName}
AppVersion={#MyVersion}
AppVerName={#MyAppName} {#MyVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={localappdata}\Programs\FileUploader
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=FileUploader-{#MyVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#MyIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
AllowNoIcons=yes
SetupLogging=yes
; 手动安装时先把正在跑的程序关掉；自动升级时程序在我们启动安装器之前就已经退出了。
CloseApplications=yes
RestartApplications=no

[Languages]
; Inno 没有官方简体中文语言包，安装界面保持英文（静默升级时根本不显示界面）。
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#MySourceExe}"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
; 应用判断"安装版 / 便携版"的唯一依据（值为安装目录，不是 exe 路径）。
Root: HKCU; Subkey: "Software\FileUploader"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\FileUploader"; ValueType: string; ValueName: "Version"; ValueData: "{#MyVersion}"; Flags: uninsdeletekey

[Run]
; 交互式安装：完成页上的「运行」勾选项，默认不勾。
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent unchecked
; 静默升级（应用自己下载安装包后发起的）：装完必须把程序拉起来。
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
; 只清 %LOCALAPPDATA%\FileUploader（升级下载缓存），配置与历史在 %APPDATA%\FileUploader，卸载不动它们。
Type: filesandordirs; Name: "{localappdata}\FileUploader"
