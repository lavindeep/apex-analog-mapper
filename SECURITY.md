# Security policy

Apex Analog Mapper reads global keyboard input and drives a virtual game controller,
so it is a security-relevant tool. This document says what it touches, how it behaves,
and how to report a vulnerability.

## What the app touches

- Global keyboard input. The app reads keyboard events through Raw Input, reads key
  depth through the keyboard's own vendor HID interface, and installs a low-level
  keyboard hook. While the selected game is in the foreground, the hook blocks the
  mapped keys so the game sees controller input instead of keystrokes. Keystroke data
  stays in memory. The log records session events and faults, never which keys were
  pressed.
- The keyboard's vendor interface. The app sends exactly two commands to it: a
  firmware version query and a sensor read. Nothing else is ever written to the
  keyboard, and the app never changes firmware or actuation settings.
- A virtual controller through the ViGEmBus driver.

## Network posture

The app makes one kind of network request. It asks this repository's GitHub Releases
whether there is a newer version, when it starts and at most once every six hours, and
whenever you press Check for updates. The check on start can be turned off on the Setup
card. A new version downloads only when you press Update, Velopack checks the download
against the hash in the release's feed, and it installs only when you restart the app,
never while mapping runs. There is no telemetry.

## What the installer touches

Setup.exe installs the app for the current user into `%LocalAppData%\ApexAnalogMapper`,
adds Start menu and desktop shortcuts, and registers an uninstall entry for the current
user. It needs no administrator rights and adds no service, driver or scheduled task.
Uninstalling removes all of that. Profiles, calibration, settings and the log live in
`%AppData%\ApexAnalogMapper` and stay after an uninstall.

## Third-party kernel driver (ViGEmBus)

Virtual-controller output requires the ViGEmBus kernel driver. The app does not
bundle it, never downloads it, and never installs it. When the driver is missing the
app shows the version it needs and a button that opens the official releases page,
https://github.com/nefarius/ViGEmBus/releases, in your browser. Download it only from
there. Search results for "ViGEmBus download" include third-party mirrors, and no
mirror is official.

ViGEmBus is end of life. Its author archived the project in November 2023 and 1.22.0 is
the final release. The official binary is signed by Nefarius Software Solutions e.U.,
and 64-bit Windows checks that signature every time the driver loads.

## Unsigned binaries

Releases are unsigned. Windows SmartScreen will warn the first time you run the
installer. Every release lists the SHA-256 of each file in its notes and in
`SHA256SUMS.txt`. Check the installer's hash before running it.

## What the app never does

It never injects code into a game, never reads another process's memory, and never
installs a filter driver. Using it with online games that run anti-cheat may still
violate those games' terms of service. That is your call.

## Reporting a vulnerability

Open a public issue. This is a personal project with no private channel and no
bounty; I answer issues and pull requests, not email. Describe what the app does that
it should not, how you found it, and the version you ran. Responses are best effort.
