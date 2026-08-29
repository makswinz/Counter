<#
.SYNOPSIS
    Builds Counter in Debug if needed and launches it.

.PARAMETER Demo
    Inserts a handful of example tasks, but only into a database that has no tasks at all.
    A normal launch never writes demo content.

.PARAMETER Rebuild
    Force a rebuild even when the executable is already up to date.

.EXAMPLE
    .\run.ps1
    .\run.ps1 -Demo
#>
[CmdletBinding()]
param(
    [switch]$Demo,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\Counter.App\Counter.App.csproj'
$exe = Join-Path $root 'src\Counter.App\bin\Debug\net8.0-windows10.0.19041.0\Counter.exe'

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

if ($Rebuild -or -not (Test-Path $exe)) {
    $dotnet = Resolve-Dotnet
    Write-Host "Building Debug..." -ForegroundColor Cyan
    & $dotnet build $project --configuration Debug
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

# Counter enforces a single instance: a second launch just reveals the running one.
$arguments = @()
if ($Demo) { $arguments += '--demo' }

Write-Host "Launching $exe $($arguments -join ' ')" -ForegroundColor Green
if ($arguments.Count -gt 0) {
    Start-Process -FilePath $exe -ArgumentList $arguments
}
else {
    Start-Process -FilePath $exe
}

Write-Host "Counter is at the top centre of your primary display. Its tray icon lives in the notification area."
