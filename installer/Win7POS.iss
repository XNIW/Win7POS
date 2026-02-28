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

