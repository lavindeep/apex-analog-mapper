# Requirements ledger

One line per requirement in `2026-09-19-rewrite.md`. The evidence column is filled by
the trace reviewer at each stage gate with `file:line` and a test name. Empty evidence
after the owning stage closes is a gap.

## Stack and shape

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| S1 | C# on .NET 10 LTS. | 0 | |
| S2 | WPF with WPF-UI, following the OS light or dark theme. | 0, 5 | |
| S3 | Velopack per-user installer, Add or Remove Programs entry, uninstall, delta updates from GitHub Releases, self-contained publish with the size in the README. | 0, 6 | |
| S4 | Three projects (Core, Windows, App), Core has no Windows APIs. | 0, 1 | |
| S5 | Hardware tests gated by `APEX_HW_TESTS=1`; soak by `APEX_SOAK=1`. | 2, 3 | |
| S6 | MIT license file present. | 0 | |

## Threads and hot path

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| T1 | Raw Input pump on its own thread, message-only window, `RIDEV_INPUTSINK | RIDEV_DEVNOTIFY`, attributes key events to a device, raises device arrival and removal. | 2 | |
| T2 | Hook thread with its own message loop, above-normal priority, runs the watchdog and the stop hotkey. | 2, 3 | |
| T3 | Sensor thread polls back to back for the needed groups and publishes timestamped snapshots. | 2 | |
| T4 | Engine thread on a high-resolution 1 ms timer, submits when the packed report changes, capped at 500 Hz, publishes a tick timestamp. | 3 | |
| T5 | Hook callback, sensor loop, and engine tick allocate nothing. | 1, 2, 3 | |
| T6 | `GCSettings.LatencyMode = SustainedLowLatency` from Start to Stop, restored on every exit path. | 3 | |
| T7 | Hook callback reads `KBDLLHOOKSTRUCT` by pointer, reads a cached foreground flag, never calls win32k or takes a lock. | 2 | |
| T8 | Foreground tracker on its own thread with `WINEVENT_OUTOFCONTEXT`. | 2 | |

## Key state store

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| K1 | One slot per scan code across plain, E0, and E1 pages. | 1 | |
| K2 | Each slot holds the digital state, the analog depth, and a gate bit, readable together. | 1 | |
| K3 | Gate set on session start, return from alt-tab, hook install, keyboard reconnect. | 1, 3 | |
| K4 | Gated key contributes zero and its ramp, selector, and rate state reset. | 1 | |
| K5 | Gate clears on a hook key-up or an analog reading inside the noise band. | 1 | |
| K6 | Gate is never set by a sensor fault. | 1 | |
| K7 | The hook owns the digital value and gate clearing; Raw Input only attributes. | 2 | |

## Analog input

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| A1 | Vendor interface selected by usage 0xFFC0:0x0001, 65-byte reports, same container id as the selected keyboard, exactly one match or nothing, non-exclusive open. | 2 | |
| A2 | Request layout `[0, command, selector, 0...]`; reply byte 0 must be 0. | 1 | |
| A3 | Command 0x90 parses the firmware string at offset 1. | 1 | |
| A4 | Command 0xD7 with selector 1..5 parses 14 raw and 14 filtered uint16 LE with zero padding at 57..64 and values at most 4095. | 1 | |
| A5 | Only 0x90 and 0xD7 can ever be written, enforced in one place with a test, and nothing outside the vendor interface performs HID writes. | 1, 2 | |
| A6 | 70-slot sensor table with 65 mapped keys, arrows and function row unsupported, per-keyboard overrides from the learn step. | 1 | |
| A7 | Input queue drained before every write. | 2 | |
| A8 | 0x90 canary every N cycles; mismatch retires the handle. | 2 | |
| A9 | Any fault retires the handle, waits on a signalable backoff, reopens, and re-verifies firmware. | 2 | |
| A10 | Raw versus filtered chosen from the stage 0 measurement and recorded. | 0, 1 | |
| A11 | Freshness limit `max(3 x p99 cycle period, 12 ms)` over the last 100 cycles; a cycle over 100 ms is a hard fault. | 1, 2 | |
| A12 | Status card shows measured p50 and p99 cycle period. | 5 | |
| A13 | Calibration per container id and firmware: rest, full, noise band, sensor index. | 1, 4 | |
| A14 | Depth re-normalised above the noise band per the design formula; in-band reads as released. | 1 | |
| A15 | Firmware change warns and keeps calibration data. | 4, 5 | |

