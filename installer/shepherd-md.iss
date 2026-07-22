; Shepherd MD - installer script (Inno Setup 6)
; Build:  "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" shepherd-md.iss
;
; Per-user install (no admin prompt). This is deliberate: the app keeps its
; config/session/window state next to its own exe, so it must live somewhere
; writable. %LOCALAPPDATA%\Programs is writable; Program Files is not.

#define AppName        "Shepherd Markdown"
#define AppVersion     "1.0.4"
#define AppPublisher   "Shepherd"
#define AppExeName     "ShepherdMD.exe"

[Setup]
AppId={{8F3A6C21-4D7E-4B95-9A62-1E5C7D0B3F84}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Shepherd-Markdown-Setup
SetupIconFile=payload\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
AppMutex=ShepherdMD_SingleInstance_v1
CloseApplications=yes
InfoAfterFile=payload\README.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "assocmd";     Description: "Add Shepherd Markdown to the ""Open with"" list for Markdown files"; GroupDescription: "File associations:"

[Files]
; everything except config.json, which must not clobber an existing user's folders
Source: "payload\*"; DestDir: "{app}"; Excludes: "config.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; ProgID so the app appears under "Open with". Windows 11 stores the actual
; default in a hash-protected UserChoice key no installer may write, so the
; final "always use this app" is one click by the user.
Root: HKCU; Subkey: "Software\Classes\ShepherdMD.md"; ValueType: string; ValueData: "Markdown Document"; Flags: uninsdeletekey; Tasks: assocmd
Root: HKCU; Subkey: "Software\Classes\ShepherdMD.md\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExeName},0"; Tasks: assocmd
Root: HKCU; Subkey: "Software\Classes\ShepherdMD.md\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Tasks: assocmd
Root: HKCU; Subkey: "Software\Classes\ShepherdMD.md\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: assocmd

Root: HKCU; Subkey: "Software\Classes\.md\OpenWithProgids";       ValueType: none; ValueName: "ShepherdMD.md"; Flags: uninsdeletevalue; Tasks: assocmd
Root: HKCU; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: none; ValueName: "ShepherdMD.md"; Flags: uninsdeletevalue; Tasks: assocmd
Root: HKCU; Subkey: "Software\Classes\.mdown\OpenWithProgids";    ValueType: none; ValueName: "ShepherdMD.md"; Flags: uninsdeletevalue; Tasks: assocmd
Root: HKCU; Subkey: "Software\Classes\.mkd\OpenWithProgids";      ValueType: none; ValueName: "ShepherdMD.md"; Flags: uninsdeletevalue; Tasks: assocmd

; show up in Settings > Default apps
Root: HKCU; Subkey: "Software\ShepherdMD\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: assocmd
Root: HKCU; Subkey: "Software\ShepherdMD\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A fast, offline reader for Markdown files."; Tasks: assocmd
Root: HKCU; Subkey: "Software\ShepherdMD\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\{#AppExeName},0"; Tasks: assocmd
Root: HKCU; Subkey: "Software\ShepherdMD\Capabilities\FileAssociations"; ValueType: string; ValueName: ".md"; ValueData: "ShepherdMD.md"; Tasks: assocmd
Root: HKCU; Subkey: "Software\ShepherdMD\Capabilities\FileAssociations"; ValueType: string; ValueName: ".markdown"; ValueData: "ShepherdMD.md"; Tasks: assocmd
Root: HKCU; Subkey: "Software\ShepherdMD\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdown"; ValueData: "ShepherdMD.md"; Tasks: assocmd
Root: HKCU; Subkey: "Software\ShepherdMD\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkd"; ValueData: "ShepherdMD.md"; Tasks: assocmd
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\ShepherdMD\Capabilities"; Flags: uninsdeletevalue; Tasks: assocmd

[Run]
; runasoriginaluser so the app comes back as the user (not elevated); NO skipifsilent, so a silent
; update (triggered from the in-app "Update now") relaunches the app after installing.
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall runasoriginaluser

[UninstallDelete]
; files the app generates at runtime, so uninstall leaves nothing behind
Type: filesandordirs; Name: "{app}\webview2profile"
Type: files; Name: "{app}\running.json"
Type: files; Name: "{app}\session.json"
Type: files; Name: "{app}\windowstate.txt"
Type: files; Name: "{app}\config.json"
Type: dirifempty; Name: "{app}"

[Code]
function WebView2Installed(): Boolean;
var
  v: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) then
    Result := (v <> '') and (v <> '0.0.0.0');
  if not Result then
    if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) then
      Result := (v <> '') and (v <> '0.0.0.0');
  if not Result then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) then
      Result := (v <> '') and (v <> '0.0.0.0');
end;

function InitializeSetup(): Boolean;
var
  ec: Integer;
begin
  Result := True;
  if not WebView2Installed() then
  begin
    if MsgBox('Shepherd Markdown needs the Microsoft Edge WebView2 runtime, which does not look like it is installed on this PC.' + #13#10#13#10 +
              'It is a free Microsoft component that comes built into Windows 11.' + #13#10#13#10 +
              'Open the download page now? (Install it, then run this setup again.)',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://go.microsoft.com/fwlink/p/?LinkId=2124703', '', '', SW_SHOW, ewNoWait, ec);
      Result := False;
    end;
  end;
end;
