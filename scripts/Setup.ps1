[CmdletBinding()]
param([string]$GameDirectory = '')
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET SDK 8 or later, then run setup.cmd again.' }
if ($GameDirectory) { $GameDirectory = (Resolve-Path -LiteralPath $GameDirectory).Path }
& (Join-Path $PSScriptRoot 'Bootstrap.ps1')
$project = Join-Path $sourceRoot 'src/MonMP.Plugin/MonMP.Plugin.csproj'
$packages = Join-Path $sourceRoot '.deps/nuget'
# The committed lock files pin package versions and NuGet content hashes.
$arguments = @('restore', $project, '--locked-mode', '--source', 'https://api.nuget.org/v3/index.json', '--packages', $packages)
if ($GameDirectory) { $arguments += "-p:GameDirectory=$GameDirectory" }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
Write-Host 'Dependencies ready. Run build.cmd -GameDirectory "C:\Games\Mon Bazou" to build.'
