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
reports the problem and directs you to install it yourself from the official
source (https://github.com/nefarius/ViGEmBus/releases).

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
