# Apex Analog Mapper

A Windows app that turns physical key depth into Xbox 360 controller input.
Press lightly for partial throttle or steering, then press farther for more.

**v0.5 alpha** supports the original SteelSeries Apex Pro TKL with USB ID
`1038:1614` and firmware `4.9.1`. W, A, S and D have been tested on that hardware.
Other Apex Pro generations and firmware versions are not supported by this alpha.

## Install

You need Windows 10 or 11 x64, the supported keyboard, and
[ViGEmBus 1.22.0](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0).
The driver requires administrator approval and is installed separately.
ViGEmBus is end-of-life; use its official download.

Download an MSI and its SHA-256 manifest from this repository's
[Releases](https://github.com/lavindeep/apex-analog-mapper/releases) when published.
Unreleased alpha builds are distributed for testing before a release is tagged.
The app is self-contained, so you do not need to install .NET.

Close Mapper before running the MSI. It replaces an older MSI installation and
preserves your profiles and calibration. The app installs for your Windows user
in `%LocalAppData%\Programs\Apex Analog Mapper`.

Installers are unsigned. Compare the download's checksum with its accompanying
manifest before running it:

```powershell
Get-FileHash .\apex-analog-mapper-0.5.0-alpha.1.msi -Algorithm SHA256
```

## Set up

1. Open Apex Analog Mapper and choose your **Keyboard**. Its Windows input
   interfaces are grouped into one physical device. If the selected source does
   not receive keys, choose another **Input source** under **Advanced**.
2. Select **Racing**. An empty profile directory gets this preset automatically;
   an upgrade keeps your existing profile.
3. Use **Edit bindings** to choose controller outputs and record your keys.
   Click a key button, then press the replacement key. **Add key** adds a
   single-key binding; **Add stick axis** adds a pair of direction keys. Save.
4. Open **Calibrate keys**. For each listed key, release it and click
   **Set released**. Hold it fully down and click **Set fully pressed**. Save
   after setting both positions for every listed key.
5. Launch your game. Choose its window in **Game**, using **Refresh** if needed.
6. Release the mapped keys and click **Start**, then return to the game.

Mapper starts stopped on every launch. **Stop** and **Ctrl+Alt+F12** turn mapping
off. Closing the window exits the app. Opening either editor stops mapping;
close the editor before starting again.

Select the running game again after it exits and restarts. Game selection is
for that process instance and is not saved across Mapper launches.

## Racing bindings

The preset matches Forza's Default Layout 1 controller actions and WASD keyboard
layout. Bindings are editable; controller actions also depend on the layout
selected inside the game.

| Action | Keyboard | Controller |
| --- | --- | --- |
| Accelerate | W | RT, analog |
| Brake | S | LT, analog |
| Steer left / right | A / D | Left stick X, analog |
| E-brake | Space | A |
| Rewind | R | Y |
| Shift up | E | B |
| Shift down | Q | X |
| Clutch / AutoDrive cinematic camera | Left Shift | LB |
| Switch camera | Tab | RB |
| Toggle convertible | G | Left stick click |
| Look left / right | Left / Right arrows | Right stick X |
| Look back / forward | Down / Up arrows | Right stick Y |

Left Shift works together with Q and E for clutch and shifting. Arrow keys on
this keyboard are mechanical and produce digital camera input.

Ctrl, Alt, Windows and F12 are reserved for shortcuts and cannot be assigned as
controller bindings. Ctrl/Alt/Windows combinations pass through to Windows.

## Analog response

SteelSeries GG's actuation setting controls when a normal keyboard key-down
fires. Changing it to 3 or 4 mm does not turn that key-down into analog input.
Mapper reads separate sensor reports and normalizes them using your saved
released and fully pressed positions.

Calibration is specific to the physical keyboard and firmware. The displayed
percentage is normalized sensor input, not a measurement in millimetres or a
pressure reading. Recalibrate after changing analog bindings or if a key no
longer reaches zero or full input reliably.

The Racing curve builds gradually through the first 70% of normalized input,
then rises more steeply to full output. Steering has no added press or release
delay. Analog presses follow the sensor; digital bindings can use timed ramps.
Existing profiles keep their curves and timing when you upgrade.

Curves and timing are stored in the profile JSON. `release_ramp_ms` can soften
analog return toward zero; `0` disables it. A new press or direction reversal
responds immediately, and stopping clears the return history. The current editor
changes bindings, not response curves or ramp values.

## Keyboard filtering

While your selected game is foreground, Mapper blocks mapped keyboard events
and sends their controller equivalents. This includes clutch, shifting and
handbrake, so the game does not receive both inputs.

Analog input comes from the selected keyboard's sensors. Digital bindings are
captured by a Windows keyboard hook, which cannot identify the originating
keyboard. **Mapped digital keys work on every physical keyboard, and mapped
keystrokes are blocked on every keyboard, while the selected game is foreground.**
Unmapped keys remain available.

Switching away restores normal keyboard input and zeros controller output.
Release any held mapped keys before using them again after returning. Stop,
game exit, controller disconnection, profile or device changes, and input faults
also stop mapping or clear held input. Mapper never starts automatically.

## Troubleshooting

| Problem | Check |
| --- | --- |
| Start asks for a game | Launch the game, refresh the Game list and select its window. |
| No virtual controller | Install ViGEmBus, restart Windows if its installer requests it, then reopen Mapper. |
| No sensor input | Check the exact USB ID and firmware above. Other models and firmware are rejected. |
| Calibration is missing or incomplete | Set both endpoints for every key listed in Calibrate keys, then save. |
| A key stays at zero after Start or an app switch | Release it fully, then press it again. Held keys must be released before reuse. |
| A key reaches full input too early | Recalibrate it, holding it fully down for Set fully pressed. Also check the profile's response curve. |
| Digital input is missing outside game capture | Try the other Input source under Advanced. Some keyboards expose more than one keyboard interface. |
| The game still receives keyboard and controller input | Check that Mapper is Running and the selected Game is the actual game window. Only mapped keys are filtered; Ctrl/Alt/Windows shortcuts are passed through. |
| An upgrade kept old binds or steering timing | Existing profiles are preserved. Edit bindings in the app and inspect the saved profile for curve or ramp changes. |

For a bug report, include the app version, keyboard USB ID, firmware, selected
input source, game, and the steps that reproduce it. The supervisor log is at
`%LocalAppData%\ApexAnalogMapper\logs\supervisor.log`. It records controller
session state and errors, not key presses.

## Data and removal

User data lives in `%AppData%\ApexMapper`:

| Location | Contents |
| --- | --- |
| `profiles\*.json` | Bindings, curves and timing |
| `device-registry.json` | Device selection and per-key calibration |
| `profile-pin.json` | Selected profile |
| `panic-policy.json` | Stop shortcut policy |

Direct profile JSON edits reload automatically and can stop mapping. Back up
this directory before editing files or moving settings to another installation.
Calibration should be recaptured for a different physical keyboard.

Uninstall through Windows Installed apps. User data survives uninstallation.
If an older build created a login task, remove that task separately; it is not
owned by the MSI. There is no automatic updater.

## Alpha limits and safety

Only the hardware and firmware listed above have analog support. Other sensor
key positions come from the firmware map and still need physical testing beyond
WASD. Supported sensor keys require calibration and fresh reports; they do not
silently fall back to digital ramps. The app does not flash firmware or change
SteelSeries actuation settings.

A separate supervisor owns the virtual controller. Loss of the client or its
heartbeat zeros and disconnects the controller. Stop and device/profile changes
clear held input before reactivation. The app does not inject code into games
or install a keyboard filter driver.

Driver and anti-cheat checks run before Start. Anti-cheat detection is an
advisory check, not proof that a game permits virtual controllers. See
[SECURITY.md](SECURITY.md) for input handling and vulnerability reporting.

## Build and test

The full solution needs Windows x64 and the .NET 8 SDK. Normal tests do not
require ViGEmBus or generate desktop key presses.

```powershell
dotnet build ApexAnalogMapper.sln -c Release
dotnet test ApexAnalogMapper.sln -c Release --no-build
```

For sequential background checks with native opt-ins rejected:

```powershell
pwsh -File scripts/Test-WindowsBackground.ps1
```

The cross-platform subset is available with
`dotnet test ApexAnalogMapper.CrossPlatform.slnf -c Release`.

Native tests are skipped unless explicitly enabled. Run desktop tests only when
you are not using the machine for another task.

```powershell
# Injects synthetic A key events on an idle interactive desktop.
$env:APEX_TEST_DESKTOP_INPUT = '1'
dotnet test tests/ApexMapper.Input.Tests -c Release
Remove-Item Env:APEX_TEST_DESKTOP_INPUT

# Requires a Windows machine WITHOUT ViGEmBus.
$env:APEX_TEST_DRIVERLESS = '1'
dotnet test tests/ApexMapper.Output.Tests -c Release
Remove-Item Env:APEX_TEST_DRIVERLESS

# Requires ViGEmBus and no other XInput controllers. Creates and removes a pad.
$env:APEX_TEST_LIVE_OUTPUT = '1'
dotnet test tests/ApexMapper.Output.Tests -c Release --filter FullyQualifiedName~ViGEmLiveOutputTests
Remove-Item Env:APEX_TEST_LIVE_OUTPUT
```

Synthetic input does not validate physical keyboard mapping. Before a release,
test partial/full presses and releases, simultaneous clutch and shifting,
handbrake, app switching, Stop, disconnects and process exit in the actual game.

## Package

Publish the app and supervisor into the same fresh directory, then build the
MSI with WiX. .NET restores the WiX SDK as part of the installer build.

```powershell
dotnet publish src/ApexMapper.App -c Release -r win-x64 --self-contained true -o artifacts/staging
dotnet publish src/ApexMapper.Supervisor -c Release -r win-x64 --self-contained true -o artifacts/staging
dotnet build installer/ApexAnalogMapper.wixproj -c Release -p:StagingDir="$PWD/artifacts/staging" -p:ProductVersion=0.5.0
```

The app version is `0.5.0-alpha.1`; the numeric MSI version is `0.5.0`. Subsequent
MSI upgrades need a higher numeric product version. The tag-driven release
workflow produces an MSI and SHA-256 manifest. Tagging and publishing follow
review and physical acceptance.

## License

MIT
