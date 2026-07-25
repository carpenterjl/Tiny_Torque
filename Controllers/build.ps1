<#
    build.ps1 — configure + build the controller DLLs and drop them into Unity.

    Usage:
        ./build.ps1                     # configure (first run) + build Release
        ./build.ps1 -Clean              # wipe the build dir first
        ./build.ps1 -Config Debug
        ./build.ps1 -Target opus_controller   # build one DLL instead of all

    Toolchain: the script auto-detects, in this order:
      1. MSVC   (cl.exe via vswhere)     -> Visual Studio generator, -A x64
      2. mingw-w64 (64-bit gcc)          -> Ninja, or MinGW Makefiles
    Only a *64-bit* compiler is accepted: Unity loads plugins from
    Assets/Plugins/x86_64 and silently refuses a 32-bit DLL. The stock
    MinGW.org gcc that ships on many machines is 32-bit (-dumpmachine reports
    "mingw32"), so it is rejected here rather than producing an unloadable DLL.

    If neither is installed:
        winget install --id BrechtSanders.WinLibs.POSIX.UCRT --exact
    which bundles gcc, cmake, ninja and mingw32-make in one directory.

    Unity can hot-reload the DLLs at runtime (see NativeControllerLoader): it
    loads a shadow copy, so this script can overwrite them even while the
    editor is in Play mode.
#>
param(
    [string]$Config = "Release",
    [switch]$Clean,
    [string]$Target
)

$ErrorActionPreference = "Stop"
$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginDir  = Join-Path $here "..\UnitySim\Assets\Plugins\x86_64"

# ---------------------------------------------------------------- helpers --

function Find-Exe {
    param([string]$Name, [string[]]$Extra)
    $c = Get-Command $Name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in $Extra) {
        # Extra entries may be globs (winget package dirs carry a hash suffix).
        $hit = Get-Item $p -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$winlibsGlob = Join-Path $env:LOCALAPPDATA `
    "Microsoft\WinGet\Packages\BrechtSanders.WinLibs*\mingw64\bin"

$cmakeExe = Find-Exe "cmake" @(
    "C:\Program Files\CMake\bin\cmake.exe",
    (Join-Path $winlibsGlob "cmake.exe"))
if (-not $cmakeExe) {
    throw "cmake not found. Install CMake, or run: winget install --id BrechtSanders.WinLibs.POSIX.UCRT --exact"
}

# ------------------------------------------------------------- toolchain --

$useMsvc = $false
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath 2>$null
    if ($vsPath) { $useMsvc = $true }
}

$gccExe = $null
if (-not $useMsvc) {
    # Prefer the explicitly-triplet-named driver; fall back to plain gcc, but
    # only accept it once -dumpmachine confirms a 64-bit target.
    foreach ($cand in @("x86_64-w64-mingw32-gcc", "gcc")) {
        $exe = Find-Exe $cand @((Join-Path $winlibsGlob "$cand.exe"))
        if (-not $exe) { continue }
        $triple = (& $exe -dumpmachine 2>$null)
        if ($triple -like "x86_64*") { $gccExe = $exe; break }
        Write-Host "Skipping $exe (targets '$triple', not 64-bit)" -ForegroundColor DarkYellow
    }
    if (-not $gccExe) {
        throw @"
No 64-bit C compiler found.
  - MSVC:  install Visual Studio Build Tools with the 'Desktop development with C++' workload, or
  - MinGW: winget install --id BrechtSanders.WinLibs.POSIX.UCRT --exact
A 32-bit gcc cannot produce a plugin Unity will load.
"@
    }
}

# Separate build dirs per toolchain: CMake caches the compiler, so reusing one
# directory across generators fails with a confusing cache mismatch.
$buildDir = Join-Path $here ($(if ($useMsvc) { "build\msvc" } else { "build\mingw" }))

if ($Clean -and (Test-Path $buildDir)) {
    Write-Host "Cleaning $buildDir" -ForegroundColor Yellow
    Remove-Item -Recurse -Force $buildDir
}

$configureArgs = @("-S", $here, "-B", $buildDir)
if ($useMsvc) {
    Write-Host "Toolchain: MSVC ($vsPath)" -ForegroundColor Cyan
    $configureArgs += @("-A", "x64")   # match Unity's Plugins/x86_64
} else {
    $gccBin = Split-Path $gccExe
    Write-Host "Toolchain: mingw-w64 ($gccExe)" -ForegroundColor Cyan
    # The compiler's own bin dir must be on PATH so it finds ar/ld/windres.
    $env:PATH = "$gccBin;$env:PATH"

    $ninja = Find-Exe "ninja" @((Join-Path $gccBin "ninja.exe"))
    if ($ninja) {
        $configureArgs += @("-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=$ninja")
    } else {
        $make = Find-Exe "mingw32-make" @((Join-Path $gccBin "mingw32-make.exe"))
        if (-not $make) { throw "Found gcc at $gccExe but neither ninja nor mingw32-make." }
        $configureArgs += @("-G", "MinGW Makefiles", "-DCMAKE_MAKE_PROGRAM=$make")
    }
    # Single-config generators need the build type at configure time.
    $configureArgs += @("-DCMAKE_C_COMPILER=$($gccExe -replace '\\','/')",
                        "-DCMAKE_BUILD_TYPE=$Config")
}

if (-not (Test-Path (Join-Path $buildDir "CMakeCache.txt"))) {
    Write-Host "Configuring..." -ForegroundColor Cyan
    & $cmakeExe @configureArgs
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed." }
}

Write-Host "Building ($Config)..." -ForegroundColor Cyan
$buildArgs = @("--build", $buildDir)
if ($useMsvc) { $buildArgs += @("--config", $Config) }   # multi-config only
if ($Target)  { $buildArgs += @("--target", $Target) }
& $cmakeExe @buildArgs
if ($LASTEXITCODE -ne 0) { throw "CMake build failed." }

# ---------------------------------------------------------------- report --

if (Test-Path $pluginDir) {
    $dlls = Get-ChildItem $pluginDir -Filter '*.dll' -ErrorAction SilentlyContinue
    if ($dlls) {
        Write-Host "`nDLLs in $((Resolve-Path $pluginDir).Path):" -ForegroundColor Green
        foreach ($d in $dlls) {
            Write-Host ("  {0,-32} {1,8:N0} bytes  {2}" -f $d.Name, $d.Length, $d.LastWriteTime) -ForegroundColor Green
        }
        Write-Host "In Unity: press 'Reload Controller DLL' (or restart Play) to load them." -ForegroundColor Green
    } else {
        Write-Warning "Build succeeded but no DLLs were found in $pluginDir"
    }
} else {
    Write-Warning "Plugin directory not found: $pluginDir"
}