## Fallback

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| F1 | Analog drives a key while the snapshot is fresh, the key is calibrated, and the at-rest latch is set. | 1 | |
| F2 | Stale or faulted sensor after a valid start falls back to the hook's digital state. | 1, 3 | |
| F3 | One ramp per key, set to the depth every analog tick, so fallback starts from the last analog value. | 1 | |
| F4 | Fallback rate is the binding's rate, or 50 ms full scale when the binding's rate is zero. | 1 | |
| F5 | Analog resumes only after a reading at rest; no reverse ramp. | 1 | |
| F6 | Fallback warning with the reason shown while active. | 5 | |
| F7 | Start refused until every analog key in the profile is calibrated; Start points at the calibration card. | 3, 5 | |

## Keyboard blocking

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| B1 | Swallow a mapped key only while the game is foreground. | 2 | |
| B2 | Never swallow injected events (outside test mode). | 2 | |
| B3 | Key-up swallowed only if its key-down was swallowed. | 2 | |
| B4 | Keys physically down at hook install are marked passed. | 2 | |
| B5 | Ctrl, Alt, or Win chords pass through and release held mapped keys. | 2 | |
| B6 | Ctrl, Alt, Win, F12 cannot be mapped. | 1, 5 | |
| B7 | On foreground loss, swallowed-down keys are marked passed; nothing is injected. | 2 | |
| B8 | Game matched by executable path, re-resolved on each foreground change. | 2 | |
| B9 | `ApplicationFrameHost` unwrapped to the `CoreWindow` owner. | 2 | |
| B10 | Elevated game detected and reported. | 2, 5 | |
| B11 | Mapped key from another keyboard produces a one-time notice; README states the limitation. | 2, 5, 6 | |

## Output and safety

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| O1 | ViGEm in-process on the engine thread with atomic submit. | 3 | |
| O2 | Connect sequence: prime with `LeftStickX = 1`, zero, loop until XInput reads all-zero, tolerate `UserIndexNotReported`, 2 s timeout. | 3 | |
| O3 | Packing: symmetric +/-32767 sticks, 0..255 triggers, non-finite to neutral. | 1 | |
| O4 | Zero before disconnect; disconnect idempotent. | 3 | |
| O5 | Process death unplugs the pad, proven by the kill test, or the contingency applies. | 0, 3 | |
| O6 | Watchdog on the hook timer: tick older than 200 ms claims pad ownership, zeros, unplugs, unhooks. | 3 | |
| O7 | Pad ownership token checked by the engine before every submit. | 3 | |
| O8 | Unhandled-exception handler zeros, unplugs, unhooks, restores GC mode. | 3 | |
| O9 | Driver exception on the engine thread stops the session with the reason shown. | 3, 5 | |
| O10 | Ctrl+Alt+F12 handled on the hook thread. | 2, 3 | |
| O11 | Driver check is present-and-running; missing shows version, browser button, re-check on focus; present-but-not-started shown distinctly; never downloads or runs the installer. | 3, 5 | |
| O12 | Controller disconnect detected on the hook timer's XInput check. | 3 | |

## Session

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| E1 | Start preconditions: keyboard, running game, driver running, all analog keys calibrated; connect pad, install hook, gate all. | 3 | |
| E2 | Foreground loss: pad zeroed, held keys handed back, gate set for return, session stays Running. | 3 | |
| E3 | Foreground regained: mapping resumes; held keys dead until released; status text says so. | 3, 5 | |
| E4 | Keyboard unplugged: Paused with hook kept and pad zeroed; auto-resume on reconnect with gate set. | 3 | |
| E5 | Stop on game exit, controller disconnect, resume from sleep, user Stop, hotkey, app close: zero, unplug, unhook, ungate. | 3 | |
| E6 | Game remembered by executable path; selected keyboard persisted. | 4 | |
| E7 | Updates never applied during a session. | 6 | |
| E8 | Editing the active profile stops the session. | 3, 5 | |
| E9 | Shutdown order and bounds as in the plan; Stop returns within bound with a blocked read or a backoff in progress. | 3 | |
| E10 | Rolling 1 MB log with one background writer; hot threads never write it; no key presses logged. | 4 | |

