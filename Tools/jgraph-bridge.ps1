<#
.SYNOPSIS
    Opens figures exported by the Tiny Torque tools in JGraph, automatically.

.DESCRIPTION
    The browser pages cannot launch a process, and JGraph has no socket or pipe
    to talk to, so the handover is a folder: the pages write a .m script plus its
    .csv, and this script watches for them and runs JGraph on each new script.

    Leave it running in a terminal, point a page's "Open in JGraph" button at
    the same folder once, and figures pop up as you click.

    Without this you can still do everything by hand — the pages tell you the
    exact command — this only removes the copy-paste.

.PARAMETER Path
    Folder to watch. Defaults to Tools\jgraph-out beside this script, created if
    it does not exist.

.PARAMETER JGraph
    Path to jgraph.exe. Found automatically if it is on PATH or sitting in the
    usual build output of a JGraph source tree.

.PARAMETER Once
    Process whatever is already in the folder, then exit instead of watching.

.PARAMETER KeepFiles
    Do not move handled scripts into a "done" subfolder.

.PARAMETER DryRun
    Report what would be run without launching JGraph. Use it to check the
    folder and the executable are found before relying on the watcher.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\jgraph-bridge.ps1

.EXAMPLE
    .\Tools\jgraph-bridge.ps1 -Path D:\figures -JGraph "C:\JGraph\jgraph.exe"
#>
[CmdletBinding()]
param(
    [string] $Path,
    [string] $JGraph,
    [switch] $Once,
    [switch] $KeepFiles,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

# ---- locate the watch folder ------------------------------------------------

if (-not $Path) {
    $Path = Join-Path $PSScriptRoot 'jgraph-out'
}
if (-not (Test-Path $Path)) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    Write-Host "Created watch folder: $Path" -ForegroundColor DarkGray
}
$Path = (Resolve-Path $Path).Path

# ---- locate jgraph.exe ------------------------------------------------------

function Find-JGraph {
    param([string] $Explicit)

    if ($Explicit) {
        if (Test-Path $Explicit) { return (Resolve-Path $Explicit).Path }
        throw "No jgraph.exe at '$Explicit'."
    }

    $onPath = Get-Command 'jgraph' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # The usual spot in a JGraph source tree, Release before Debug.
    $roots = @(
        'E:\EE Projects\JGraph',
        (Join-Path (Split-Path $PSScriptRoot -Parent) '..\JGraph')
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($cfg in @('Release', 'Debug')) {
            $candidate = Join-Path $root "src\JGraph.Cli\bin\$cfg\net8.0\jgraph.exe"
            if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
        }
    }
    return $null
}

$exe = Find-JGraph -Explicit $JGraph
if (-not $exe) {
    Write-Host ""
    Write-Host "Could not find jgraph.exe." -ForegroundColor Yellow
    Write-Host "  Put it on PATH, or pass -JGraph <path to jgraph.exe>."
    Write-Host "  The pages still work without it — they draw their own plots, and the"
    Write-Host "  exported .m and .csv are perfectly good on their own."
    Write-Host ""
    exit 1
}
Write-Host "JGraph:  $exe" -ForegroundColor DarkGray
Write-Host "Watching: $Path" -ForegroundColor DarkGray

# ---- run one script ---------------------------------------------------------

$doneDir = Join-Path $Path 'done'

function Invoke-Figure {
    param([string] $ScriptPath)

    $name = Split-Path $ScriptPath -Leaf
    # The page writes the .csv and the .m separately; if we pounce the moment the
    # script appears its data may still be arriving. Wait for the size to settle.
    for ($i = 0; $i -lt 20; $i++) {
        $csv = [IO.Path]::ChangeExtension($ScriptPath, '.csv')
        if (Test-Path $csv) {
            $a = (Get-Item $csv).Length
            Start-Sleep -Milliseconds 120
            $b = (Get-Item $csv).Length
            if ($a -eq $b -and $a -gt 0) { break }
        } else {
            Start-Sleep -Milliseconds 120
        }
    }

    $args = @('-batch', "`"$name`"", '-showfigures', '-sd', "`"$Path`"")
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $name) -ForegroundColor Cyan

    if ($DryRun) {
        Write-Host ("  would run: {0} {1}" -f $exe, ($args -join ' ')) -ForegroundColor DarkGray
        return
    }

    # -showfigures opens a real interactive window and the process lives until
    # the user closes it. It must get its own console: sharing ours would keep
    # our stdout pipe open for as long as the figure is on screen, which makes
    # the watcher look hung to anything reading our output.
    $proc = Start-Process -FilePath $exe -ArgumentList $args -PassThru
    if (-not $proc) {
        Write-Host "  could not start JGraph" -ForegroundColor Red
        return
    }

    if (-not $KeepFiles) {
        # Move the script aside so a rewrite of the same name fires again, but
        # leave the CSV where JGraph is reading it from.
        if (-not (Test-Path $doneDir)) { New-Item -ItemType Directory -Path $doneDir -Force | Out-Null }
        Start-Sleep -Milliseconds 400
        try {
            Move-Item -LiteralPath $ScriptPath -Destination (Join-Path $doneDir $name) -Force
        } catch {
            Write-Host "  (left $name in place: $($_.Exception.Message))" -ForegroundColor DarkGray
        }
    }
}

# ---- catch up on anything already sitting there -----------------------------

Get-ChildItem -LiteralPath $Path -Filter '*.m' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime |
    ForEach-Object { Invoke-Figure $_.FullName }

if ($Once) {
    Write-Host "Done." -ForegroundColor Green
    exit 0
}

# ---- watch -------------------------------------------------------------------

Write-Host ""
Write-Host "Ready. Click 'Open in JGraph' on any Tiny Torque tool page and point it here." -ForegroundColor Green
Write-Host "Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

$watcher = New-Object System.IO.FileSystemWatcher
$watcher.Path = $Path
$watcher.Filter = '*.m'
$watcher.IncludeSubdirectories = $false
$watcher.NotifyFilter = [IO.NotifyFilters]::FileName -bor [IO.NotifyFilters]::LastWrite

# FileSystemWatcher fires from a background thread, and calling back into
# PowerShell from there is unreliable — so the event handler only records the
# path and the main loop does the work.
$queue = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
$action = {
    $q = $Event.MessageData
    $q.Enqueue($Event.SourceEventArgs.FullPath)
}
$subs = @(
    Register-ObjectEvent -InputObject $watcher -EventName Created -Action $action -MessageData $queue
    Register-ObjectEvent -InputObject $watcher -EventName Changed -Action $action -MessageData $queue
)
$watcher.EnableRaisingEvents = $true

$recent = @{}
try {
    while ($true) {
        Start-Sleep -Milliseconds 250
        $file = $null
        while ($queue.TryDequeue([ref] $file)) {
            if (-not (Test-Path $file)) { continue }
            # Created and Changed both fire for one write; debounce by path.
            $now = Get-Date
            if ($recent.ContainsKey($file) -and ($now - $recent[$file]).TotalMilliseconds -lt 1500) { continue }
            $recent[$file] = $now
            Invoke-Figure $file
        }
    }
} finally {
    $watcher.EnableRaisingEvents = $false
    $subs | ForEach-Object { Unregister-Event -SourceIdentifier $_.Name -ErrorAction SilentlyContinue }
    $watcher.Dispose()
    Write-Host "Stopped." -ForegroundColor DarkGray
}
