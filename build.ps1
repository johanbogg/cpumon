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

if (Test-Path $OutDir) {
    Remove-Item -Path (Join-Path $OutDir '*.zip') -Force -ErrorAction SilentlyContinue
    Remove-Item -Path (Join-Path $OutDir 'SHA256SUMS-*.txt') -Force -ErrorAction SilentlyContinue
}

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
        $content = $content -replace "`r`n", "`n"
        [System.IO.File]::WriteAllText($py, $content, [System.Text.UTF8Encoding]::new($false))
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
    $producedZips = @()
    if ($builtClient) {
        $z = Join-Path $OutDir "cpumon-client-$ver.zip"
        Compress-Archive -Path (Join-Path $OutDir 'client\*') -DestinationPath $z -Force
        $producedZips += $z
    }
    if ($builtServer) {
        $z = Join-Path $OutDir "cpumon-server-$ver.zip"
        Compress-Archive -Path (Join-Path $OutDir 'server\*') -DestinationPath $z -Force
        $producedZips += $z
    }
    # Linux client is bundled only on a full build, since it travels with both halves.
    if ($builtClient -and $builtServer) {
        $linuxSrc = Join-Path $PSScriptRoot 'cpumon.linux'
        if (Test-Path $linuxSrc) {
            $linuxOut = Join-Path $OutDir 'linux'
            Copy-LinuxClient $linuxSrc $linuxOut $ver
            $z = Join-Path $OutDir "cpumon-linux-$ver.zip"
            Compress-Archive -Path (Join-Path $linuxOut '*') -DestinationPath $z -Force
            $producedZips += $z
        }
    }
    Write-Ok

    if ($producedZips.Count -gt 0) {
        Write-Step "Generating SHA256SUMS-$ver.txt..."
        $sumsPath = Join-Path $OutDir "SHA256SUMS-$ver.txt"
        $lines = foreach ($p in $producedZips) {
            $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
            "$h  $(Split-Path $p -Leaf)"
        }
        # sha256sum-compatible: lowercase hash, two spaces, filename. LF line endings, no BOM.
        $bytes = [System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
        [System.IO.File]::WriteAllBytes($sumsPath, $bytes)
        Write-Ok
    }
}
Write-Host ''
