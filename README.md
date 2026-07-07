# Apex Analog Mapper

Apex Analog Mapper is a Windows-focused tool for turning selected SteelSeries
Apex Pro keyboard input into virtual Xbox controller input. The goal is to make
analog-friendly games see smooth steering, throttle, brake, and controller
buttons from a keyboard setup.

The project is under active development, and the full software pipeline is now
in place. The current build contains the cross-platform mapping core (per-binding
deadzones and curves, synthetic ramps, SOCD resolution with hysteresis, and a
store-level safety gate), profile and device-registry persistence with atomic
writes, rolling backups, corrupt-file recovery, and lazy schema migration, a
rotating log store, default profile loading, the Windows Raw Input digital path
with per-device filtering and phantom-key suppression, an exploratory HID analog
path, a dedicated ~1 ms mapping engine with a zero-allocation steady state, a
named-pipe supervisor process that owns the virtual pad and zeroes it the moment
liveness is lost, ViGEm-based Xbox controller output, anti-cheat detection with
fail-closed pre-flight checks, and a system-tray shell that drives the whole
pipeline end to end. The diagnostics components (a latency HDR histogram and a
log tail) are built and tested but not yet surfaced in the UI. What remains is
signed distribution, an updater, and validation on real hardware.

## Why This Exists

SteelSeries Apex Pro boards use Hall-effect/OmniPoint switches, but SteelSeries
does not expose a public per-key analog input API for games, and no public
protocol has been found that reads per-key analog travel from the board. Games
generally know how to consume analog values from a controller, not from a
keyboard.

This app is intended to bridge that gap:

- read normal keyboard events through Windows Raw Input
- treat any model-specific HID analog reports as exploratory, not the primary
  path, until they can be proven safe on real hardware
- fall back to synthetic analog ramps and curves when true travel is unavailable
  (this synthetic-from-digital path is what the shipped product actually maps)
- map keys to a virtual Xbox-style controller state
- keep safety behavior explicit: a separate supervisor process owns the virtual
  pad and zeroes and disconnects it the moment liveness is lost, so stuck
  throttle/brake/steering cannot survive a disconnect, enable or disable,
  profile switch (including a hot reload of the active profile), sleep/resume,
  panic, or crash. On any of these a currently-held key is gated —
  it must be released once before it maps again (end-to-end on real hardware
  still pending). The supervisor process exits on its own roughly a minute
  after the tray disconnects or exits; re-enabling mapping starts a fresh one
  automatically

## Current Status

Legend: ✅ done and tested · 🚧 in progress on this branch · ⏳ not started / not built

| Area | Status |
| --- | --- |
| Mapping core: curves, per-binding deadzones, synthetic ramps | ✅ Implemented and tested |
| SOCD resolution with stronger-analog hysteresis | ✅ Implemented and tested |
| Store-level safety gate (held keys cannot latch across transitions) | ✅ Implemented and tested |
| Profiles, device registry, atomic writes, backups, recovery, migration (v1→v2) | ✅ Implemented and tested |
| Logging with rotation | ✅ Implemented and tested |
| Default racing profile | ✅ Implemented |
| Raw Input digital path: per-device filtering, phantom-key suppression | ✅ Implemented, verified on Windows CI |
| Mapping engine: ~1 ms tick, measured dt, zero-alloc steady state, atomic hot-swap | ✅ Implemented and tested |
| Named-pipe IPC: length-prefixed MessagePack frames, per-session, current-user only | ✅ Implemented and tested |
| Supervisor: owns the pad, heartbeat watchdog, fail-closed zero+disconnect, idle self-exit | ✅ Implemented and tested |
| Virtual Xbox (ViGEm) output: atomic submit, fail-closed packing | ✅ Implemented and unit-tested; live pad needs ViGEmBus on a real desktop |
| Anti-cheat detection & ViGEmBus pre-flight (detect-and-disable, fail-closed) | ✅ Implemented and tested |
| Tray shell: toggle drives preflight → supervisor → engine, zero on exit | ✅ Implemented, verified on Windows CI |
| HID analog path (feature/input routing, numbered reports, calibration) | 🚧 Exploratory; unproven without hardware |
| Calibration wizard | 🚧 Present but stub-gated; raw capture pending |
| Diagnostics components (latency histogram, log tail) | 🚧 Built and tested, not yet wired into the app shell |
| Per-user MSI installer (unsigned, built and verified in CI) | ✅ Released (v0.1.0, with SHA-256 checksums); not hardware-tested |
| Signed distribution, auto-updater | ⏳ Not started |
| Real Apex Pro hardware verification | ⏳ Pending — no hardware available |

## Testing and Verification

This project is developed without an Apex Pro keyboard or Windows hardware. All
verification is automated: unit tests locally and on GitHub Actions.

- **macOS / cross-platform job** builds and runs the cross-platform subset:
  **645 tests** — ApexMapper.Core (149), ApexMapper.Input.Abstractions (239),
  ApexMapper.Persistence (73), ApexMapper.Logging (20), the IPC frame codec and
  transport (52), ApexMapper.Output (63), and ApexMapper.Supervisor (49). The
  supervisor, IPC, and output logic run over real named pipes on the dev box, so
  everything except the ViGEm P/Invoke layer is exercised locally.
