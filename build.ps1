<#
.SYNOPSIS
    Restores, builds, tests and publishes Focus Notch.

.DESCRIPTION
    Produces a self-contained win-x64 build in artifacts\FocusNotch-win-x64 that runs on a
    machine with no .NET installed. Stops on the first failure.

.PARAMETER SkipTests
    Publish without running the test suite. Not recommended.

.EXAMPLE
    .\build.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root 'FocusNotch.sln'
$artifacts = Join-Path $root 'artifacts\FocusNotch-win-x64'

# The SDK may be installed per-user rather than under Program Files.
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

$dotnet = Resolve-Dotnet
Write-Host "Using SDK: $dotnet" -ForegroundColor DarkGray

Write-Host "`n[1/4] Restoring packages" -ForegroundColor Cyan
& $dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "Restore failed." }

Write-Host "`n[2/4] Building Release" -ForegroundColor Cyan
& $dotnet build $solution --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if ($SkipTests) {
    Write-Host "`n[3/4] Tests skipped by request" -ForegroundColor Yellow
}
else {
    Write-Host "`n[3/4] Running tests" -ForegroundColor Cyan
    & $dotnet test $solution --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

Write-Host "`n[4/4] Publishing self-contained win-x64" -ForegroundColor Cyan
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }

& $dotnet publish (Join-Path $root 'src\FocusNotch.App\FocusNotch.App.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $artifacts
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$exe = Join-Path $artifacts 'FocusNotch.exe'
if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing." }

$size = [math]::Round(((Get-ChildItem $artifacts -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "`nDone. $exe ($size MB total)" -ForegroundColor Green
