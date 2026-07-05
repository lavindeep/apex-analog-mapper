# Apex Analog Mapper

Apex Analog Mapper is a Windows-focused tool for turning selected SteelSeries
Apex Pro keyboard input into virtual Xbox controller input. The goal is to make
analog-friendly games see smooth steering, throttle, brake, and controller
buttons from a keyboard setup.

The project is under active development. The current base contains the
cross-platform mapping core (per-binding deadzones and curves, synthetic ramps,
SOCD resolution with hysteresis, and a store-level safety gate), profile and
device-registry persistence with atomic writes, rolling backups, corrupt-file
recovery, and lazy schema migration, a rotating log store, default profile
loading, the Windows Raw Input digital path with per-device filtering and
phantom-key suppression, an exploratory HID analog path, and diagnostics
components (a latency HDR histogram and a log tail). The virtual-controller
output path, the supervisor, the tray UI, wiring the diagnostics components into
the app shell, the installer, and real-hardware validation are not done yet.

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
- keep safety behavior explicit so stuck throttle/brake/steering cannot survive
  disconnects, toggles, profile switches, or crashes once the output supervisor
  is complete

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
| HID analog path (feature/input routing, numbered reports, calibration) | 🚧 Exploratory; unproven without hardware |
| Diagnostics components (latency histogram, log tail) | 🚧 Built and tested, not yet wired into the app shell |
| Virtual Xbox (ViGEm) output | ⏳ Contracts only; not implemented |
| Supervisor / IPC (heartbeat, zero-on-fault) | ⏳ Contracts and scaffold only |
| Tray UI | ⏳ Prototype on an unmerged remote branch only |
| Installer, signed distribution, updater | ⏳ Not started |
| Real Apex Pro hardware verification | ⏳ Pending — no hardware available |

An earlier tray-UI prototype exists on the remote branch
`worktree-phase-4-tray-ui`, developed from an older base. It is not merged and
must be rebased or cherry-picked carefully so it does not overwrite newer input
work.

## Testing and Verification

This project is developed without an Apex Pro keyboard or Windows hardware. All
verification is automated: unit tests locally and on GitHub Actions.

- **macOS / cross-platform job** builds and runs the cross-platform subset:
  **466 tests** — ApexMapper.Core (134), ApexMapper.Input.Abstractions (239),
  ApexMapper.Persistence (73), ApexMapper.Logging (20).
- **Windows job** builds the full solution and additionally runs the
  Windows-only suites — ApexMapper.Input (Raw Input / HidSharp adapters) and
  ApexMapper.App — plus the Output, Supervisor, and Ipc placeholder projects.

Because the Windows runner is Windows Server, ViGEm cannot load there, so there
are **no end-to-end virtual-pad tests anywhere in CI**. End-to-end behavior on a
real desktop, and any in-game validation, are **pending** and are not claimed as
verified.

## Repository Layout

```text
src/
  ApexMapper.Core/                 Mapping pipeline, curves, ramps, SOCD
  ApexMapper.Input.Abstractions/   Cross-platform input contracts, decoder, host, store
  ApexMapper.Input/                Windows Raw Input and HidSharp adapters
  ApexMapper.Output/               Virtual controller contracts, IPC frames, preflight
  ApexMapper.Supervisor/           Supervisor process scaffold
  ApexMapper.App/                  WPF app scaffold and diagnostics components
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
  ApexMapper.Output.Tests/               placeholder
  ApexMapper.Supervisor.Tests/           placeholder
  ApexMapper.Ipc.Tests/                  placeholder

perf/
  ApexMapper.Core.Benchmarks/
```

## Build

Requirements:

- .NET 8 SDK
- Windows 10/11 for the full app/input/output build (WPF and Windows-only input)
- Windows Desktop SDK support for WPF projects

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
  the HID analog path is exploratory. The reliable path is digital Raw Input
  mapped through synthetic ramps and curves.
- The current base is not yet an end-to-end usable tray app: there is no virtual
  controller output, supervisor, or tray UI wired up.
- No real-hardware or in-game validation has been performed. Development uses
  unit tests and GitHub Actions only, and CI cannot exercise ViGEm output.
- Binaries are unsigned; there is no installer or updater yet.

## License

MIT
