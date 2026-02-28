; Win7POS Inno Setup script skeleton
; Win7-compatible target, no NuGet dependency.
; This installer deploys app binaries only.
; Data folder in %ProgramData%\Win7POS is intentionally NOT touched.

#define MyAppName "Win7POS"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Win7POS"
#define MyAppExeName "Win7POS.Wpf.exe"
#define MyAppSourceDir "..\dist\Win7POS"

[Setup]
AppId={{2C3D8A34-5312-4A1C-90C5-6A6F2D4A9873}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=Win7POS-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x86 x64
ArchitecturesInstallIn64BitMode=no
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; Pack all files produced by release-pack into Program Files\Win7POS
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Optional: run Win7POS after setup
Filename: "{app}\{#MyAppExeName}"; Description: "Esegui Win7POS"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Intentionally empty for ProgramData safety.
; Do NOT add entries for %ProgramData%\Win7POS here.

[Code]
function IsDotNet48OrLaterInstalled(): Boolean;
var
  ReleaseValue: Cardinal;
begin
  ReleaseValue := 0;
  Result :=
    RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', ReleaseValue) or
    RegQueryDWordValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', ReleaseValue);

  if Result then
    Result := (ReleaseValue >= 528040);
end;

function IsVcRuntimeX86Installed(): Boolean;
var
  Installed: Cardinal;
begin
  Installed := 0;
  Result :=
    RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86', 'Installed', Installed) or
    RegQueryDWordValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86', 'Installed', Installed);

  if Result then
    Result := (Installed = 1);
end;

function InitializeSetup(): Boolean;
begin
  if not IsDotNet48OrLaterInstalled() then
  begin
    MsgBox(
      '.NET Framework 4.8 (o superiore) non trovato.' + #13#10#13#10 +
      'Installa .NET Framework 4.8 e riesegui il setup.',
      mbCriticalError,
      MB_OK);
    Result := False;
    Exit;
  end;

  if not IsVcRuntimeX86Installed() then
  begin
    MsgBox(
      'Avviso: Microsoft Visual C++ Runtime x86 non rilevato.' + #13#10 +
      'Alcune dipendenze native (es. SQLite) potrebbero richiederlo.' + #13#10#13#10 +
      'Il setup continua comunque.',
      mbInformation,
      MB_OK);
  end;

  Result := True;
end;

