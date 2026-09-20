# Publishes the app self-contained and packs it with Velopack.
# Usage: scripts/pack.ps1 -Version 0.1.0 [-Output releases]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Output = "releases"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "publish"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish (Join-Path $root "src/ApexMapper.App/ApexMapper.App.csproj") `
    -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

vpk pack --packId ApexAnalogMapper --packTitle "Apex Analog Mapper" `
    --packAuthors "Lavindeep Dhillon" --packVersion $Version `
    --packDir $publish --mainExe ApexAnalogMapper.exe --outputDir (Join-Path $root $Output)
exit $LASTEXITCODE
