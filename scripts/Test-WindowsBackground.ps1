# Build and test without injecting desktop input or connecting a virtual pad.
# Requires the .NET 8 SDK on PATH. Run from any directory.
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if ($env:APEX_TEST_DESKTOP_INPUT -eq '1' -or $env:APEX_TEST_DRIVERLESS -eq '1') {
    throw 'Unset APEX_TEST_DESKTOP_INPUT and APEX_TEST_DRIVERLESS for background testing.'
}

Push-Location $repository
try {
    dotnet build ApexAnalogMapper.sln -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

    dotnet test ApexAnalogMapper.sln -c Release --no-build -m:1 --logger trx --results-directory artifacts/windows-tests
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
