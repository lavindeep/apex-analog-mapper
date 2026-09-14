# Apex Analog Mapper

Use key depth to control a virtual Xbox 360 controller on Windows. A light press
can produce partial throttle or steering. A deeper press produces more input.

This guide covers v0.5 alpha. You need Windows 10 or 11 x64 and the original
SteelSeries Apex Pro TKL with USB ID `1038:1614` and firmware `4.9.1`.
Analog input has been tested with W, A, S, and D on that keyboard.
Other models and firmware versions are not supported.

## Install or upgrade Mapper

Use these steps to install the app:

1. Install [ViGEmBus 1.22.0](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0)
   if it is missing. This separate driver installation requires administrator
   approval. ViGEmBus is no longer maintained, so use the official download.
2. Download the Mapper MSI and `SHA256SUMS.txt` from the repository's
   [Releases](https://github.com/lavindeep/apex-analog-mapper/releases), or use the
   alpha files provided for testing.
3. Before running the unsigned MSI, compare its SHA-256 hash with the manifest.
   For this alpha, run the following command in the download folder:

   ```powershell
   Get-FileHash .\apex-analog-mapper-0.5.0-alpha.1.msi -Algorithm SHA256
   ```

4. If Mapper is open, close it.
5. Run the MSI.

The installer replaces an older MSI installation and keeps your bindings and
calibration. You do not need to install .NET separately.

## Choose your keyboard and bindings

Set up the keys before you calibrate them:

1. Open Apex Analog Mapper.
2. Choose your keyboard in **Keyboard**.
3. Choose **Racing** in **Profile**.
4. To change the preset, click **Edit bindings**.
5. Choose a controller output for each binding you want to change.
6. Click that binding's key button.
7. Press the replacement key.
8. Click **Save** when you finish.

To add a binding, use **Add key** for one key or **Add stick axis** for two
direction keys. Ctrl, Alt, Windows, and F12 are reserved for shortcuts.
Left Shift is available for the clutch.

The [Racing bindings](docs/reference.md#racing-bindings) match Forza's Default
Layout 1 and WASD driving controls. Upgrades keep your saved profile, including
any earlier bindings or timing settings.

## Calibrate the analog keys

Calibrate each listed key before the first test. Repeat calibration after you
change analog bindings or if a key reaches full output too early.

1. Click **Calibrate keys**.
2. Release the first key.
3. Click **Set released** on that key's row.
4. Hold the key fully down.
5. While holding it, click **Set fully pressed** on the same row.
6. Repeat steps 2 through 5 for every listed key.
7. Click **Save**.

The [analog input reference](docs/reference.md#analog-input) describes the saved
calibration and response curve. GG's actuation setting controls ordinary
keyboard presses. Mapper reads separate sensor values for analog input.

## Start a game session

Open the game before selecting it in Mapper:

1. Choose the game's window in **Game**. If it is missing, click **Refresh**.
2. Release the mapped keys.
3. Click **Start**.
4. Switch to the game.

Keep Mapper open while you play. Closing its window exits the app.
The global Stop shortcut is **Ctrl+Alt+F12**.

If you switch to another app, Mapper restores keyboard input and returns the
controller to zero. When you return to the game, release any held keys before
pressing them again. Opening a binding or calibration editor stops the session.

After restarting the game or Mapper, select the game again before starting.

## Test your controls

Test the following in the game:

- Press W and S slowly through their travel. Check for partial throttle and brake.
- Press A and D slowly, then release them. Check steering response and return to centre.
- Hold Left Shift and press Q or E. Check clutch and shifting together.
- Press Space to check the handbrake.
- Check that these presses keep the game's controller prompts on screen.
- Switch to another app and back. Release the keys before testing another press.

Mapped keys are blocked on every keyboard while you use the selected game.
Digital controller bindings also work from every keyboard during that session.
Analog input comes from the selected keyboard. See
[keyboard filtering](docs/reference.md#keyboard-filtering) for the full behavior.

## Fix common problems

### Keys do not respond

Check the selected keyboard and its USB ID against the supported model above.
If the keyboard has more than one input source, open **Advanced** and try the
other **Input source**. Windows can expose several interfaces for one keyboard.

If Mapper reports missing calibration, set both positions for every key in
**Calibrate keys**. If a key stops responding after Start or an app switch,
release the key fully before pressing it again.

### The game still switches to keyboard input

Check that Mapper shows **Running** and that **Game** names the actual game
window. Unmapped keys still reach the game as keyboard input. Shortcuts that
use Ctrl, Alt, or Windows also pass through.

### The virtual controller is missing

Check that ViGEmBus is installed. If its installer asks for a restart, restart
Windows before reopening Mapper.

### An upgrade keeps the old steering response

Upgrades preserve your saved profile. To change curves or timing, edit the
profile JSON described in [profile settings](docs/reference.md#profile-settings).
The binding editor does not change those values.

### A problem remains

Include the app version, keyboard USB ID, firmware, selected input source, game,
and reproduction steps in your bug report. The controller log is at
`%LocalAppData%\ApexAnalogMapper\logs\supervisor.log`.

## Back up settings or remove the app

Before editing profile files or moving settings, copy `%AppData%\ApexMapper` to
a backup folder. If you use a different physical keyboard, calibrate it again.
See [stored files](docs/reference.md#stored-files) for each file's contents.

To uninstall Mapper, use Windows Installed apps. Uninstallation keeps your user
data. If an older build created a login task, remove that task separately.

## Build from source

Follow [Build, test, and package Mapper](docs/development.md) for the .NET commands
and the tests that need a physical Windows desktop.

See [SECURITY.md](SECURITY.md) for input handling and vulnerability reporting.

## License

MIT
