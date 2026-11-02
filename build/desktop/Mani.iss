; Windows deployment boundary. IINA's corresponding distribution is app/DMG.
; Inno Setup implements directory selection, rollback, upgrade and uninstall.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifdef TestInstall
  #define ProductId "Mani.Desktop.InstallTest"
  #define ProductName "Mani Installation Test"
#else
  #define ProductId "Mani.Desktop"
  #define ProductName "Mani"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=Mani contributors
AppPublisherURL=https://github.com/showmethecold/mani
DefaultDirName={autopf}\{#ProductName}
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowRootDirectory=no
UsePreviousAppDir=yes
UsePreviousPrivileges=yes
UsePreviousLanguage=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.26100
OutputDir={#InstallerOutput}
OutputBaseFilename={#InstallerName}
SetupIconFile=..\..\src\Mani.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Mani.App.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=yes
ChangesAssociations=yes
AppMutex={#ProductId}.Running
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UninstallLogMode=append
SignedUninstaller={#SignUninstaller}
#ifdef SignCommand
SignTool=mani
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "zhTW"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"

[CustomMessages]
en.DesktopShortcut=Create a desktop shortcut
zh.DesktopShortcut=创建桌面快捷方式
zhTW.DesktopShortcut=建立桌面捷徑
en.DefaultPlayer=Choose default video player in Windows Settings
zh.DefaultPlayer=在 Windows 设置中选择默认视频播放器
zhTW.DefaultPlayer=在 Windows 設定中選擇預設影片播放器
en.OccupiedFolder=Choose an empty folder for Mani. Setup will not overwrite an unrelated folder.
zh.OccupiedFolder=请选择空文件夹安装 Mani，安装程序不会覆盖其他文件夹中的文件。
zhTW.OccupiedFolder=請選擇空資料夾安裝 Mani，安裝程式不會覆寫其他資料夾中的檔案。
en.MoveUpgrade=Mani is already installed in %1. Upgrade in that folder, or uninstall Mani before choosing another location. Your settings and history will be kept.
zh.MoveUpgrade=Mani 已安装在 %1。请在原目录升级，或先卸载再选择新目录。设置和历史记录会保留。
zhTW.MoveUpgrade=Mani 已安裝在 %1。請在原目錄升級，或先解除安裝再選擇新目錄。設定和歷史記錄會保留。
en.NewerVersion=A newer version of Mani is installed. This installer cannot replace it.
zh.NewerVersion=已安装更新版本的 Mani，此安装程序不能将其降级。
zhTW.NewerVersion=已安裝更新版本的 Mani，此安裝程式不能將其降級。
en.VideoType=Mani video
zh.VideoType=Mani 视频
zhTW.VideoType=Mani 影片
en.AppDescription=Video player inspired by IINA
zh.AppDescription=受 IINA 启发的视频播放器
zhTW.AppDescription=受 IINA 啟發的影片播放器
en.LegacyNote=An earlier Microsoft Store-style Mani installation can coexist with this version. Your settings and history are retained. After checking this installation, remove the earlier Mani entry in Windows Settings > Apps.
zh.LegacyNote=旧版 Mani 的 Windows 应用包可能与此版本同时存在。设置和历史记录会保留。确认新版本正常后，请在 Windows 设置 > 应用中卸载旧版 Mani。
zhTW.LegacyNote=舊版 Mani 的 Windows 應用程式套件可能與此版本同時存在。設定和歷史記錄會保留。確認新版本正常後，請在 Windows 設定 > 應用程式中解除安裝舊版 Mani。

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#ProductName}"; Filename: "{app}\Mani.App.exe"; IconFilename: "{app}\Mani.App.exe"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\Mani.App.exe"; IconFilename: "{app}\Mani.App.exe"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\{#ProductId}"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\{#ProductId}"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"
Root: HKA; Subkey: "Software\{#ProductId}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#ProductName}"
Root: HKA; Subkey: "Software\{#ProductId}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{cm:AppDescription}"
Root: HKA; Subkey: "Software\{#ProductId}\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\Mani.App.exe,0"
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#ProductId}"; ValueData: "Software\{#ProductId}\Capabilities"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\{#ProductId}.Video"; ValueType: string; ValueData: "{cm:VideoType}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#ProductId}.Video"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "{cm:VideoType}"
Root: HKA; Subkey: "Software\Classes\{#ProductId}.Video\DefaultIcon"; ValueType: string; ValueData: "{app}\Mani.App.exe,0"
Root: HKA; Subkey: "Software\Classes\{#ProductId}.Video\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#ProductName}"
Root: HKA; Subkey: "Software\Classes\{#ProductId}.Video\shell\open"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"
Root: HKA; Subkey: "Software\Classes\{#ProductId}.Video\shell\open\command"; ValueType: string; ValueData: """{app}\Mani.App.exe"" ""%1"""
#include AssociationInclude

[Run]
Filename: "{app}\Mani.App.exe"; Description: "{cm:LaunchProgram,{#ProductName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser unchecked
Filename: "ms-settings:defaultapps"; Description: "{cm:DefaultPlayer}"; Flags: shellexec postinstall skipifsilent runasoriginaluser unchecked

[Code]
function NonEmptyDirectory(const Path: String): Boolean;
var Entry: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Path) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin Result := True; Break; end;
      until not FindNext(Entry);
    finally FindClose(Entry); end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var PreviousDir, PreviousVersion: String;
    Existing, Incoming: Int64;
begin
  Result := '';
  if RegQueryStringValue(HKA, 'Software\{#ProductId}', 'Version', PreviousVersion) then begin
    if not StrToVersion(PreviousVersion, Existing) or not StrToVersion('{#AppVersion}', Incoming) or
       (ComparePackedVersion(Existing, Incoming) > 0) then begin
      Result := CustomMessage('NewerVersion'); Exit;
    end;
  end;
  if RegQueryStringValue(HKA, 'Software\{#ProductId}', 'InstallDir', PreviousDir) then begin
    if CompareText(RemoveBackslashUnlessRoot(PreviousDir), RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0 then
      Result := FmtMessage(CustomMessage('MoveUpgrade'), [PreviousDir]);
  end else if NonEmptyDirectory(ExpandConstant('{app}')) then
    Result := CustomMessage('OccupiedFolder');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and DirExists(ExpandConstant('{localappdata}\Packages\Mani.Beta.Test_h6anv01wm286t')) then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + CustomMessage('LegacyNote');
end;
