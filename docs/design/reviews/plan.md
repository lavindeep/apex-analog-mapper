# Plan review, 2026-09-19

Reviewer: Opus subagent, adversarial charge against the first version of
`2026-09-19-plan.md`. Outcome per finding. "Fixed" means the plan and, where needed, the
design were changed in the revision committed with this file.

| # | Severity | Finding | Outcome |
| --- | --- | --- | --- |
| 1 | Blocker | Core split cannot express fallback: one value per slot, no clock in `Mapper.Tick`, no bindings, two ramps with no precedence. | Fixed. Interfaces section added: two values per slot, `nowTicks` in `Tick`, `CompiledProfile` in the `Mapper` constructor, one ramp per key seeded from the analog depth, worked example. |
| 2 | Blocker | Gate missing from tick order; a ramp running under a gate snaps on clear. | Fixed. Gate is step 1 and resets ramp, selector, and rate state. Test added. |
| 3 | Blocker | Kill test cannot fail: injected input is never swallowed; message-only window cannot be foreground; blocking the hook thread evicts the hook by timeout. | Fixed. Test-only swallow-injected mode, real foreground window, message pump kept alive, `ERROR_DEVICE_NOT_CONNECTED` named. |
| 4 | Blocker | CI never runs on `claude/rewrite`; workflow lacks setup-dotnet, vpk install, publish, version. | Fixed. Trigger on `claude/**`, explicit steps, synthesised CI version. |
| 5 | Blocker | No thread lifecycle owner; Stop blocked by a 150 ms read or 1 s backoff; watchdog races the engine on the pad; watchdog fires in Paused. | Fixed. Shutdown order with bounds, `Abort()` on the interface, backoff on a wait handle, `PadOwner` token, watchdog armed only in Running and Paused (engine keeps ticking in Paused). |
| 6 | High | Stage 3 thresholds guessed before stage 0 measures them. | Fixed. `HardwareThresholds.cs` filled from `measurements.md`; stage 7 checks agreement. |
| 7 | High | "200 distinct trigger values" measures ramp slope on an 8-bit trigger. | Fixed. `dwPacketNumber` increments per second on a 16-bit stick ramp. |
| 8 | High | "No GC pause over 5 ms" not assertable. | Fixed. `GC.GetTotalPauseDuration` delta per second plus gen 2 count unchanged. |
| 9 | High | Logging inside the hook callback; max-of-one is a jitter detector. | Fixed. Timestamps into a preallocated ring, p99.9 plus excursion count. |
| 10 | High | Loopback threshold has no floor; sampler quantises. | Fixed. `readback` control run in stage 0; floor plus headroom; sampler period reported. |
| 11 | High | e2e primary anchor measures actuation offset, not latency. | Fixed. Sensor-crossing anchor is the headline; hook anchor labelled and paired with the GG actuation setting. |
| 12 | High | Resume from sleep has no task, and the design listed it as both a gate trigger and a Stop trigger. | Fixed. `PowerNotifier` in stage 3; design says Stop only. |
| 13 | High | "Held keys handed back" has no owner; reviewer proposed injecting key-downs. | Partly rejected. Injecting a down would type into the window the user switched to. Design and plan now say swallowed keys are marked passed so the key-up leaves naturally; test added. |
| 14 | High | Digital source for the store undefined. | Fixed. The hook owns digital value and gate clearing; Raw Input attributes only. |
| 15 | High | Allowlist enforced in two places. | Fixed. `SensorRequest` with a private constructor is the single allowlist; `VendorInterface.Exchange` accepts only it; architecture test. |
| 16 | High | `ForegroundTracker` has no thread; resolving on the hook thread breaks it. | Fixed. Own thread; hook reads a flag. |
| 17 | High | Protocol test fixture has no provenance. | Fixed. `probe` dumps replies into `docs/design/fixtures/`. |
| 18 | High | GC latency mode has no task. | Fixed. On `Session`, restored on every exit path including `CrashGuard`. |
| 19 | High | At-rest recovery latch has no owner. | Fixed. In `SourceSelector` with tests; the design drops the reverse ramp since recovery happens at zero. |
| 20 | Medium | GG stop step kills the GUI, not the engine service. | Fixed. `Stop-Service` from an elevated prompt, process list recorded, actuation settings checked afterwards, fallback of asking the maintainer. |
| 21 | Medium | Curve fit is predetermined and fitted to a curve the design calls broken. | Fixed. Fit dropped; exponent plus saturation plus deadzone chosen outright; feel comparison moved to stage 5. |
| 22 | Medium | Kill test decision has no failure branch. | Fixed. Contingency: minimal watchdog process waiting on the app's process handle. |
| 23 | Medium | `spike/` deletion contradicted between stages. | Fixed. Kept until stage 7. |
| 24 | Medium | No LICENSE exists. | Fixed. MIT added in 0a. |
| 25 | Medium | CI ViGEmBus attempt silently dropped. | Fixed. Decision recorded in 0b and the design. |
| 26 | Medium | Try-it: change-on-press check, export format, firmware verified list unassigned. | Fixed. `LearnStep`, `CaptureExport` (JSON, round-trip test), `KnownKeyboards`. |
| 27 | Medium | 100 ms hard fault unassigned; poller has no Waiting state; status card lacks p50 and p99. | Fixed. |
| 28 | Medium | Pump and discovery both claim device events. | Fixed. Discovery consumes the pump's events. |
| 29 | Medium | "Compile" is not a test for native interop. | Fixed. `[LibraryImport]` and struct size assertions. |
| 30 | Medium | Gate protocol not executable. | Fixed. `requirements.md` with evidence column, test output pasted, citations required, findings committed before fixes, all three reviewers on every stage, hardware outputs saved, stage 7 reviews seams. |
| 31 | Medium | Wedge test wedges in the wrong place with a guessed bound. | Fixed. Wedge inside a fake submit; bound derived; concurrency assertion. |
| 32 | Medium | Build config: TFM per project, NoWarn budget, WPF-UI on net10 unconfirmed, 1b after stage 0. | Fixed. Toolchain proof moved ahead of the spike. |
| 33 | Medium | Soak has no separate opt-in. | Fixed. `APEX_SOAK=1`. |
| 34 | Low | README size, old-folder assertion, calibration navigation, controller-disconnect detection, fallback reason text. | Fixed. Controller disconnect detected on the hook timer's XInput check. |
| 35 | Low | `FileLog` not in design; rate mode built before a feel step exists; `LearnStep` in the wrong project. | Fixed. Log added to the design with a single writer; feel step added to stage 5; `LearnStep` moved to Core. |
| 36 | Low | `vpk upload github` plus hashes is two steps. | Fixed. `SHA256SUMS.txt` then `gh release edit`. |
