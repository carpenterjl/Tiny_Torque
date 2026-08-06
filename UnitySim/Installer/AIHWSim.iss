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
; folder at runtime (see Persistence/AppPaths.cs).
;
; The install directory, however, is NOT read-only here, which is why this
; installs per-user rather than into Program Files. "Build & Reload" compiles
; the player's C controller in {app}\Controllers\build and drops the DLL into
; {app}\<name>_Data\Plugins\x86_64 — both inside the install directory, and
; neither writable under Program Files without elevation. PrivilegesRequired
; below turns {autopf} into %LocalAppData%\Programs, which is writable, prompts
; for nothing, and is what other self-modifying tools do. Reverting it would
; not break the game; it would break writing controllers, and only for people
; who installed rather than unzipped.

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
; Per-user: {autopf} becomes %LocalAppData%\Programs, so the game can compile
; controllers into its own folder. See the note at the top of this file.
PrivilegesRequired=lowest
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
; Controllers\build is excluded because testing "Build & Reload" in the release
; folder leaves a CMake cache there, and a CMakeCache.txt records the absolute
; paths it was generated for — shipping one hands every player a cache stamped
; with a path from the build machine.
Source: "..\Builds\Release\*"; DestDir: "{app}"; Excludes: "*_BurstDebugInformation_DoNotShip\*,*_BurstDebugInformation_DoNotShip,Controllers\build\*,Controllers\build"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
