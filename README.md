# Apex Analog Mapper

A Windows app that maps a selected SteelSeries keyboard to a virtual Xbox 360
controller. It reads digital key events through Windows Raw Input and turns
presses into smooth steering and trigger ramps. It does not measure key travel
or pressure.

## Quick start

1. Install the per-user MSI from [Releases](https://github.com/lavindeep/apex-analog-mapper/releases).
2. Install ViGEmBus 1.22.0 from the [official Nefarius release](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0).
   This separate driver installation requires Windows administrator approval.
3. Open Apex Analog Mapper. In Devices, choose **Make Primary** for your keyboard
   input. Some boards expose multiple input interfaces; select another if the
   selected interface does not produce the expected keys.
4. In Profiles, pin **Racing**, then choose **Enable mapping**.
5. Keep the app open while playing. Closing its window exits the mapper.
   **Ctrl+Alt+F12** forces output off. Mapping starts disabled on every launch.

The Racing profile is installed into an empty profile directory automatically.
Existing profiles and recovery backups are preserved.

| Key | Controller output |
| --- | --- |
| W / S | Right / left trigger, with a 120 ms press ramp |
| A / D | Left stick horizontal, with an 80 ms ramp |
| Left Shift / Space | LB / RB |
| Q / E | B / A |

Keyboard events also continue to reach the game. Configure its bindings if it
responds to both keyboard and controller input.

## Installation and data

The self-contained app and supervisor install together in
`%LocalAppData%\Programs\Apex Analog Mapper`. No separate .NET runtime is needed.
Profiles and selection settings live in `%AppData%\ApexMapper` and survive MSI
upgrades and uninstallation. Edit profile JSON there and use Refresh or the
built-in hot reload; there is no visual binding editor.

The MSI upgrades older product versions. Close the app before upgrading. Use
Windows Installed apps to uninstall it. If an older build registered a login
task, remove that task separately; the MSI does not own app-created login tasks.

Installers are unsigned. Check release SHA-256 checksums and obtain binaries
only from this repository. ViGEmBus is end-of-life; its final official installer
is signed by Nefarius. See [SECURITY.md](SECURITY.md).

## Scope and safety

The shipped app uses only digital Raw Input. Exploratory HID parsers and
calibration primitives remain in the input libraries, but are not connected to
the app and do not establish support for analog key travel. The unusable app
calibration wizard has been removed.

A separate supervisor owns the virtual controller. Named-pipe liveness checks
zero and disconnect it when the client disappears or heartbeat expires.
Disable, panic, device changes, profile changes, resume, and input-queue overflow
gate held keys so they must be released before mapping again. Overflow recovery
runs when the mapping loop next drains input. No process injects code into games.

The app performs driver and anti-cheat preflight checks before enabling output.
Detection is a best-effort advisory, not a guarantee that a game's anti-cheat
permits virtual controllers. There is no automatic enable path.

## Build and test

Requirements: Windows 10/11 x64 and .NET 8 SDK for the complete solution. ViGEmBus
is required only for live controller output.

```powershell
dotnet build ApexAnalogMapper.sln -c Release
dotnet test ApexAnalogMapper.sln -c Release --no-build
```

Normal test runs report desktop-injection, driver-absence and live-output tests as
skipped. They require explicit opt-in so tests do not type into another app or
assume a developer's machine has no driver. Composition tests use temporary
app-data directories.

```powershell
# Sequential build/test, with native opt-ins rejected.
pwsh -File scripts/Test-WindowsBackground.ps1

# Only on an idle interactive desktop: inject synthetic A key events.
$env:APEX_TEST_DESKTOP_INPUT = '1'
dotnet test tests/ApexMapper.Input.Tests -c Release
Remove-Item Env:APEX_TEST_DESKTOP_INPUT

# Only on a Windows machine WITHOUT ViGEmBus.
$env:APEX_TEST_DRIVERLESS = '1'
dotnet test tests/ApexMapper.Output.Tests -c Release
Remove-Item Env:APEX_TEST_DRIVERLESS

# Only on an idle desktop WITH ViGEmBus and no other XInput controllers.
# Creates a pad, checks exact neutral and nonzero reports, then removes it.
$env:APEX_TEST_LIVE_OUTPUT = '1'
dotnet test tests/ApexMapper.Output.Tests -c Release --filter FullyQualifiedName~ViGEmLiveOutputTests
Remove-Item Env:APEX_TEST_LIVE_OUTPUT
```

Synthetic injected keys do not prove physical-device mapping. Live acceptance
must cover the physical keyboard, controller-axis direction, hold/release,
profile switching, panic, disconnect, and process termination in an actual game.

The cross-platform subset is available with
`dotnet test ApexAnalogMapper.CrossPlatform.slnf -c Release`.

## Package

Publish the app and supervisor to the same fresh staging directory, then build
WiX. Use a higher numeric MSI version for an upgrade.

```powershell
dotnet publish src/ApexMapper.App -c Release -r win-x64 --self-contained true -o artifacts/staging
dotnet publish src/ApexMapper.Supervisor -c Release -r win-x64 --self-contained true -o artifacts/staging
dotnet build installer/ApexAnalogMapper.wixproj -c Release -p:StagingDir="$PWD/artifacts/staging" -p:ProductVersion=0.1.3
```

The tag-driven release workflow builds the MSI and SHA-256 manifest. Public
release still requires live hardware acceptance. No signing or updater is
implemented. The current source version is a preview, not a claim of completed
in-game validation.

## License

MIT
