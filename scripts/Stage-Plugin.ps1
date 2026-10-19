param([Parameter(Mandatory)][string]$PluginDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $repo 'src/Jellyfin.Plugin.S3Storage/Jellyfin.Plugin.S3Storage.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$output = Join-Path $repo 'src/Jellyfin.Plugin.S3Storage/bin/Release/net10.0'
New-Item -ItemType Directory -Path $PluginDirectory -Force | Out-Null
foreach ($name in @('Jellyfin.Plugin.S3Storage.dll', 'AWSSDK.S3.dll', 'AWSSDK.Core.dll')) {
    Copy-Item -LiteralPath (Join-Path $output $name) -Destination $PluginDirectory -Force
}
$manifest = @{ guid='7637e67a-7fd1-45a2-8b34-e69170cb470f'; name='S3 Storage'; version='0.2.0.0'; targetAbi='13.0.0.0'; status='Active'; autoUpdate=$false; assemblies=@('Jellyfin.Plugin.S3Storage.dll','AWSSDK.S3.dll','AWSSDK.Core.dll') }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PluginDirectory 'meta.json') -Encoding utf8
Write-Output "Staged S3 Storage at $PluginDirectory"
