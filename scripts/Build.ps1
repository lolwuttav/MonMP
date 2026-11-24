[CmdletBinding()]
param([Parameter(Mandatory)][string]$GameDirectory, [string]$PreviewSource)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
$GameDirectory = (Resolve-Path -LiteralPath $GameDirectory).Path
$gameAssembly = Join-Path $GameDirectory 'Mon Bazou_Data/Managed/Assembly-CSharp.dll'
if ((Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash -ne 'A7FF10D8090B788DE54DE31F31B8B0BCD338F94FF6C943653EA53D66395A20EE') {
    throw 'Unsupported game assembly. Use the supported Mon Bazou build.'
}
& (Join-Path $PSScriptRoot 'Setup.ps1') -GameDirectory $GameDirectory
& (Join-Path $PSScriptRoot 'Import-Previews.ps1') -GameDirectory $GameDirectory -PreviewSource $PreviewSource
$project = Join-Path $sourceRoot 'src\MonMP.Plugin\MonMP.Plugin.csproj'
dotnet build $project -c Release --no-restore "-p:GameDirectory=$GameDirectory" -p:MonMPDevelopmentTools=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Plugin and Core binaries: $sourceRoot\src\MonMP.Plugin\bin\Release\netstandard2.1"