## Profile model

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| P1 | Profile is a name plus bindings; key binding to button or trigger; axis binding of two keys to a stick axis. | 1 | |
| P2 | Response is exponent, saturation, deadzone, with presets linear, soft, aggressive. | 1 | |
| P3 | Press and release ramps for digital keys and fallback. | 1 | |
| P4 | Conflict rule last-input-wins (default) or neutral. | 1 | |
| P5 | Axis mode position or rate; Forza default chosen in the feel step. | 1, 5 | |
| P6 | Forza profile ships as default and reset. | 1, 4 | |
| P7 | JSON with a version integer, atomic write, one `.bak`, `.corrupt` rename. | 1 | |
| P8 | Data folder `%AppData%\ApexAnalogMapper`; old folder never touched. | 4 | |

## Keyboards

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| H1 | 0x1610 and 0x1614 verified; the listed other product ids get try-it. | 1, 5 | |
| H2 | Try-it sends 0x90 only first; non-version reply stops with export offered. | 5 | |
| H3 | Consent naming the risk before any 0xD7 on an unverified board. | 5 | |
| H4 | Replies all zero, all identical, or unchanged on press are rejected. | 1 | |
| H5 | Learn step finds the sensor index per prompted key and rejects ambiguity. | 1, 5 | |
| H6 | Unverified banner; export JSON with firmware, product id, report lengths, container id, captures. | 1, 5 | |
| H7 | Firmware verified list warns rather than rejects. | 1, 5 | |

## Window

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| W1 | Status card: state, reason, Start and Stop, cycle p50 and p99, submit rate, warnings. | 5 | |
| W2 | Keyboard card: boards, firmware, verified or try-it. | 5 | |
| W3 | Game card: visible windows with executable, remembered game, elevated warning. | 5 | |
| W4 | Profile card: list, bindings editor with key capture and target, response picker with live preview. | 5 | |
| W5 | Calibration card: live raw bar per key, Set released, Set fully pressed, learn step, noise at rest. | 5 | |
| W6 | Setup card: driver state and browser button, Steam Input note, start-before-game note, update check with Update button. | 5, 6 | |
| W7 | Feel step recorded in `measurements.md`. | 5 | |

## Install, update, docs, CI

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| U1 | Update check on launch, cached, silent on failure, never blocks startup, prereleases off by default. | 6 | |
| U2 | Release workflow on tag: pack, `SHA256SUMS.txt`, upload, hashes in notes. | 6 | |
| U3 | README covers install and size, SmartScreen, driver link, Steam Input, start order, blocking limitation, alt-tab note, calibration, troubleshooting, build, never-inject stance. | 6 | |
| U4 | `docs/acceptance.md` in-game checklist. | 6 | |
| U5 | CI on `main`, `claude/**`, and pull requests: build, non-hardware tests, pack artifact. | 0 | |

## Measurements and tests

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| M1 | `measurements.md` records every stage 0 number and the five decisions. | 0 | |
| M2 | Fixtures from the real board under `docs/design/fixtures/`. | 0 | |
| M3 | `HardwareThresholds.cs` filled from measurements with headroom; stage 7 confirms agreement. | 3, 7 | |
| M4 | Loopback test asserts packet-number rate and p99 submit-to-readback. | 3 | |
| M5 | Kill test with a hook that swallows injected input in test mode and a real foreground window. | 3 | |
| M6 | Wedge test blocks inside a submit; watchdog claims ownership and unplugs; the wedged call never touches the driver again. | 3 | |
| M7 | Soak: working set, handles, keys at zero, GC pause budget, gen 2 count. | 3 | |
| M8 | Each stage's review findings recorded before fixes, with citations and test output. | all | |
