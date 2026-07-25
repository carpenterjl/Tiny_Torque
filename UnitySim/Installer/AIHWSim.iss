; Inno Setup script for AI Hardware Control Sim.
; Builds a single Setup.exe from the Unity release player.
;
; Prerequisites:
;   1. Produce the release player first:
;        Unity: Tools > AIHWSim > Build Standalone (Release)
;        (or headless: Unity.exe -batchmode -quit -projectPath <UnitySim>
;                      -executeMethod AIHWSim.EditorTools.BuildMenu.BuildRelease)
;      This writes UnitySim\Builds\Release\  (exe + _Data + UnityPlayer.dll + MonoBleedingEdge\).
;   2. Install Inno Setup (free): https://jrsoftware.org/isdl.php
;   3. Compile this script:
;        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" Installer\AIHWSim.iss
;      Output: UnitySim\Builds\Installer\AI-Hardware-Control-Sim-Setup.exe
;
; Saves/vehicles/tracks/telemetry are written to the per-user AppData\LocalLow
; folder at runtime (see Persistence/AppPaths.cs), so installing under Program
; Files is safe — no admin-only writes into the install directory.

#define AppName "AI Hardware Control Sim"
#define AppVersion "1.0"
#define AppExe "AI Hardware Control Sim.exe"
#define AppPublisher "AIHWSim"

[Setup]
; A fixed AppId ties upgrades/uninstalls together — keep this GUID stable.
AppId={{A7F3C2E1-5B94-4D6A-8E21-9C4B7D0F1A28}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\Builds\Installer
OutputBaseFilename=AI-Hardware-Control-Sim-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Pack the entire release player folder (exe, _Data, UnityPlayer.dll, MonoBleedingEdge),
; minus the Burst debug symbols folder Unity marks "DoNotShip".
Source: "..\Builds\Release\*"; DestDir: "{app}"; Excludes: "*_BurstDebugInformation_DoNotShip\*,*_BurstDebugInformation_DoNotShip"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
