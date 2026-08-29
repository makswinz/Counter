<#
.SYNOPSIS
    Builds everything a release ships: the installer and the portable executable.

.DESCRIPTION
    Runs the full build and test suite, then produces two artifacts:

      artifacts\Counter-Setup-<version>.exe     a per-user installer
      artifacts\Counter-<version>-portable.exe  one self-contained file

    Both are self-contained, so neither needs .NET installed on the machine that runs them.
    The version comes from Directory.Build.props and is never typed twice.

.PARAMETER SkipTests
    Package without running the test suite. Only for iterating on the packaging itself.

.PARAMETER PortableOnly
    Skip the installer. Useful on a machine without Inno Setup.

.EXAMPLE
    .\package.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$PortableOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifacts = Join-Path $root 'artifacts'
$folderBuild = Join-Path $artifacts 'Counter-win-x64'
$portableDir = Join-Path $artifacts 'portable'

function Resolve-Dotnet {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) {
        $sdks = & $onPath.Source --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $onPath.Source }
    }

    $userLocal = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $userLocal) { return $userLocal }

    throw "No .NET SDK found. Install the .NET 8 SDK from https://dot.net and re-run this script."
}

# Inno Setup installs to Program Files by default but a winget install can land elsewhere, so
# the compiler is looked for rather than assumed.
function Resolve-Inno {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    return $null
}

# One version, read from the one place it is written.
$props = Join-Path $root 'Directory.Build.props'
$version = ([xml](Get-Content $props)).Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { throw "Could not read <Version> from $props." }
$version = $version.Trim()

$dotnet = Resolve-Dotnet
Write-Host "Counter $version" -ForegroundColor Cyan
Write-Host "Using SDK: $dotnet" -ForegroundColor DarkGray

# A running instance holds its own executable open, and every build after that fails on a file
# lock with an error that says nothing about why.
Get-Process Counter -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

Write-Host "`n[1/5] Restoring and building" -ForegroundColor Cyan
& $dotnet build (Join-Path $root 'Counter.sln') --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if ($SkipTests) {
    Write-Host "`n[2/5] Tests skipped by request" -ForegroundColor Yellow
}
else {
    Write-Host "`n[2/5] Running tests" -ForegroundColor Cyan
    & $dotnet test (Join-Path $root 'Counter.sln') --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

Write-Host "`n[3/5] Publishing the installable build" -ForegroundColor Cyan
if (Test-Path $folderBuild) { Remove-Item $folderBuild -Recurse -Force }

& $dotnet publish (Join-Path $root 'src\Counter.App\Counter.App.csproj') `
    --configuration Release --runtime win-x64 --self-contained true --output $folderBuild
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Write-Host "`n[4/5] Publishing the portable single file" -ForegroundColor Cyan
if (Test-Path $portableDir) { Remove-Item $portableDir -Recurse -Force }

& $dotnet publish (Join-Path $root 'src\Counter.App\Counter.App.csproj') `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true --output $portableDir
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed." }

$portable = Join-Path $artifacts "Counter-$version-portable.exe"
Move-Item (Join-Path $portableDir 'Counter.exe') $portable -Force
Remove-Item $portableDir -Recurse -Force

if ($PortableOnly) {
    Write-Host "`n[5/5] Installer skipped by request" -ForegroundColor Yellow
}
else {
    Write-Host "`n[5/5] Building the installer" -ForegroundColor Cyan
    $inno = Resolve-Inno

    if (-not $inno) {
        throw @"
Inno Setup 6 was not found, so the installer cannot be built.

    winget install --id JRSoftware.InnoSetup --source winget

Or re-run with -PortableOnly to produce just the single file.
"@
    }

    & $inno "/DAppVersion=$version" (Join-Path $root 'packaging\Counter.iss')
    if ($LASTEXITCODE -ne 0) { throw "The installer failed to build." }
}

Write-Host ""
foreach ($file in Get-ChildItem $artifacts -Filter '*.exe' -File | Sort-Object Name) {
    $size = [math]::Round($file.Length / 1MB, 1)
    Write-Host ("  {0,-42} {1,6} MB" -f $file.Name, $size) -ForegroundColor Green
}

Write-Host "`nDone." -ForegroundColor Green
