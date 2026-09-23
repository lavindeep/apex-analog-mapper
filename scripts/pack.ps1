# Publishes the app self-contained and packs it with Velopack into Setup.exe, a portable
# zip and the update packages. A previous release already in the output folder becomes
# the base of a delta package.
# Usage: scripts/pack.ps1 -Version 0.5.0-alpha [-Output releases] [-ReleaseNotes notes.md]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Output = "releases",
    [string] $ReleaseNotes
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "publish"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish (Join-Path $root "src/ApexMapper.App/ApexMapper.App.csproj") `
    -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$notes = if ($ReleaseNotes) { @("--releaseNotes", $ReleaseNotes) } else { @() }
vpk pack --packId ApexAnalogMapper --packTitle "Apex Analog Mapper" `
    --packAuthors "Lavindeep Dhillon" --packVersion $Version `
    --packDir $publish --mainExe ApexAnalogMapper.exe `
    --icon (Join-Path $root "src/ApexMapper.App/Assets/app.ico") `
    --outputDir (Join-Path $root $Output) @notes
exit $LASTEXITCODE
