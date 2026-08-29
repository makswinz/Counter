<#
.SYNOPSIS
    Regenerates Assets\FocusNotch.ico from the application's own drawing.

.DESCRIPTION
    The icon is a committed binary, and the only thing in the project that can silently stop
    matching the code that produced it. Run this after changing Branding.DrawMark; a test
    compares the committed file with what the drawing produces and fails the build otherwise.

    Nothing else happens: the switch is handled before the database is opened or a window is
    created, so this is safe to run while Focus Notch is running.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function Resolve-Dotnet {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) {
        $sdks = & $onPath.Source --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $onPath.Source }
    }
    $userLocal = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $userLocal) { return $userLocal }
    throw "No .NET SDK found. Install the .NET 8 SDK from https://dot.net."
}

$dotnet = Resolve-Dotnet
$icon = Join-Path $root 'Assets\FocusNotch.ico'

& $dotnet run --project (Join-Path $root 'src\FocusNotch.App\FocusNotch.App.csproj') `
    --configuration Release -- --write-icon $icon
if ($LASTEXITCODE -ne 0) { throw "Writing the icon failed." }

$size = (Get-Item $icon).Length
Write-Host "Wrote $icon ($size bytes)." -ForegroundColor Green
