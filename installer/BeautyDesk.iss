; Установщик BeautyDesk (Inno Setup 6).
; Сборка: ISCC /DAppVersion=0.4.0 /DSourceDir=<папка с опубликованной программой> /DOutDir=<куда положить> BeautyDesk.iss
; Ставится в папку пользователя, права администратора не нужны.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{B6E1F2D4-7A53-4C1E-9E0B-BEA07DE5C001}
AppName=BeautyDesk
AppVersion={#AppVersion}
AppPublisher=Sheddy4
DefaultDirName={autopf}\BeautyDesk
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir={#OutDir}
OutputBaseFilename=BeautyDesk-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\BeautyDesk.exe
WizardStyle=modern

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\BeautyDesk"; Filename: "{app}\BeautyDesk.exe"

[Registry]
; Автозапуск включается в настройках программы; при удалении запись убирается.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "BeautyDesk"; ValueType: none; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\BeautyDesk.exe"; Description: "{cm:LaunchProgram,BeautyDesk}"; Flags: nowait postinstall skipifsilent

[Code]
// Перед обновлением и перед удалением работающий BeautyDesk нужно закрыть, причём как положено:
// команда --quit возвращает панель задач Windows, а простое завершение процесса — нет.
procedure QuitRunningApp();
var
  Code: Integer;
begin
  if FileExists(ExpandConstant('{app}\BeautyDesk.exe')) then
    Exec(ExpandConstant('{app}\BeautyDesk.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  QuitRunningApp();
  Result := True;
end;
