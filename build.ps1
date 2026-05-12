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
    if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
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

function Copy-LinuxClient([string]$src, [string]$out, [string]$ver) {
    if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    New-Item -ItemType Directory -Path $out | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $out -Recurse -Force

    $py = Join-Path $out 'cpumon.py'
    if (Test-Path $py) {
        $content = Get-Content -LiteralPath $py -Raw
        $content = $content -replace '(?m)^VERSION\s*=\s*"[^"]*"', "VERSION     = `"$ver-linux`""
        Set-Content -LiteralPath $py -Value $content -NoNewline -Encoding UTF8
    }
}

Write-Host ''
Write-Host '  +------------------------------+' -ForegroundColor DarkCyan
Write-Host '  |      CpuMon  Build           |' -ForegroundColor Cyan
Write-Host '  +------------------------------+' -ForegroundColor DarkCyan
Write-Host ''

$clientProj = Join-Path $PSScriptRoot 'cpumon.client\cpumon.client.csproj'
$serverProj = Join-Path $PSScriptRoot 'cpumon.server\cpumon.server.csproj'
$testsProj  = Join-Path $PSScriptRoot 'cpumon.tests\cpumon.tests.csproj'
$clientExe  = Join-Path $OutDir 'client\cpumon.client.exe'
$serverExe  = Join-Path $OutDir 'server\cpumon.server.exe'

$builtClient = $false
$builtServer = $false

if (Test-Path $testsProj) {
    Run-SmokeTests $testsProj
}

if (-not $ServerOnly) {
    if (-not (Test-Path $clientProj)) { throw "Not found: $clientProj" }
    Publish-Project $clientProj (Join-Path $OutDir 'client')
    $builtClient = $true
}

if (-not $ClientOnly) {
    if (-not (Test-Path $serverProj)) { throw "Not found: $serverProj" }
    Publish-Project $serverProj (Join-Path $OutDir 'server')
    $builtServer = $true
}

Write-Host ''

# Version comes from an artifact we built this invocation, never a stale one.
$ver = $null
if     ($builtClient -and (Test-Path $clientExe)) { $ver = (Get-Item $clientExe).VersionInfo.FileVersion }
elseif ($builtServer -and (Test-Path $serverExe)) { $ver = (Get-Item $serverExe).VersionInfo.FileVersion }
if ($ver) { $ver = $ver -replace '\.0$', '' }

if ($ver) { Write-Host "  Version : $ver" -ForegroundColor Yellow }
Write-Host "  Output  : $OutDir" -ForegroundColor DarkGray

if ($ver) {
    Write-Step "Packaging zips..."
    if ($builtClient) {
        Compress-Archive -Path (Join-Path $OutDir 'client\*') -DestinationPath (Join-Path $OutDir "cpumon-client-$ver.zip") -Force
    }
    if ($builtServer) {
        Compress-Archive -Path (Join-Path $OutDir 'server\*') -DestinationPath (Join-Path $OutDir "cpumon-server-$ver.zip") -Force
    }
    # Linux client is bundled only on a full build, since it travels with both halves.
    if ($builtClient -and $builtServer) {
        $linuxSrc = Join-Path $PSScriptRoot 'cpumon.linux'
        if (Test-Path $linuxSrc) {
            $linuxOut = Join-Path $OutDir 'linux'
            Copy-LinuxClient $linuxSrc $linuxOut $ver
            Compress-Archive -Path (Join-Path $linuxOut '*') -DestinationPath (Join-Path $OutDir "cpumon-linux-$ver.zip") -Force
        }
    }
    Write-Ok
}
Write-Host ''
