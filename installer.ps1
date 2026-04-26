#Requires -RunAsAdministrator
<#
.SYNOPSIS
    CpuMon interactive installer / updater / uninstaller.

.PARAMETER ClientExe
    Explicit path to cpumon.client.exe (skips auto-detection).

.PARAMETER ServerExe
    Explicit path to cpumon.server.exe (skips auto-detection).

.EXAMPLE
    .\installer.ps1
    .\installer.ps1 -ClientExe ".\dist\client\cpumon.client.exe"
#>
param(
    [string]$ClientExe = '',
    [string]$ServerExe = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Constants ─────────────────────────────────────────────────────────────────
$InstallRoot    = 'C:\Program Files\CpuMon'
$ClientDir      = "$InstallRoot\Client"
$ServerDir      = "$InstallRoot\Server"
$ServiceName    = 'CpuMonClient'
$AgentTask      = 'CpuMonAgent'
$StartMenuGroup = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\CpuMon"

# ── UI helpers ────────────────────────────────────────────────────────────────
function Write-Banner {
    $installed = ''
    if (Test-Path "$ClientDir\cpumon.client.exe") {
        $v = (Get-Item "$ClientDir\cpumon.client.exe").VersionInfo.FileVersion
        if ($v) { $installed = "  installed v$v" }
    }
    Write-Host ''
    Write-Host '  ╔════════════════════════════════════════╗' -ForegroundColor DarkCyan
    Write-Host '  ║       CpuMon  Installer                ║' -ForegroundColor Cyan
    if ($installed) {
    Write-Host "  ║  $($installed.PadRight(38))║" -ForegroundColor DarkGray
    }
    Write-Host '  ╚════════════════════════════════════════╝' -ForegroundColor DarkCyan
    Write-Host ''
}

function Read-Choice([string]$title, [string[]]$opts) {
    Write-Host "  $title" -ForegroundColor White
    for ($i = 0; $i -lt $opts.Length; $i++) {
        Write-Host "    [$($i+1)] $($opts[$i])" -ForegroundColor Cyan
    }
    while ($true) {
        $raw = (Read-Host '  >').Trim()
        $n   = 0
        if ([int]::TryParse($raw, [ref]$n) -and $n -ge 1 -and $n -le $opts.Length) {
            Write-Host ''
            return $opts[$n - 1]
        }
        Write-Host "  Please enter a number from 1 to $($opts.Length)." -ForegroundColor Yellow
    }
}

function Read-Optional([string]$prompt) {
    Write-Host "  $prompt  " -ForegroundColor DarkGray -NoNewline
    Write-Host '[Enter to skip]' -ForegroundColor DarkGray
    return (Read-Host '  >').Trim()
}

function Confirm-Action([string]$msg) {
    Write-Host "  $msg  [Y/N]" -ForegroundColor Yellow -NoNewline
    $k = (Read-Host ' ').Trim()
    Write-Host ''
    return $k -match '^[Yy]'
}

function Write-Step([string]$m) { Write-Host "  ► $m" -ForegroundColor Cyan }
function Write-Ok([string]$m)   { Write-Host "  ✓ $m" -ForegroundColor Green }
function Write-Warn([string]$m) { Write-Host "  ⚠ $m" -ForegroundColor Yellow }
function Write-Err([string]$m)  { Write-Host "  ✕ $m" -ForegroundColor Red }

# ── Exe resolution ────────────────────────────────────────────────────────────
function Resolve-Exe([string]$explicit, [string]$name) {
    if ($explicit -and (Test-Path $explicit)) { return (Resolve-Path $explicit).Path }
    foreach ($c in @(
        (Join-Path $PSScriptRoot "dist\$($name -replace 'cpumon\.','')\$name"),
        (Join-Path $PSScriptRoot "$($name -replace 'cpumon\.','')\$name"),
        (Join-Path $PSScriptRoot $name)
    )) { if (Test-Path $c) { return $c } }

    # Try auto-build if build.ps1 is present
    $build = Join-Path $PSScriptRoot 'build.ps1'
    if (Test-Path $build) {
        $flag = if ($name -like '*client*') { '-ClientOnly' } else { '-ServerOnly' }
        Write-Warn "$name not found — running build.ps1 $flag ..."
        & $build $flag
        $built = Join-Path $PSScriptRoot "dist\$($name -replace 'cpumon\.','')\$name"
        if (Test-Path $built) { return $built }
    }
    throw "Cannot locate $name. Run build.ps1 first, or pass -ClientExe / -ServerExe."
}

# ── Shortcut helper ───────────────────────────────────────────────────────────
function New-Shortcut([string]$lnk, [string]$target, [string]$args = '', [string]$desc = '', [string]$workDir = '') {
    $dir = Split-Path $lnk
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $wsh = New-Object -ComObject WScript.Shell
    $sc  = $wsh.CreateShortcut($lnk)
    $sc.TargetPath      = $target
    $sc.Arguments       = $args
    $sc.Description     = $desc
    $sc.WorkingDirectory = $workDir
    $sc.Save()
}

# ── Service helpers ───────────────────────────────────────────────────────────
function Stop-CpuMonService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -eq 'Stopped') { return }
    Write-Step 'Stopping service...'
    Stop-Service -Name $ServiceName -Force
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    Write-Ok 'Service stopped.'
}

