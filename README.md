# Apex Analog Mapper

Apex Analog Mapper is a Windows-focused tool for turning selected SteelSeries
Apex Pro keyboard input into virtual Xbox controller input. The goal is to make
analog-friendly games see smooth steering, throttle, brake, and controller
buttons from a keyboard setup.

The project is still under active development. The current stable base contains
the cross-platform mapping core, profile/device persistence, logging, default
profile loading, Raw Input scaffolding, HID polling/parsing infrastructure, and
input-host fallback behavior. The virtual-controller output path, production tray
UI integration, diagnostics UI, installer, and hardware validation are still in
progress.

## Why This Exists

SteelSeries Apex Pro boards use Hall-effect/OmniPoint switches, but SteelSeries
does not expose a public per-key analog input API for games. Games generally know
how to consume analog values from a controller, not from a keyboard.

This app is intended to bridge that gap:

- read normal keyboard events through Windows Raw Input
- optionally read model-specific HID analog reports when they can be proven safe
- fall back to synthetic analog ramps when true travel is unavailable
- map keys to a virtual Xbox-style controller state
- keep safety behavior explicit so stuck throttle/brake/steering cannot survive
  disconnects, toggles, profile switches, or crashes once the output supervisor
  is complete

## Current Status

| Area | Status |
| --- | --- |
| Mapping core | Implemented and tested |
| Profiles, device registry, logging | Implemented and tested |
| Default racing profile | Implemented |
| Raw Input and HID input foundation | Implemented, needs Windows CI rerun/validation |
| Virtual Xbox output | In progress on a separate branch |
| Tray UI | In progress on a separate branch |
| Diagnostics | In progress on a separate branch |
| Installer and signed distribution | Not started |
| Real Apex Pro hardware verification | Not complete |

Phase 4 tray work exists on `worktree-phase-4-tray-ui`, but it was developed
from an older base and should be rebased or cherry-picked carefully so it does
not overwrite newer input work. Phase 3 and Phase 5 are not treated as complete.

## Repository Layout

```text
src/
  ApexMapper.Core/                 Mapping pipeline, curves, ramps, SOCD
  ApexMapper.Input.Abstractions/   Cross-platform input contracts and parsers
  ApexMapper.Input/                Windows Raw Input and HidSharp adapters
  ApexMapper.Output/               Virtual controller contracts and early IPC types
  ApexMapper.Supervisor/           Supervisor process scaffold
  ApexMapper.App/                  WPF app scaffold
  ApexMapper.Persistence/          Profiles, registry, migrations, atomic files
  ApexMapper.Logging/              Local rotating log store
  ApexMapper.Profiles/             Embedded default profiles

tests/
  ApexMapper.Core.Tests/
  ApexMapper.Input.Abstractions.Tests/
  ApexMapper.Input.Tests/
  ApexMapper.Persistence.Tests/
  ApexMapper.Logging.Tests/
  ApexMapper.Output.Tests/
  ApexMapper.Supervisor.Tests/
  ApexMapper.Ipc.Tests/

perf/
  ApexMapper.Core.Benchmarks/
```

## Build

Requirements:

- .NET 8 SDK
- Windows 11 for full app/input/output builds
- Windows Desktop SDK support for WPF projects

Cross-platform core subset:

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

- True Apex Pro per-key analog HID reports are not production-ready; digital
  Raw Input plus synthetic ramps is the reliable fallback path.
- The current public base is not an end-to-end usable tray app yet.
- A previous Windows CI build failed in the Windows-only input test project
  because of a missing namespace import; the code now includes that fix, but
  Windows CI still needs to be rerun.
- Virtual controller output and supervisor heartbeat behavior are not complete in
  the stable branch.

## License

MIT
