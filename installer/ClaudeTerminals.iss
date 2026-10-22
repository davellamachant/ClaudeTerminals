; Inno Setup script for Claude Terminals. Build with ..\publish.ps1 (passes AppVersion and SourceDir).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

#define AppName "Claude Terminals"
#define AppExe "ClaudeTerminals.exe"
#define AppPublisher "LLAMACHANT TECHNOLOGY LTD"

[Setup]
; Never change AppId: it is how upgrades and uninstall find the existing install.
AppId={{A224708F-0121-4F63-924D-5E894BC86AF6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

; Per-user install into %LOCALAPPDATA%\Programs (no admin prompt); the dialog lets an admin choose all users instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; Setup asks to close a running copy (Program.cs owns this mutex) before replacing files.
AppMutex=ClaudeTerminals.SingleInstance
CloseApplications=yes
RestartApplications=no

OutputDir=..\artifacts
OutputBaseFilename=ClaudeTerminals-Setup-{#AppVersion}
SetupIconFile=..\src\ClaudeTerminals\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
#ifdef Sign
; publish.ps1 -Sign defines the "signtool" command (ISCC /S); setup and the uninstaller it embeds are both signed.
SignTool=signtool
SignedUninstaller=yes
#endif
Compression=lzma2/max
SolidCompression=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Replace the web assets wholesale so files removed in a new version don't linger.
Type: filesandordirs; Name: "{app}\wwwroot"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; The app's updater installs silently with /RELAUNCH=1: reopen it afterwards, as the user (not elevated, if setup was).
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: RelaunchAfterUpdate

[UninstallDelete]
; Browser cache only. Saved sessions in %APPDATA%\ClaudeTerminals are kept for a reinstall.
Type: filesandordirs; Name: "{localappdata}\ClaudeTerminals\WebView2"

[Code]
const
  WebView2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Key32 = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasWebView2Version(RootKey: Integer; SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function IsWebView2Installed(): Boolean;
begin
  Result := HasWebView2Version(HKLM, WebView2Key32) or HasWebView2Version(HKLM, WebView2Key) or HasWebView2Version(HKCU, WebView2Key);
end;

function RelaunchAfterUpdate(): Boolean;
begin
  Result := WizardSilent() and (ExpandConstant('{param:relaunch|0}') = '1');
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  // Built into Windows 11; only older or stripped-down systems lack it.
  if not IsWebView2Installed() then
  begin
    if MsgBox('Claude Terminals needs the Microsoft Edge WebView2 Runtime, which was not found on this PC.' + #13#10 + #13#10 +
              'Open the download page now? Setup will continue; install the runtime before starting the app.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;