function Start-CpuMonService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Warn "Service '$ServiceName' not registered."; return }
    if ($svc.Status -eq 'Running') { Write-Ok 'Service already running.'; return }
    Write-Step 'Starting service...'
    Start-Service -Name $ServiceName
    $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
    Write-Ok "Service running."
}

# ── Install ───────────────────────────────────────────────────────────────────
function Install-Client([string]$mode) {
    $src = Resolve-Exe $ClientExe 'cpumon.client.exe'
    $ver = (Get-Item $src).VersionInfo.FileVersion
    Write-Step "Source: $src  (v$ver)"

    if ($mode -eq 'Service') {
        $ip    = Read-Optional 'Server IP  (leave blank for auto-discovery via UDP beacon)'
        $token = Read-Optional 'Auth token (leave blank — server will show one on first run)'

        $iArgs = @('--install')
        if ($ip)    { $iArgs += '--server-ip'; $iArgs += $ip }
        if ($token) { $iArgs += '--token';     $iArgs += $token }

        Write-Step 'Registering service and agent task...'
        & $src @iArgs
        if ($LASTEXITCODE -ne 0) { throw "--install exited with code $LASTEXITCODE" }

        Start-CpuMonService
        Write-Ok "Client service installed to $ClientDir"

    } else {
        # Standalone daemon
        $ip = Read-Optional 'Server IP  (leave blank for auto-discovery)'

        if (-not (Test-Path $ClientDir)) { New-Item -ItemType Directory -Path $ClientDir | Out-Null }
        Write-Step "Copying to $ClientDir..."
        Copy-Item $src $ClientDir -Force
        Write-Ok 'Files copied.'

        $daemonArgs = '--daemon'
        if ($ip) { $daemonArgs += " --server-ip $ip" }

        New-Shortcut "$StartMenuGroup\CpuMon Client.lnk" `
            "$ClientDir\cpumon.client.exe" $daemonArgs `
            'CpuMon telemetry agent' $ClientDir
        Write-Ok 'Start Menu shortcut created.'

        if (Confirm-Action 'Start automatically at Windows logon?') {
            $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
            Set-ItemProperty $runKey -Name 'CpuMonClient' `
                -Value "`"$ClientDir\cpumon.client.exe`" $daemonArgs"
            Write-Ok 'Added to user startup (HKCU Run).'
        }

        Write-Ok "Client standalone installed to $ClientDir"
    }
}

function Install-Server {
    $src = Resolve-Exe $ServerExe 'cpumon.server.exe'
    $ver = (Get-Item $src).VersionInfo.FileVersion
    Write-Step "Source: $src  (v$ver)"

    if (-not (Test-Path $ServerDir)) { New-Item -ItemType Directory -Path $ServerDir | Out-Null }
    Write-Step "Copying to $ServerDir..."
    Copy-Item $src $ServerDir -Force
    Write-Ok 'Files copied.'

    New-Shortcut "$StartMenuGroup\CpuMon Server.lnk" `
        "$ServerDir\cpumon.server.exe" '' `
        'CpuMon Server Dashboard' $ServerDir
    Write-Ok 'Start Menu shortcut created.'

    Write-Ok "Server installed to $ServerDir"

    if (Confirm-Action 'Launch server now?') {
        Start-Process "$ServerDir\cpumon.server.exe" -WorkingDirectory $ServerDir
        Write-Ok 'Server launched.'
    }
}

# ── Update ────────────────────────────────────────────────────────────────────
function Update-Client([string]$mode) {
    $dest = "$ClientDir\cpumon.client.exe"
    if (-not (Test-Path $dest)) {
        Write-Warn "No existing install found at $dest — switching to Install."
        Install-Client $mode; return
    }

    $src    = Resolve-Exe $ClientExe 'cpumon.client.exe'
    $oldVer = (Get-Item $dest).VersionInfo.FileVersion
    $newVer = (Get-Item $src).VersionInfo.FileVersion
    Write-Step "Updating client  v$oldVer → v$newVer"

    if ($mode -eq 'Service') {
        Stop-CpuMonService
        Write-Step 'Replacing exe...'
        Copy-Item $src $dest -Force
        Write-Ok 'Exe replaced.'
        Start-CpuMonService
    } else {
        $proc = Get-Process -Name 'cpumon.client' -ErrorAction SilentlyContinue
        if ($proc) { Write-Warn 'cpumon.client.exe is running — close it first to avoid file-lock errors.' }
        Write-Step 'Replacing exe...'
        Copy-Item $src $dest -Force
        Write-Ok "Client updated to v$newVer"
    }
}

