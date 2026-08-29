<#
.SYNOPSIS
    Downloads the pinned Microsoft Fluent UI System Icons and regenerates the compiled WPF
    geometry resources and the icon catalog from them.

.DESCRIPTION
    Focus Notch draws every icon from one family. This script is the only thing that puts an
    icon into the application, and it is deterministic: tools/icons.psd1 names an exact upstream
    release tag and an exact commit, and every file is checksummed into
    Assets/Icons/Fluent/manifest.json. Re-running the script on an unchanged manifest downloads
    nothing and produces byte-identical output.

    The build never runs this. The generated files are committed, so a build has no network
    dependency at all and the application has no runtime one.

    Outputs:
      Assets/Icons/Fluent/*.svg                     the untouched upstream artwork
      Assets/Icons/Fluent/manifest.json             revision, commit and per-file SHA-256
      src/FocusNotch.App/Controls/IconCatalog.g.cs  IconKind, IconVariant, the path data and the
                                                    lookup table

.PARAMETER Refresh
    Re-downloads every file even when it is already present, and rewrites the manifest. Use it
    only when deliberately moving to a newer revision in tools/icons.psd1.

.PARAMETER Verify
    Checks the local files against the manifest and regenerates nothing. Exits non-zero on any
    mismatch. Suitable for a pre-commit hook.
#>
[CmdletBinding()]
param(
    [switch] $Refresh,
    [switch] $Verify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName PresentationCore

$root = Split-Path -Parent $PSScriptRoot
$spec = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'icons.psd1')

$assetDir = Join-Path $root 'Assets\Icons\Fluent'
$manifestPath = Join-Path $assetDir 'manifest.json'
$catalogPath = Join-Path $root 'src\FocusNotch.App\Controls\IconCatalog.g.cs'

if (-not (Test-Path $assetDir)) { New-Item -ItemType Directory -Path $assetDir -Force | Out-Null }

# TLS 1.2 is not the default on Windows PowerShell 5.1 and raw.githubusercontent.com requires it.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-Sha256([string] $path) {
    return (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---------------------------------------------------------------------------------- download

$baseUrl = "https://raw.githubusercontent.com/microsoft/fluentui-system-icons/$($spec.Revision)/assets/"
$downloaded = 0

foreach ($icon in $spec.Icons) {
    $target = Join-Path $assetDir $icon.File

    if ((Test-Path $target) -and -not $Refresh) { continue }
    if ($Verify) { throw "Missing asset $($icon.File). Run the script without -Verify first." }

    $folder = [Uri]::EscapeDataString($icon.Folder)
    $url = "$baseUrl$folder/SVG/$($icon.File)"

    Write-Host "  download $($icon.File)"
    Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
    $downloaded++
}

# ---------------------------------------------------------------------------------- manifest

# One entry per distinct file: two variants of the same icon are two files, but Pin Regular and
# Pin Filled would otherwise be checksummed twice. Deduplicated by hand because Sort-Object
# -Unique compares hashtables by identity rather than by the property asked for.
$hashes = [ordered] @{}
foreach ($file in ($spec.Icons | ForEach-Object { $_.File } | Sort-Object -Unique)) {
    $hashes[$file] = Get-Sha256 (Join-Path $assetDir $file)
}

if ((Test-Path $manifestPath) -and -not $Refresh) {
    $existing = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    $problems = @()

    if ($existing.revision -ne $spec.Revision) {
        $problems += "manifest revision $($existing.revision) does not match icons.psd1 $($spec.Revision)"
    }

    foreach ($file in $hashes.Keys) {
        $recorded = $existing.files.PSObject.Properties[$file]
        if ($null -eq $recorded) { $problems += "$file is not in the manifest"; continue }
        if ($recorded.Value -ne $hashes[$file]) { $problems += "$file does not match its recorded SHA-256" }
    }

    foreach ($recorded in $existing.files.PSObject.Properties) {
        if (-not $hashes.Contains($recorded.Name)) { $problems += "$($recorded.Name) is in the manifest but not in icons.psd1" }
    }

    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "  ! $_" -ForegroundColor Red }
        throw 'The bundled icons do not match the manifest. Re-run with -Refresh to accept the change deliberately.'
    }

    Write-Host "  verified $($hashes.Count) assets against the manifest"
}
else {
    $manifest = [ordered] @{
        source      = $spec.Source
        revision    = $spec.Revision
        commit      = $spec.Commit
        license     = $spec.License
        retrievedOn = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
        files       = $hashes
    }

    ($manifest | ConvertTo-Json -Depth 4) | Out-File -FilePath $manifestPath -Encoding utf8
    Write-Host "  wrote manifest for $($hashes.Count) assets"
}

if ($Verify) {
    Write-Host 'Icons verified.' -ForegroundColor Green
    return
}

# ---------------------------------------------------------------------------------- convert

# Extracts the fill geometry from one upstream SVG.
#
# Fluent System Icons are single-colour filled artwork: every icon is one or more <path>
# elements with a solid fill and no stroke, no transform and no gradient. Anything else would
# not survive the conversion silently, so it stops the script instead.
function Convert-Svg([string] $path, [int] $expectedSize) {
    [xml] $svg = Get-Content -Path $path -Raw

    $viewBox = $svg.svg.viewBox
    if ($viewBox -ne "0 0 $expectedSize $expectedSize") {
        throw "$([IO.Path]::GetFileName($path)) has viewBox '$viewBox', expected '0 0 $expectedSize $expectedSize'."
    }

    $paths = @($svg.SelectNodes('//*[local-name()="path"]'))
    if ($paths.Count -eq 0) { throw "$([IO.Path]::GetFileName($path)) has no path element." }

    $segments = [System.Collections.Generic.List[string]]::new()
    $rules = [System.Collections.Generic.List[string]]::new()

    foreach ($node in $paths) {
        foreach ($banned in @('transform', 'stroke', 'clip-path', 'mask')) {
            if ($node.HasAttribute($banned)) {
                throw "$([IO.Path]::GetFileName($path)) uses '$banned', which this converter does not handle."
            }
        }

        $fill = $node.GetAttribute('fill')
        if ($fill -eq 'none') { continue }
        if ($fill -like 'url(*') { throw "$([IO.Path]::GetFileName($path)) uses a gradient or pattern fill." }

        $rule = $node.GetAttribute('fill-rule')
        if ([string]::IsNullOrEmpty($rule)) { $rule = 'nonzero' }
        $rules.Add($rule)
        $segments.Add((($node.GetAttribute('d') -replace '\s+', ' ').Trim()))
    }

    if ($segments.Count -eq 0) { throw "$([IO.Path]::GetFileName($path)) has no filled path." }

    $distinct = @($rules | Sort-Object -Unique)
    if ($distinct.Count -gt 1) {
        throw "$([IO.Path]::GetFileName($path)) mixes fill rules, which one geometry cannot express."
    }

    # WPF's mini-language defaults to EvenOdd; SVG defaults to nonzero. The prefix makes the
    # source file's own rule explicit rather than relying on either default.
    $prefix = 'F1'
    if ($distinct[0] -eq 'evenodd') { $prefix = 'F0' }

    $data = "$prefix " + ($segments -join ' ')

    # Parsed here so a geometry that WPF cannot read fails the conversion rather than the app.
    [System.Windows.Media.Geometry]::Parse($data) | Out-Null

    return $data
}

$entries = [System.Collections.Generic.List[object]]::new()
foreach ($icon in $spec.Icons) {
    $data = Convert-Svg (Join-Path $assetDir $icon.File) $icon.Size

    $entries.Add([pscustomobject] @{
        Kind    = $icon.Kind
        Variant = $icon.Variant
        Key     = "Icon$($icon.Kind)$($icon.Variant)"
        Size    = $icon.Size
        File    = $icon.File
        Data    = $data
    })
}

$entries = @($entries | Sort-Object -Property Key)

# ---------------------------------------------------------------------------------- catalog

$kinds = @($entries | Select-Object -ExpandProperty Kind -Unique | Sort-Object)

$cs = New-Object System.Text.StringBuilder
[void] $cs.AppendLine('// GENERATED FILE - do not edit by hand. See tools\Sync-FluentIcons.ps1.')
[void] $cs.AppendLine('//')
[void] $cs.AppendLine('// Microsoft Fluent UI System Icons, MIT licensed.')
[void] $cs.AppendLine("// Revision $($spec.Revision), commit $($spec.Commit).")
[void] $cs.AppendLine('')
[void] $cs.AppendLine('namespace FocusNotch.App.Controls;')
[void] $cs.AppendLine('')
[void] $cs.AppendLine('/// <summary>Every icon the application is allowed to draw.</summary>')
[void] $cs.AppendLine('public enum IconKind')
[void] $cs.AppendLine('{')
[void] $cs.AppendLine('    /// <summary>Draws nothing. The default, so an unset icon is blank rather than wrong.</summary>')
[void] $cs.AppendLine('    None,')
[void] $cs.AppendLine('')
foreach ($kind in $kinds) { [void] $cs.AppendLine("    $kind,") }
[void] $cs.AppendLine('}')
[void] $cs.AppendLine('')
[void] $cs.AppendLine('/// <summary>Regular is the default weight; Filled is for primary actions and active states.</summary>')
[void] $cs.AppendLine('public enum IconVariant')
[void] $cs.AppendLine('{')
[void] $cs.AppendLine('    Regular,')
[void] $cs.AppendLine('    Filled,')
[void] $cs.AppendLine('}')
[void] $cs.AppendLine('')
[void] $cs.AppendLine('/// <summary>One entry of the generated table: which resource, and the grid it was drawn on.</summary>')
[void] $cs.AppendLine('/// <param name="ResourceKey">The key of the compiled geometry.</param>')
[void] $cs.AppendLine('/// <param name="ViewboxSize">The edge of the source viewBox, in its own units.</param>')
[void] $cs.AppendLine('/// <param name="SourceFile">The upstream SVG, for traceability.</param>')
[void] $cs.AppendLine('public readonly record struct IconGlyph(string ResourceKey, double ViewboxSize, string SourceFile);')
[void] $cs.AppendLine('')
[void] $cs.AppendLine('public static partial class IconCatalog')
[void] $cs.AppendLine('{')
[void] $cs.AppendLine('    /// <summary>The upstream release these geometries were taken from.</summary>')
[void] $cs.AppendLine("    public const string Revision = `"$($spec.Revision)`";")
[void] $cs.AppendLine('')
[void] $cs.AppendLine('    /// <summary>The exact upstream commit, so a geometry can always be traced back.</summary>')
[void] $cs.AppendLine("    public const string Commit = `"$($spec.Commit)`";")
[void] $cs.AppendLine('')
[void] $cs.AppendLine('    private static readonly Dictionary<(IconKind, IconVariant), IconGlyph> Glyphs = new()')
[void] $cs.AppendLine('    {')
foreach ($entry in $entries) {
    [void] $cs.AppendLine("        [(IconKind.$($entry.Kind), IconVariant.$($entry.Variant))] = new(`"$($entry.Key)`", $($entry.Size), `"$($entry.File)`"),")
}
[void] $cs.AppendLine('    };')
[void] $cs.AppendLine('')
[void] $cs.AppendLine('    /// <summary>')
[void] $cs.AppendLine('    /// The artwork itself, in WPF path mini-language, one entry per resource key.')
[void] $cs.AppendLine('    ///')
[void] $cs.AppendLine('    /// Compiled into the assembly rather than parsed out of a resource dictionary: the app')
[void] $cs.AppendLine('    /// has no way to fail to find an icon at runtime, a test can read one with no Application')
[void] $cs.AppendLine('    /// in the process, and there is no dictionary for a same-named key to shadow.')
[void] $cs.AppendLine('    ///')
[void] $cs.AppendLine('    /// The F1 prefix selects the nonzero fill rule, which is what SVG uses by default and')
[void] $cs.AppendLine('    /// what makes the holes in a shape like the settings gear appear.')
[void] $cs.AppendLine('    /// </summary>')
[void] $cs.AppendLine('    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)')
[void] $cs.AppendLine('    {')
foreach ($entry in $entries) {
    [void] $cs.AppendLine("        // $($entry.File), viewBox $($entry.Size) x $($entry.Size)")
    [void] $cs.AppendLine("        [`"$($entry.Key)`"] =")
    [void] $cs.AppendLine("            `"$($entry.Data)`",")
    [void] $cs.AppendLine('')
}
[void] $cs.AppendLine('    };')
[void] $cs.AppendLine('}')

$cs.ToString() | Out-File -FilePath $catalogPath -Encoding utf8
Write-Host "  wrote $($kinds.Count) kinds and $($entries.Count) geometries to Controls\IconCatalog.g.cs"

Write-Host "Icons synced from $($spec.Source) at $($spec.Revision) ($downloaded downloaded)." -ForegroundColor Green
