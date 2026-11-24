[CmdletBinding()]
param([Parameter(Mandatory)][string]$GameDirectory, [string]$PreviewSource)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root 'src/MonMP.Plugin/UI/AppearancePortraits'
$images = (Get-Content -Raw -LiteralPath (Join-Path $directory 'manifest.json') | ConvertFrom-Json).images
$names = @{}
$missing = @(foreach ($image in $images) {
    if ($image.file -cnotmatch '^[a-z0-9-]+\.png$' -or $image.sha256 -notmatch '^[a-f0-9]{64}$' -or $names.ContainsKey($image.file)) {
        throw 'Invalid entry in the appearance manifest.'
    }
    $names[$image.file] = $true
    $file = Join-Path $directory $image.file
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file).Hash -ne $image.sha256) { $image }
})
foreach ($file in Get-ChildItem -LiteralPath $directory -File -Filter '*.png') {
    if (!$names.ContainsKey($file.Name)) { throw "Unexpected preview in build inputs: $($file.Name)" }
}
if (!$missing.Count) { Write-Host "Verified all $($images.Count) appearance previews."; return }
if (!$PreviewSource) {
    $PreviewSource = Join-Path $GameDirectory 'BepInEx/plugins/MonMP/MonMP.dll'
    if (!(Test-Path -LiteralPath $PreviewSource -PathType Leaf)) {
        throw 'Appearance previews are missing. Pass -PreviewSource with a matching MonMP mod-only ZIP, release source ZIP or MonMP.dll. See BUILDING.md.'
    }
}
$path = (Get-Item -LiteralPath $PreviewSource).FullName
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = $null
$dll = $null
$module = $null
$hasher = [Security.Cryptography.SHA256]::Create()
try {
    $resourceFolder = 'src/MonMP.Plugin/UI/AppearancePortraits/'
    if ([IO.Path]::GetExtension($path) -ieq '.zip') {
        $zip = [IO.Compression.ZipFile]::OpenRead($path)
        $entries = @{}
        foreach ($archived in $zip.Entries) {
            $entries.Add($archived.FullName.Replace('\', '/'), $archived)
        }
        if (!$entries.ContainsKey($resourceFolder + 'manifest.json')) {
            $entry = $entries['BepInEx/plugins/MonMP/MonMP.dll']
            if (!$entry -or $entry.Length -gt 64MB) { throw 'The archive has no supported MonMP preview source.' }
            $dll = [IO.MemoryStream]::new()
            $stream = $entry.Open()
            try { $stream.CopyTo($dll) } finally { $stream.Dispose() }
            $dll.Position = 0
        }
    } elseif ([IO.Path]::GetExtension($path) -ieq '.dll') {
        $dll = [IO.File]::OpenRead($path)
    } else { throw 'Pass a MonMP release ZIP or MonMP.dll.' }
    if ($dll) {
        # Read resource data. The supplied game plugin is never executed.
        $cecil = Join-Path $root '.deps/BepInEx-5.4.23.5/BepInEx/core/Mono.Cecil.dll'
        [void][Reflection.Assembly]::LoadFrom($cecil)
        $module = [Mono.Cecil.ModuleDefinition]::ReadModule($dll)
        $resources = @{}
        foreach ($resource in $module.Resources) {
            if ($resource -is [Mono.Cecil.EmbeddedResource]) { $resources[$resource.Name] = $resource }
        }
    }
    foreach ($image in $missing) {
        if ($module) {
            $resource = $resources['MonMP.Appearance.' + $image.file]
            if (!$resource) { throw "Release is missing $($image.file)." }
            $bytes = $resource.GetResourceData()
        } else {
            $entry = $entries[$resourceFolder + $image.file]
            if (!$entry -or $entry.Length -gt 1MB) { throw "Missing or oversized preview: $($image.file)" }
            $stream = $entry.Open()
            $buffer = [IO.MemoryStream]::new()
            try { $stream.CopyTo($buffer); $bytes = $buffer.ToArray() }
            finally { $buffer.Dispose(); $stream.Dispose() }
        }
        $hash = [BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace('-', '')
        if ($hash -ne $image.sha256) { throw "Different artwork for $($image.file). Use the release corresponding to this source." }
        [IO.File]::WriteAllBytes((Join-Path $directory $image.file), $bytes)
    }
    Write-Host "Imported $($missing.Count) verified previews ($($images.Count) total)."
} finally {
    if ($module) { $module.Dispose() }
    if ($dll) { $dll.Dispose() }
    if ($zip) { $zip.Dispose() }
    $hasher.Dispose()
}
