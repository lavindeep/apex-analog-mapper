# Apex Analog Mapper

Apex Analog Mapper turns how far you press a key on a SteelSeries Apex Pro into a
virtual Xbox 360 controller. W becomes a throttle, S a brake, and A and D a steering
stick that moves as far as you press.

It is a Windows app, MIT licensed, and in alpha.

## Why I built it

The Apex Pro's switches are hall effect sensors. The keyboard knows how far down every
key is, but games only ever see on and off. I wanted to drive Forza Horizon 5 and 6
with W and S as real throttle and brake and A and D as real steering, the way a pad
does it. SteelSeries doesn't expose the key depth to games, so this app reads it from
the keyboard and hands it to the game as a controller.

Forza is what I play and what the default profile is set up for. Any game that takes an
Xbox controller works.

## What you need

- Windows 10 or 11, 64-bit.
- An Apex Pro. The sensor readout has been checked on the Apex Pro and Apex Pro TKL
  with firmware 4.9.1 and 4.16.8. Other Apex Pro models show up as untested. The app
  tells you the risk and asks before it reads their sensors.
- The ViGEmBus driver, which creates the virtual controller. See below.

## Install

1. Download `ApexAnalogMapper-win-Setup.exe` from the
   [latest release](https://github.com/lavindeep/apex-analog-mapper/releases).
2. Check its hash. The release notes list the SHA-256 of every file. In PowerShell:
   `Get-FileHash .\ApexAnalogMapper-win-Setup.exe`. The two should match.
3. Run it. The installer isn't code-signed, so SmartScreen says it doesn't recognize
   the app. Choose More info, then Run anyway.

The installer is about 70 MB and the app takes about 140 MB once installed, because it
carries its own copy of .NET. It installs for your user only, with no administrator
prompt, adds Start menu and desktop shortcuts, and shows up in Settings > Apps for
uninstalling.

If you installed v0.1.0 from its MSI, uninstall that first. The two versions share
nothing.

### The controller driver

The virtual controller needs [ViGEmBus 1.22.0](https://github.com/nefarius/ViGEmBus/releases).
Download it from that page only. Searching for it turns up mirror sites, and none of
them are official. The app never downloads or installs the driver for you. When it's
missing, the Setup card opens by itself with a button to the release page.

ViGEmBus is finished. Its author archived it in 2023 and 1.22.0 is the last release.
It is still what almost every virtual controller tool on Windows uses, and Windows
checks its signature every time it loads.

## Getting started

1. **Keyboard.** Plug in the Apex Pro and pick it on the keyboard card.
2. **Game.** Start the game once, then pick it on the game card. The app remembers the
   game by its executable, so a restarted game keeps working.
3. **Calibrate.** Every key the profile reads for depth needs calibrating once. For
   W, A, S and D in the Forza profile:
   1. Open the calibration card. Each key has a bar showing its sensor reading.
   2. With the key up and your hands off the keyboard, press **Set released**.
   3. Hold the key all the way down and press **Set fully pressed** with the mouse.
      Keep holding until the row says it saved.
4. **Start.** Press Start. The app waits for the game to come to the front, then
   blocks W, A, S and D from reaching it as keys and sends them as the controller
   instead.

The calibration is per keyboard and firmware version. A key that reaches full output
before it bottoms out says so on its row, which just means the last bit of travel does
nothing.

## Playing

- **Start before the game, if you can.** Start works either way, but some games only
  look for controllers when they launch, and the virtual controller only exists while
  mapping runs.
- **Steam Input.** If the game runs through Steam, turn Steam Input off for it.
  Otherwise Steam grabs the virtual controller and may remap or hide it.
- **Stopping.** Stop on the status card, or Ctrl+Left Alt+F12 from anywhere, even with
  the game in front. The controller goes to rest and disappears, and your keyboard
  is normal again at once.
- **Alt-tab.** Keys are only blocked while the game is in front. A key you're holding
  when you come back to the game stays off until you let go of it once. The app didn't
  see it go down, so it waits to see it come up before trusting it.
- **Every keyboard.** While mapping, the mapped keys are blocked on every keyboard
  plugged in, not just the Apex Pro. Windows doesn't tell a keyboard hook which
  keyboard a key came from. The app notices and tells you once.
- **Games that run as administrator.** If the game runs as administrator and this app
  doesn't, the app leaves the game alone. Your keys reach it as plain key presses and
  the controller stays at rest. The game card says so. Close the app and run it as
  administrator too.
- **No keyboard settings to change.** Rapid trigger and actuation point only change
  when the keyboard sends key presses. The app reads key depth directly, so leave them
  however you like them.

If the keyboard stops answering mid-race, the analog keys fall back to on and off with
a short ramp, and the status card says so until the readings come back. That's there
so a hiccup doesn't cut your throttle mid-corner. It isn't meant to be driven on.

## Updates

The app looks for a newer version on GitHub when it starts, at most once every six
hours. Turn that off on the Setup card if you'd rather check by hand. Nothing
downloads until you press Update, and the new version installs when you restart the
app, never while mapping runs. While you're on a test version like an alpha, you're
offered the next test version too.

## Troubleshooting

- **The game doesn't react to the controller.** Check that Steam Input is off for the
  game, that the game is set to use a controller, and that you started mapping before
  launching it.
- **Keys still type in the game.** The app only blocks keys while the chosen game is in
  front. Check that the game card shows the right game, and that it has no warning
  about running as administrator.
- **Start stays greyed out.** The status card says what's missing, with a button to
  the fix when there is one.
- **A key's calibration is refused.** Let go of the key and every key near it, then
  press Set released again. A key held during that step is caught and nothing is saved.
- **Something else.** The Setup card opens the folder with the log. It records
  sessions and faults, never which keys you pressed. Attach it to a
  [new issue](https://github.com/lavindeep/apex-analog-mapper/issues/new).

## Uninstall

Settings > Apps > Apex Analog Mapper > Uninstall. Your profiles, calibration and log
stay in `%AppData%\ApexAnalogMapper`. Delete that folder if you want them gone too.

## What it never does

The app never injects anything into a game, never reads another program's memory, and
never installs a driver of its own. It reads the keyboard, blocks keys with a normal
Windows keyboard hook, and drives a controller through ViGEmBus. Using it in an online
game with anti-cheat may still break that game's rules, and that's your call.
[SECURITY.md](SECURITY.md) lists everything the app touches.

## Building from source

You need Windows and the .NET 10 SDK.

```
dotnet build ApexAnalogMapper.slnx
dotnet test ApexAnalogMapper.slnx
```

`scripts/pack.ps1 -Version 0.5.0` builds the installer with the
[Velopack](https://velopack.io) CLI (`dotnet tool install -g vpk --version 1.2.0`).
[CONTRIBUTING.md](CONTRIBUTING.md) covers the hardware tests and how to send a change.

## License

MIT. See [LICENSE](LICENSE).
