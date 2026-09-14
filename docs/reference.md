# v0.5 alpha reference

This page lists the supported hardware, preset bindings, input behavior, and
saved files for Apex Analog Mapper `0.5.0-alpha.1`.

## Supported hardware

The analog reader supports the original SteelSeries Apex Pro TKL with USB vendor
ID `1038`, product ID `1614`, and firmware `4.9.1`. W, A, S, and D have physical
test evidence. Other sensor positions come from the firmware map and still need
physical testing. Other keyboard models and firmware versions are unsupported.

Mechanical keys, including this keyboard's arrows, produce digital input.
The app does not change firmware or SteelSeries actuation settings.

## Racing bindings

These actions match Forza's Default Layout 1 controller layout and WASD keyboard
layout. The game's selected controller layout determines the action for each
controller output.

| Action | Keyboard | Controller output |
| --- | --- | --- |
| Accelerate | W | RT, analog |
| Brake | S | LT, analog |
| Steer left | A | Left stick left, analog |
| Steer right | D | Left stick right, analog |
| E-brake | Space | A |
| Rewind | R | Y |
| Shift up | E | B |
| Shift down | Q | X |
| Clutch | Left Shift | LB |
| AutoDrive cinematic camera | Left Shift | LB |
| Switch camera | Tab | RB |
| Toggle convertible | G | Left stick click |
| Look left | Left arrow | Right stick left |
| Look right | Right arrow | Right stick right |
| Look back | Down arrow | Right stick down |
| Look forward | Up arrow | Right stick up |

Ctrl, Alt, Windows, and F12 cannot be mapped. Left Shift can be held together
with Q or E for clutch and shifting.

## Analog input

GG's actuation setting determines when a key produces an ordinary keyboard
press. That event is on or off, even with actuation set to 3 or 4 mm.

Mapper reads separate sensor reports. Calibration converts each key's released
and fully pressed readings into an input between zero and full output.
The percentage represents calibrated sensor input, not millimetres or pressure.
Calibration belongs to the physical keyboard and firmware.

The Racing curve increases gradually through the first 70% of calibrated input.
The final 30% increases more steeply toward full output. The preset adds no
steering press or release delay.

Analog input requires calibration and fresh sensor reports. Missing calibration,
stale reports, or connection faults prevent analog output. Supported analog keys
do not fall back to simulated input from ordinary keyboard presses.

## Keyboard filtering

When the selected game is the active window, Mapper blocks mapped keyboard
presses and sends controller input instead. The filter includes clutch,
shifting, and handbrake. Unmapped keys and shortcuts that use Ctrl, Alt, or
Windows remain available.

The Windows keyboard hook does not identify which physical keyboard produced a
press. Mapped digital bindings work from every keyboard during a game session.
Mapped keyboard presses are also blocked on every keyboard during that session.
Analog input comes from the selected keyboard's sensors.

Switching to another app restores normal keyboard input and zeros controller
output. Held keys remain inactive until released. Stop, game exit, controller
disconnection, profile changes, device changes, and input faults stop the session
or clear held input. Mapping never starts automatically.

A separate supervisor process owns the virtual controller. If Mapper exits or
stops sending its heartbeat, the supervisor zeros and disconnects the controller.
The app does not inject code into games or install a keyboard filter driver.

Start runs driver and anti-cheat checks. An anti-cheat check does not establish
whether a game permits virtual controllers. The app asks for confirmation when
it detects an anti-cheat signal or cannot complete the check.

## Profile settings

The binding editor changes keys and controller outputs. Curves and timing are
stored in the profile JSON. Direct file edits reload automatically and can stop
the session. Upgrades preserve existing profile settings.

| Setting | Effect |
| --- | --- |
| `curve` | Maps calibrated input to controller output. Each point contains an input value and an output value. |
| `press_ramp_ms` | Controls how long digital input takes to rise. Analog presses follow sensor input directly. |
| `release_ramp_ms` | Controls how long input takes to return to zero. For analog input, a new press or direction reversal responds immediately. |

A ramp value of `0` adds no delay. The current Racing preset sets both steering
ramps to `0`. Stop and held-key resets clear any remaining return movement.

## Stored files

The app and supervisor install together at
`%LocalAppData%\Programs\Apex Analog Mapper`. The installer includes the .NET
runtime. The app version is `0.5.0-alpha.1`; Windows Installed apps shows the
numeric MSI version, `0.5.0`.

The following paths are relative to `%AppData%\ApexMapper`:

| Path | Contents |
| --- | --- |
| `profiles\*.json` | Bindings, curves, and timing |
| `device-registry.json` | Device selection and per-key calibration |
| `profile-pin.json` | Selected profile |
| `panic-policy.json` | Stop shortcut policy |

The supervisor log is at
`%LocalAppData%\ApexAnalogMapper\logs\supervisor.log`. It records controller
session state and errors, not key presses.

Upgrades and uninstallation preserve user data. Game selection is not saved.
It identifies one running process, so a restarted game needs a new selection.
There is no automatic updater. Older app-created login tasks are separate from
the MSI installation.
