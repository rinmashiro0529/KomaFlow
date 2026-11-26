; KomaFlow Inno Setup 6 Installer Script
; Build via scripts/build-release.ps1 or:
;   ISCC.exe /DAppVersion=1.7.0 /DSourceDir=..\dist\stage\KomaFlow /DOutputDir=..\dist KomaFlow.iss

#ifndef AppVersion
  #define AppVersion "1.7.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\dist\stage\KomaFlow"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "KomaFlow"
#define AppPublisher "rinmashiro0529"
#define AppURL "https://github.com/rinmashiro0529/KomaFlow"
#define AppExeName "MangaGui.exe"

[Setup]
AppId={{B3F2E8A4-9C61-4D17-A2F5-7E4C8B1D6390}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=KomaFlow-v{#AppVersion}-win-x64-setup
SetupIconFile=..\src\MangaGui\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
; Check single-instance mutex without force-killing active billing sessions
AppMutex=Global\MangaGui_ColabL4_SingleInstance
CloseApplications=no
ChangesEnvironment=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimp.CreateDesktopIcon=创建桌面快捷方式(&D)
chinesesimp.AddCliToPath=将 MangaCli 命令行工具添加到当前用户 PATH 环境变量(&P)
chinesesimp.UninstallApp=卸载 {#AppName}
chinesesimp.LaunchApp=立即打开 {#AppName}
english.CreateDesktopIcon=Create a &desktop shortcut
english.AddCliToPath=Add MangaCli to current user &PATH environment variable
english.UninstallApp=Uninstall {#AppName}
english.LaunchApp=Launch {#AppName} now

[Tasks]
; Both tasks are checked by default (no "unchecked" flag)
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "addtopath"; Description: "{cm:AddCliToPath}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "KomaFlow Manga Translation Workbench"
Name: "{autoprograms}\{#AppName} ({cm:UninstallApp})"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "KomaFlow Manga Translation Workbench"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
const
  EnvSubKey = 'Environment';
  PathValueName = 'Path';

function NormalizePathEntry(const S: string): string;
begin
  Result := Trim(S);
  while (Length(Result) > 3) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
  Result := Lowercase(Result);
end;

function PathContainsEntry(const PathList, Target: string): Boolean;
var
  Remaining, Entry, NormTarget: string;
  SepPos: Integer;
begin
  Result := False;
  NormTarget := NormalizePathEntry(Target);
  if NormTarget = '' then
    Exit;

  Remaining := PathList;
  while Remaining <> '' do
  begin
    SepPos := Pos(';', Remaining);
    if SepPos > 0 then
    begin
      Entry := Copy(Remaining, 1, SepPos - 1);
      Delete(Remaining, 1, SepPos);
    end
    else
    begin
      Entry := Remaining;
      Remaining := '';
    end;

    if NormalizePathEntry(Entry) = NormTarget then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function RemoveEntryFromPath(const PathList, Target: string; out Changed: Boolean): string;
var
  Remaining, Entry, NormTarget, CleanEntry: string;
  SepPos: Integer;
begin
  Result := '';
  Changed := False;
  NormTarget := NormalizePathEntry(Target);
  if NormTarget = '' then
  begin
    Result := PathList;
    Exit;
  end;

  Remaining := PathList;
  while Remaining <> '' do
  begin
    SepPos := Pos(';', Remaining);
    if SepPos > 0 then
    begin
      Entry := Copy(Remaining, 1, SepPos - 1);
      Delete(Remaining, 1, SepPos);
    end
    else
    begin
      Entry := Remaining;
      Remaining := '';
    end;

    CleanEntry := Trim(Entry);
    if CleanEntry = '' then
      Continue;

    if NormalizePathEntry(CleanEntry) = NormTarget then
    begin
      Changed := True;
      Continue;
    end;

    if Result <> '' then
      Result := Result + ';' + CleanEntry
    else
      Result := CleanEntry;
  end;
end;

procedure AddDirToUserPath(const DirPath: string);
var
  CurrentPath, NewPath: string;
begin
  CurrentPath := '';
  RegQueryStringValue(HKEY_CURRENT_USER, EnvSubKey, PathValueName, CurrentPath);

  if PathContainsEntry(CurrentPath, DirPath) then
    Exit;

  if (CurrentPath <> '') and (CurrentPath[Length(CurrentPath)] <> ';') then
    NewPath := CurrentPath + ';' + DirPath
  else
    NewPath := CurrentPath + DirPath;

  RegWriteExpandStringValue(HKEY_CURRENT_USER, EnvSubKey, PathValueName, NewPath);
end;

procedure RemoveDirFromUserPath(const DirPath: string);
var
  CurrentPath, NewPath: string;
  Changed: Boolean;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, EnvSubKey, PathValueName, CurrentPath) then
    Exit;

  NewPath := RemoveEntryFromPath(CurrentPath, DirPath, Changed);
  if Changed then
    RegWriteExpandStringValue(HKEY_CURRENT_USER, EnvSubKey, PathValueName, NewPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
  begin
    AddDirToUserPath(ExpandConstant('{app}'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveDirFromUserPath(ExpandConstant('{app}'));
  end;
end;
