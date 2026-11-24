[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$workspace = Split-Path $PSScriptRoot -Parent
$deps = Join-Path $workspace '.deps'
$version = '5.4.23.5'
$archive = Join-Path $deps "BepInEx_win_x64_$version.zip"
$expectedHash = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4'
New-Item -ItemType Directory -Force -Path $deps | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v$version/BepInEx_win_x64_$version.zip" -UseBasicParsing -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw "BepInEx archive hash mismatch: $archive"
}
$destination = Join-Path $deps "BepInEx-$version"
# Cecil remains loaded when several test suites share one PowerShell process.
# Replacing an identical loaded DLL fails on Windows; verify each archive entry
# and extract only missing or changed files instead of forcing a full overwrite.
$destinationPrefix = [IO.Path]::GetFullPath($destination).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
$updated = 0
try {
    foreach ($entry in $zip.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
        if (-not $target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Archive entry escaped dependency directory: $($entry.FullName)" }
        if ([string]::IsNullOrEmpty($entry.Name)) { [void][IO.Directory]::CreateDirectory($target); continue }
        $stream = $entry.Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $hasher.Dispose() }
        if ((Test-Path -LiteralPath $target -PathType Leaf) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $entryHash) { continue }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        $updated++
    }
}
finally { $zip.Dispose() }
Write-Host "Verified BepInEx $version build references at $destination"
Write-Host "Updated $updated dependency files; matching files retained."
