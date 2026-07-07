# Security Policy

Apex Analog Mapper reads global keyboard input and drives a virtual game
controller, so it is a security-relevant tool. This document describes what it
touches, how it behaves, and how to report a vulnerability.

## What the app touches

- **Global keyboard input.** On Windows the app reads keyboard events through
  Raw Input and HID (`src/ApexMapper.Input/`) so it can map keys to a virtual
  Xbox controller. Keystroke data is processed in memory only. The local log
  store records session state, drop counts, profile identifiers, and error
  messages — it never records which keys are pressed.
- **A virtual controller.** Output goes through the ViGEmBus driver (see below).

## Network posture

Version 0.1 makes no network connections. There is no telemetry and no
auto-update; the app does not phone home.

## Inter-process communication

The tray application and the supervisor process talk over a named pipe created
with `PipeOptions.CurrentUserOnly` on both the server and the client, so only
the current user's processes can connect. No network socket is opened.

## Third-party kernel driver (ViGEmBus)

Virtual-controller output requires the ViGEmBus kernel driver. ViGEmBus is
**not bundled** and is **never silently installed**. When it is missing the app
reports the problem and directs you to install it yourself. Three things to
know before you do:

- **Official source only.** Download ViGEmBus exclusively from the official
  GitHub releases page: https://github.com/nefarius/ViGEmBus/releases. Top
  search results for "ViGEmBus download" are third-party mirror sites — a
  classic vector for bundled adware and malware. No mirror is official.
- **ViGEmBus is end-of-life.** Nefarius retired and archived the project in
  November 2023; v1.22.0 is the final release and no future updates or fixes
  will be published. Its successor (Nefarius VirtualPad) is commercial and not
  publicly available, and no maintained open fork exists. ViGEmBus nonetheless
  remains the ecosystem-standard choice — actively maintained projects such as
  DS4Windows still ship it — and this app's output layer sits behind an
  `IControllerOutput` abstraction so it can move to a replacement if a viable
  one emerges.
- **The official binary's integrity is machine-verified.** The driver from the
  official source is attestation/WHQL-signed by Nefarius Software Solutions
  e.U., and 64-bit Windows independently verifies that signature at install
  time and again every time the driver loads — a tampered binary will not
  load. That protects the binary's integrity in transit; it does not protect
  against bugs in the driver itself.

## Unsigned binaries

Releases are unsigned — there is no code-signing certificate. Windows SmartScreen
and antivirus prompts are expected the first time you run a release. Verify the
checksums published on the release page before running a download.

## Anti-cheat stance

The app **detects and disables**, it never evades. It observes the running
process list and the foreground executable name; when it sees known anti-cheat
software it disables auto-enable. It never injects code, never reads another
process's memory, and never installs a filter driver. Using this tool with
online games that run anti-cheat may still violate those games' terms of
service — that is your responsibility.

## Supported versions

This is pre-1.0 software. Only the latest release receives fixes.

## Reporting a vulnerability

Please report privately rather than opening a public issue:

- Use GitHub's **"Report a vulnerability"** button under the repository's
  **Security** tab (private vulnerability reporting), or
- Email **lavindeepdhillon@gmail.com**.

There is no bounty program. This is a personal project, so responses are
best-effort, but security reports are taken seriously and will be acknowledged.