- **Windows job** builds the full solution and additionally runs the
  Windows-only suites — ApexMapper.Input (Raw Input / HidSharp adapters) and
  ApexMapper.App (217) — for roughly **870 tests across nine assemblies**.

Because the Windows runner is Windows Server, ViGEmBus cannot load there, so
there are **no end-to-end virtual-pad tests anywhere in CI**. End-to-end behavior
on a real desktop, and any in-game validation (including in-game stick
direction), are **pending** and are not claimed as verified.

## Repository Layout

```text
src/
  ApexMapper.Core/                 Mapping pipeline, curves, ramps, SOCD
  ApexMapper.Input.Abstractions/   Cross-platform input contracts, decoder, host, store
  ApexMapper.Input/                Windows Raw Input and HidSharp adapters
  ApexMapper.Output/               ViGEm output, IPC frames/transport, detection, preflight
  ApexMapper.Supervisor/           Supervisor process (owns the virtual pad, heartbeat watchdog)
  ApexMapper.App/                  WPF tray shell, pipeline wiring, diagnostics components
  ApexMapper.Persistence/          Profiles, registry, migrations, recovery, atomic files
  ApexMapper.Logging/              Local rotating log store
  ApexMapper.Profiles/             Embedded default profiles

tests/
  ApexMapper.Core.Tests/
  ApexMapper.Input.Abstractions.Tests/
  ApexMapper.Input.Tests/                Windows-only
  ApexMapper.Persistence.Tests/
  ApexMapper.Logging.Tests/
  ApexMapper.App.Tests/                  Windows-only
  ApexMapper.Output.Tests/
  ApexMapper.Supervisor.Tests/
  ApexMapper.Ipc.Tests/

perf/
  ApexMapper.Core.Benchmarks/
```

## Installation

Prebuilt Windows installers are published on the
[Releases](https://github.com/lavindeep/apex-analog-mapper/releases) page.

1. Download the latest `ApexAnalogMapper-<version>.msi`.
2. Run it. Because the installer is **unsigned**, Windows SmartScreen warns that
   the publisher is unrecognized — choose **More info → Run anyway**. See
   [SECURITY.md](SECURITY.md) for why it is unsigned and how to check what you
   are running.
3. The MSI installs **per user** with no administrator prompt into
   `%LocalAppData%\Programs\Apex Analog Mapper` and adds a Start Menu shortcut.
   Both `ApexMapper.exe` and its supervisor process are self-contained, so no
   separate .NET runtime install is required.

### Prerequisite: ViGEmBus

Virtual-controller output needs the ViGEmBus driver. It is **not** bundled and is
never installed silently. Install it separately from the
[official ViGEmBus releases](https://github.com/nefarius/ViGEmBus/releases)
(v1.22.0). The app detects a missing driver and prompts you when you enable
mapping.

### Uninstalling

Uninstall from **Windows Settings → Apps → Installed apps → Apex Analog Mapper**
(or Control Panel's Programs and Features). The installer removes only the files
and the shortcut it created. If you turned on start-with-Windows, **disable it in
the app first** — the login task is created and removed by the app, not by the
installer.

Building from source is covered under [Build](#build) below.

## Build

Requirements:

- .NET 8 SDK
- Windows 10/11 for the full app/input/output build (WPF and Windows-only input)
- Windows Desktop SDK support for WPF projects
- ViGEmBus (v1.22.0) installed at runtime for virtual-pad output — it is a
  user-installed prerequisite and is never auto-installed by the app

Cross-platform core subset (builds and tests on macOS/Linux/Windows):

```bash
dotnet build ApexAnalogMapper.CrossPlatform.slnf
dotnet test ApexAnalogMapper.CrossPlatform.slnf
```

Full Windows solution:

```bash
dotnet build ApexAnalogMapper.sln
dotnet test ApexAnalogMapper.sln
```

## Known Limitations

- No public protocol has been found that reads Apex Pro per-key analog travel, so
  the HID analog path is exploratory: the shipped adapter's analog key map is
  empty and the analog probe is never wired to a device at runtime. The reliable
  path is digital Raw Input mapped through synthetic ramps and curves.
- The calibration wizard is present in the UI but stub-gated — the raw-capture
  plumbing it needs is not built yet.
- No real-hardware or in-game validation has been performed. Development uses
  unit tests and GitHub Actions only, and CI cannot load ViGEmBus, so the live
  virtual-pad submission path is not exercised anywhere in CI.
- Under a sustained input flood the bounded event ring can drop raw events,
  including a key release. A dropped release leaves that key's axis held until
  the key is pressed and released once more (or mapping is toggled off and on,
  which gates held keys). The app counts every drop and logs a warning on the
  first overflow.
- Some runtime behaviors cannot be exercised in CI and are deliberately recorded
  as untested rather than claimed as verified:
  - virtual-pad removal when the supervisor process dies (driver-owned behavior),
  - end-to-end behavior when either process is force-killed,
  - the supervisor's forced-exit path when a shutdown wedges,
  - WPF application-exit teardown ordering,
  - live ViGEmBus pad behaviors — only the driverless failure paths run in CI.
- ViGEmBus is a user-installed runtime prerequisite for output.
- Binaries and the MSI installer are unsigned (no code-signing certificate), so
  SmartScreen will warn on first run. There is no auto-updater yet.

## License

MIT
