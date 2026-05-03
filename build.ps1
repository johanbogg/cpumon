<#
.SYNOPSIS
    Builds CpuMon and places output in dist\

.PARAMETER ClientOnly   Build client only
.PARAMETER ServerOnly   Build server only
.PARAMETER OutDir       Output directory (default: .\dist)

.EXAMPLE
    .\build.ps1
    .\build.ps1 -ClientOnly
    .\build.ps1 -OutDir C:\Temp\CpuMon
#>
param(
    [switch]$ClientOnly,
    [switch]$ServerOnly,
    [string]$OutDir = (Join-Path $PSScriptRoot 'dist')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "  $msg" -ForegroundColor Cyan -NoNewline }
function Write-Ok                 { Write-Host ' OK'    -ForegroundColor Green }
function Write-Fail               { Write-Host ' FAIL'  -ForegroundColor Red }

function Publish-Project([string]$proj, [string]$out) {
    Write-Step "Publishing $(Split-Path $proj -Leaf)..."
    $log = dotnet publish $proj -c Release -r win-x64 --no-self-contained -o $out --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Fail
        $log | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkRed }
        throw "Build failed: $proj"
    }
    Write-Ok
}

function Run-SmokeTests([string]$proj) {
    Write-Step "Running smoke tests..."
    $log = dotnet run --project $proj -c Release --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Fail
        $log | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkRed }
        throw "Smoke tests failed: $proj"
    }
    Write-Ok
}

Write-Host ''
Write-Host '  +------------------------------+' -ForegroundColor DarkCyan
Write-Host '  |      CpuMon  Build           |' -ForegroundColor Cyan
Write-Host '  +------------------------------+' -ForegroundColor DarkCyan
Write-Host ''

$clientProj = Join-Path $PSScriptRoot 'cpumon.client\cpumon.client.csproj'
$serverProj = Join-Path $PSScriptRoot 'cpumon.server\cpumon.server.csproj'
$testsProj = Join-Path $PSScriptRoot 'cpumon.tests\cpumon.tests.csproj'

if (Test-Path $testsProj) {
    Run-SmokeTests $testsProj
}

if (-not $ServerOnly) {
    if (-not (Test-Path $clientProj)) { throw "Not found: $clientProj" }
    Publish-Project $clientProj (Join-Path $OutDir 'client')
}

if (-not $ClientOnly) {
    if (-not (Test-Path $serverProj)) { throw "Not found: $serverProj" }
    Publish-Project $serverProj (Join-Path $OutDir 'server')
}

Write-Host ''

$clientExe = Join-Path $OutDir 'client\cpumon.client.exe'
if (Test-Path $clientExe) {
    $ver = (Get-Item $clientExe).VersionInfo.FileVersion
    Write-Host "  Version : $ver" -ForegroundColor Yellow
}
Write-Host "  Output  : $OutDir" -ForegroundColor DarkGray
Write-Host ''