function Update-Server {
    $dest = "$ServerDir\cpumon.server.exe"
    if (-not (Test-Path $dest)) {
        Write-Warn "No existing install found at $dest — switching to Install."
        Install-Server; return
    }

    $src    = Resolve-Exe $ServerExe 'cpumon.server.exe'
    $oldVer = (Get-Item $dest).VersionInfo.FileVersion
    $newVer = (Get-Item $src).VersionInfo.FileVersion
    Write-Step "Updating server  v$oldVer → v$newVer"

    $proc = Get-Process -Name 'cpumon.server' -ErrorAction SilentlyContinue
    if ($proc) { Write-Warn 'cpumon.server.exe is running — close it first to avoid file-lock errors.' }

    Copy-Item $src $dest -Force
    Write-Ok "Server updated to v$newVer"
}

# ── Uninstall ─────────────────────────────────────────────────────────────────
function Uninstall-Client {
    if (-not (Confirm-Action "Remove CpuMon client from this machine?")) {
        Write-Host '  Cancelled.' -ForegroundColor DarkGray; return
    }

    $installedExe = "$ClientDir\cpumon.client.exe"
    if (Test-Path $installedExe) {
        Write-Step 'Running --uninstall...'
        & $installedExe --uninstall
        Write-Ok 'Service and agent task removed.'
    } else {
        Write-Warn 'Installed exe not found — attempting manual cleanup.'
        try { Stop-CpuMonService } catch { }
        sc.exe delete $ServiceName 2>$null | Out-Null
        schtasks.exe /delete /tn $AgentTask /f 2>$null | Out-Null
        Write-Ok 'Manual cleanup done.'
    }

    # Remove startup Run key if present
    try {
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        if (Get-ItemProperty $runKey -Name 'CpuMonClient' -ErrorAction SilentlyContinue) {
            Remove-ItemProperty $runKey -Name 'CpuMonClient'
            Write-Ok 'Removed startup entry.'
        }
    } catch { }

    if (Test-Path $ClientDir) {
        Remove-Item $ClientDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Ok "Removed $ClientDir"
    }

    $lnk = "$StartMenuGroup\CpuMon Client.lnk"
    if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Ok 'Removed Start Menu shortcut.' }

    # Remove Start Menu group folder if now empty
    if ((Test-Path $StartMenuGroup) -and -not (Get-ChildItem $StartMenuGroup -ErrorAction SilentlyContinue)) {
        Remove-Item $StartMenuGroup -Force -ErrorAction SilentlyContinue
    }

    Write-Ok 'Client uninstalled.'
}

function Uninstall-Server {
    if (-not (Confirm-Action "Remove CpuMon server from this machine?")) {
        Write-Host '  Cancelled.' -ForegroundColor DarkGray; return
    }

    $proc = Get-Process -Name 'cpumon.server' -ErrorAction SilentlyContinue
    if ($proc) {
        if (Confirm-Action 'cpumon.server.exe is running. Stop it now?') {
            Stop-Process -Name 'cpumon.server' -Force
            Write-Ok 'Server process stopped.'
        }
    }

    if (Test-Path $ServerDir) {
        Remove-Item $ServerDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Ok "Removed $ServerDir"
    }

    $lnk = "$StartMenuGroup\CpuMon Server.lnk"
    if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Ok 'Removed Start Menu shortcut.' }

    if ((Test-Path $StartMenuGroup) -and -not (Get-ChildItem $StartMenuGroup -ErrorAction SilentlyContinue)) {
        Remove-Item $StartMenuGroup -Force -ErrorAction SilentlyContinue
    }

    Write-Ok 'Server uninstalled.'
}

# ── Wizard ────────────────────────────────────────────────────────────────────
Write-Banner

$action = Read-Choice 'Step 1 — What would you like to do?' @(
    'Install',
    'Update',
    'Uninstall'
)

$component = Read-Choice 'Step 2 — Which component?' @(
    'Client  (sends telemetry, accepts remote control)',
    'Server  (dashboard, receives connections)'
)
$component = if ($component -like 'Client*') { 'Client' } else { 'Server' }

$mode = $null
if ($component -eq 'Client' -and $action -ne 'Uninstall') {
    $modeRaw = Read-Choice 'Step 3 — How should the client run?' @(
        'Service     (Windows service — starts on boot, no login required)',
        'Standalone  (copy files only — run manually or via startup shortcut)'
    )
    $mode = if ($modeRaw -like 'Service*') { 'Service' } else { 'Standalone' }
}

Write-Host '  ──────────────────────────────────────────' -ForegroundColor DarkGray
Write-Host ''

try {
    switch ("$action-$component") {
        'Install-Client'   { Install-Client $mode }
        'Install-Server'   { Install-Server }
        'Update-Client'    { Update-Client $mode }
        'Update-Server'    { Update-Server }
        'Uninstall-Client' { Uninstall-Client }
        'Uninstall-Server' { Uninstall-Server }
    }
    Write-Host ''
    Write-Host '  Done.' -ForegroundColor Green
    Write-Host ''
} catch {
    Write-Host ''
    Write-Err "Error: $_"
    Write-Host ''
    exit 1
}
