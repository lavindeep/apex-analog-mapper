# Apex Analog Mapper

Maps the SteelSeries Apex Pro's analog key inputs to a virtual Xbox controller using ViGEm — letting you use analog keystroke depth as thumbstick or trigger axes in any game that supports a gamepad.

## What it does

The Apex Pro exposes each key's actuation depth as a raw HID analog value. This tool reads those values at low latency, applies per-key calibration and curve shaping, resolves SOCD conflicts, and feeds the result into a virtual Xbox controller via the ViGEmBus driver. A Windows tray app manages profiles, hotkeys, and calibration. A separate Supervisor process owns the virtual controller and communicates with the tray app over a named pipe — so the controller keeps its state even if the UI crashes.

## Architecture

```
HID device (Apex Pro)
      │
      ▼
 Input Layer          reads raw analog reports via HidSharp + Raw Input
      │               DeviceSelector → HidAnalogProbe → HidPollLoop → InputHost
      ▼
 Core / Pipeline      pure .NET, cross-platform testable
      │               KeyStateStore → BindingPipeline → VirtualPadState
      │               (DeadzoneCurve, PiecewiseCubicCurve, Ramp, SocdResolver)
      ▼
 Output / Supervisor  Windows-only, separate process
      │               ViGEmXboxOutput → virtual Xbox controller
      │               PipeServer ←──── PipeClient (tray side)
      │               HeartbeatWatchdog: gaps ≥ 1 s → Zero + Disconnect
      ▼
 Tray App (WPF)       system tray icon, hotkeys, profile management
                      ForegroundWatcher → ProfileResolver (per-game profiles)
                      CalibrationWizard, DevicePicker, DiagnosticsTab
```

## Features

- **Analog-to-axis mapping** — full range from resting to fully pressed, with configurable dead zones, cubic curves, and digital-to-analog ramping
- **SOCD resolution** — Neutral, Last-Input-Wins, or Stronger-Analog-Wins for opposing axes
- **Per-game profiles** — auto-switches by foreground exe, Steam AppId, or window title; manual pin overrides
- **Calibration wizard** — guided rest / max / noise capture, stored atomically per device
- **Global panic hotkey** — instantly zeroes and disconnects the virtual controller; persists disable-auto-enable per exe across restarts
- **Anti-cheat awareness** — detects BE, EAC, Vanguard, and FACEIT; suppresses auto-enable when they're running
- **Diagnostics tab** — live key + pad state at 10 Hz, latency histogram (p50/p95/p99), probe runner, log tail, adapter discovery
- **Crash-safe supervisor** — heartbeat watchdog zeroes the pad within 1 s of tray app death; no stuck inputs

## Build

Requirements: .NET 8 SDK, Windows 11 SDK (for the WPF app), [ViGEmBus driver](https://github.com/nefarius/ViGEmBus/releases).

```
dotnet build ApexAnalogMapper.sln
dotnet test ApexAnalogMapper.sln
```

Cross-platform filter (Core + Persistence + Logging only, runs on macOS/Linux CI):

```
dotnet build ApexAnalogMapper.CrossPlatform.slnf
dotnet test ApexAnalogMapper.CrossPlatform.slnf
```

## Project layout

```
src/
  ApexMapper.Core/            binding pipeline, curves, SOCD — no Windows deps
  ApexMapper.Input/           HID + Raw Input, Windows-only
  ApexMapper.Input.Abstractions/
  ApexMapper.Output/          ViGEm adapter, IPC client, preflight, detection
  ApexMapper.Supervisor/      controller host process, pipe server, watchdog
  ApexMapper.App/             WPF tray app, views, view models, services
  ApexMapper.Persistence/     profiles, device registry, atomic file ops
  ApexMapper.Logging/         log store with rotation
  ApexMapper.Profiles/        default profile loader
tests/                        mirrors src/ structure; Windows-only suites skipped on CI
installer/                    WiX 5 MSI, update channel, signing scripts (phase 6)
docs/
  specs/                      V1 design spec
  superpowers/plans/          per-phase implementation plans
```

## Status

| Phase | Scope | Status |
|-------|-------|--------|
| 1 — Foundation | Core pipeline, persistence, logging, profiles | ✅ Complete |
| 2 — Input | HID/Raw Input adapters, InputHost, CI perf gate | ✅ Complete |
| 3 — Output & Supervisor | ViGEm adapter, IPC, watchdog, preflight, anti-cheat | 🔄 In progress |
| 4 — Tray UI | WPF tray, hotkeys, foreground watcher, calibration | 🔄 In progress |
| 5 — Diagnostics | Live state, latency histogram, probe runner, log tail | 🔄 In progress |
| 6 — Installer | Signed MSI, ViGEmBus detector, update channel | ⏳ Upcoming |
| 7 — Ship gates | Hardware testing, game verification, perf SLO, threat model | ⏳ Upcoming |

## Performance targets

- Steady-state binding pipeline: zero allocation per tick
- Latency p95 < 8 ms, p99 < 16 ms (Ryzen 5 / i5 desktop baseline)
- Diagnostics refresh: < 2 ms per 10 Hz tick

## License

MIT
