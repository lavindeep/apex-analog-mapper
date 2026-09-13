# Apex Analog Mapper

A Windows app that maps a selected SteelSeries keyboard to a virtual Xbox 360
controller. v0.5 alpha reads physical key sensors on the original Apex Pro TKL
with USB VID `1038`, PID `1614`, and firmware `4.9.1`. Calibrated sensor values
control steering and triggers continuously. These are sensor counts, not measured
millimetres or pressure.

## Quick start

1. Install the per-user MSI from [Releases](https://github.com/lavindeep/apex-analog-mapper/releases).
2. Install ViGEmBus 1.22.0 from the [official Nefarius release](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0).
   This separate driver installation requires Windows administrator approval.
3. Open Apex Analog Mapper and choose your **Keyboard**. Windows input interfaces
   belonging to the same physical keyboard appear as one choice. If keys do not
   respond, try another **Input source** under **Advanced**.
4. Choose the **Racing** profile. Use **Edit bindings** to change its keys.
5. Open **Calibrate keys** and set each key's released and fully pressed positions,
   then save. Calibration belongs to that physical keyboard and firmware.
6. Choose the running **Game**, then click **Start**. Release the mapped keys first.
7. Keep the app open while playing. Closing its window exits the mapper.
   **Stop** or **Ctrl+Alt+F12** forces output off. Mapping starts stopped on every launch.

The Racing profile is installed into an empty profile directory automatically.
Existing profiles and recovery backups are preserved.

| Key | Controller output |
| --- | --- |
| W / S | Right / left trigger, from calibrated sensor input |
| A / D | Left stick horizontal, from calibrated sensor input |
| Space / R / E / Q | A / Y / B / X |
| Left Shift / Tab | LB / RB |
| G | Left stick click |
| Left / Right arrows | Right stick horizontal, instant response |
| Down / Up arrows | Right stick vertical, instant response |

While the selected game is foreground, mapped keyboard events are blocked to
prevent double input. Analog depths come from the selected keyboard's sensors;
digital bindings come directly from the Windows keyboard hook. The hook cannot
identify a physical keyboard: mapped digital keys work, and mapped keystrokes
are blocked, on every keyboard during that game session. Unmapped keys and
Ctrl/Alt/Windows shortcuts remain available.

Switching away restores keyboard input and zeros controller output. Stop,
controller disconnection, and editing also release the keyboard filter. Held
keys must be released before they can map again.

Analog presses follow the sensor directly. A binding's `release_ramp_ms` can
soften the return toward zero after its response curve, without delaying a new
press or a direction reversal. The Racing preset adds no steering press or
release ramp. Zero disables smoothing. Stop and held-key
gates clear the return history immediately.

## Installation and data

The self-contained app and supervisor install together in
`%LocalAppData%\Programs\Apex Analog Mapper`. No separate .NET runtime is needed.
Profiles and selection settings live in `%AppData%\ApexMapper` and survive MSI
upgrades and uninstallation. The binding editor saves profile changes there.
Direct JSON edits also reload automatically.

The MSI upgrades older product versions. Close the app before upgrading. Use
Windows Installed apps to uninstall it. If an older build registered a login
task, remove that task separately; the MSI does not own app-created login tasks.

Installers are unsigned. Check release SHA-256 checksums and obtain binaries
only from this repository. ViGEmBus is end-of-life; its final official installer
is signed by Nefarius. See [SECURITY.md](SECURITY.md).

## Scope and safety

Analog support is limited to the exact model and firmware above. The reader
verifies the firmware and queries filtered sensor counts through the keyboard's
vendor HID interface. It does not flash firmware or change actuation settings.
W, A, S and D have physical capture evidence; other sensor key positions are
derived from the firmware map and still need hardware acceptance.

Missing calibration, stale readings, and connection failures prevent analog
output. Supported sensor keys do not fall back to digital ramps. Mechanical keys,
including this keyboard's arrows, remain digital. Opening either editor stops
mapping and blocks Start until the editor closes.

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
dotnet build installer/ApexAnalogMapper.wixproj -c Release -p:StagingDir="$PWD/artifacts/staging" -p:ProductVersion=0.5.0
```

The tag-driven release workflow builds the MSI and SHA-256 manifest. Public
release still requires live hardware acceptance. No signing or updater is
implemented. v0.5 is an alpha with hardware support limited to the model and
firmware listed above.

## License

MIT
