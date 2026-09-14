# Build, test, and package Mapper

Run all commands from the repository root.

## Build the full solution

Use Windows x64 with the .NET 8 SDK installed.

```powershell
dotnet build ApexAnalogMapper.sln -c Release
```

## Run the normal tests

Normal tests do not require ViGEmBus. They do not send desktop key presses or create a virtual controller.

```powershell
dotnet test ApexAnalogMapper.sln -c Release --no-build
```

To run the build and tests sequentially in the background, use the repository script:

```powershell
pwsh -File scripts/Test-WindowsBackground.ps1
```

The script refuses tests that send desktop key presses or create a virtual controller.
It also refuses tests that require a machine without ViGEmBus.

To test the cross-platform projects, run:

```powershell
dotnet test ApexAnalogMapper.CrossPlatform.slnf -c Release
```

## Run tests that use native Windows input or output

Use an idle desktop before you run any test in this section. These tests are skipped unless you enable them with the matching environment variable.

To inject synthetic A key events into the interactive desktop, run:

```powershell
$env:APEX_TEST_DESKTOP_INPUT = '1'
dotnet test tests/ApexMapper.Input.Tests -c Release
Remove-Item Env:APEX_TEST_DESKTOP_INPUT
```

To test behavior without ViGEmBus, use a Windows machine that does not have ViGEmBus installed:

```powershell
$env:APEX_TEST_DRIVERLESS = '1'
dotnet test tests/ApexMapper.Output.Tests -c Release
Remove-Item Env:APEX_TEST_DRIVERLESS
```

To create and remove a virtual controller, use a Windows machine with ViGEmBus and no other XInput controllers:

```powershell
$env:APEX_TEST_LIVE_OUTPUT = '1'
dotnet test tests/ApexMapper.Output.Tests -c Release --filter FullyQualifiedName~ViGEmLiveOutputTests
Remove-Item Env:APEX_TEST_LIVE_OUTPUT
```

Synthetic input does not test physical keyboard mapping. Before a release, test these actions with the supported keyboard and the actual game:

- Partial and full presses and releases
- Clutch and shifting at the same time
- Handbrake input
- Switching between apps
- Stop
- Device disconnection
- Process exit

## Build the MSI

Publish the app and the supervisor into the same fresh staging directory. Then build the MSI with WiX. The installer build restores the WiX SDK.

```powershell
dotnet publish src/ApexMapper.App -c Release -r win-x64 --self-contained true -o artifacts/staging
dotnet publish src/ApexMapper.Supervisor -c Release -r win-x64 --self-contained true -o artifacts/staging
dotnet build installer/ApexAnalogMapper.wixproj -c Release -p:StagingDir="$PWD/artifacts/staging" -p:ProductVersion=0.5.0
```

The application version is `0.5.0-alpha.1`. The numeric MSI version is `0.5.0`. A later MSI upgrade needs a higher numeric product version.

The tag-driven release workflow builds an MSI and a SHA-256 manifest. Tag and publish only after review and physical acceptance testing.
