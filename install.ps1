#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Install or uninstall the CpuMon Windows service.

.DESCRIPTION
    Builds (if needed), copies binaries to C:\ProgramData\CpuMon, then delegates to
    the exe's built-in --install / --uninstall flags which handle sc.exe and schtasks.

.PARAMETER Uninstall
    Remove the service and agent scheduled task instead of installing.

.PARAMETER ServerIp
    Optional: hard-code the server IP (skips UDP beacon discovery).

.PARAMETER Token
    Optional: pre-supply the auth token.

.PARAMETER ExePath
    Optional: path to an already-built cpumon.client.exe.
    Defaults to .\publish\cpumon.client.exe, or triggers a dotnet publish if absent.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -ServerIp 192.168.1.10 -Token mytoken
    .\install.ps1 -Uninstall
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Uninstall,
    [string]$ServerIp,
    [string]$Token,
    [string]$ExePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$InstallDir = Join-Path $env:ProgramData 'CpuMon'
$ServiceName = 'CpuMon'

function Write-Step([string]$msg) { Write-Host "  $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Write-Err([string]$msg)  { Write-Host "  ERR $msg" -ForegroundColor Red }

# ── Locate or build the exe ───────────────────────────────────────────────────
function Resolve-Exe {
    if ($ExePath) {
        if (-not (Test-Path $ExePath)) { throw "ExePath not found: $ExePath" }
        return (Resolve-Path $ExePath).Path
    }

    $candidate = Join-Path $PSScriptRoot 'publish\cpumon.client.exe'
    if (Test-Path $candidate) { return $candidate }

    Write-Step "No pre-built exe found — running dotnet publish..."
    $proj = Join-Path $PSScriptRoot 'cpumon.client\cpumon.client.csproj'
    if (-not (Test-Path $proj)) { throw "Project not found: $proj" }
    $outDir = Join-Path $PSScriptRoot 'publish'
    dotnet publish $proj -c Release -o $outDir --self-contained false | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
    return $candidate
}

# ── Uninstall ─────────────────────────────────────────────────────────────────
if ($Uninstall) {
    Write-Host "`nUninstalling CpuMon service..." -ForegroundColor Yellow

    $installedExe = Join-Path $InstallDir 'cpumon.client.exe'
    if (Test-Path $installedExe) {
        Write-Step "Running --uninstall..."
        & $installedExe --uninstall
        Write-Ok "Service and agent task removed."
    } else {
        # Fallback: manual removal
        Write-Step "Installed exe not found, attempting manual removal..."
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc) {
            if ($svc.Status -eq 'Running') { Stop-Service -Name $ServiceName -Force }
            sc.exe delete $ServiceName | Out-Null
            Write-Ok "Service deleted."
        }
        schtasks.exe /delete /tn 'CpuMonAgent' /f 2>$null | Out-Null
        Write-Ok "Agent task deleted (if it existed)."
    }

    if (Test-Path $InstallDir) {
        Remove-Item $InstallDir -Recurse -Force
        Write-Ok "Removed $InstallDir"
    }

    Write-Host "`nUninstall complete." -ForegroundColor Green
    exit 0
}

# ── Install ───────────────────────────────────────────────────────────────────
Write-Host "`nInstalling CpuMon service..." -ForegroundColor Yellow

$srcExe = Resolve-Exe
$srcDir = Split-Path $srcExe

# Copy all files from the source directory to the install dir
Write-Step "Copying files to $InstallDir..."
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir | Out-Null }
Copy-Item -Path (Join-Path $srcDir '*') -Destination $InstallDir -Recurse -Force
Write-Ok "Files copied."

# Copy any .pfx or .json config files from the script root (optional — skip if absent)
foreach ($cfg in @('cpumon.pfx', 'approved_clients.json', 'client_auth.json')) {
    $src = Join-Path $PSScriptRoot $cfg
    if (Test-Path $src) {
        Copy-Item $src $InstallDir -Force
        Write-Ok "Copied config: $cfg"
    }
}

$destExe = Join-Path $InstallDir 'cpumon.client.exe'

# Build --install argument list
$installArgs = @('--install')
if ($ServerIp) { $installArgs += '--server-ip'; $installArgs += $ServerIp }
if ($Token)    { $installArgs += '--token';     $installArgs += $Token    }

Write-Step "Running --install (registers service + agent task)..."
& $destExe @installArgs
if ($LASTEXITCODE -ne 0) { Write-Err "Install returned exit code $LASTEXITCODE"; exit $LASTEXITCODE }
Write-Ok "Service registered."

# Start the service
Write-Step "Starting service..."
Start-Service -Name $ServiceName
Write-Ok "Service started."

$svc = Get-Service -Name $ServiceName
Write-Host "`nService status: $($svc.Status)" -ForegroundColor $(if ($svc.Status -eq 'Running') { 'Green' } else { 'Yellow' })
Write-Host "Install complete.  Exe: $destExe`n" -ForegroundColor Green
