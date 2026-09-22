# Stage 2 review, 2026-09-21

Five reviewers on commit `cb192b0` (Windows input landed in `71d9b41`; `e2fdf29` loosened
three CI timing bounds; `cb192b0` fixed two Cursor Bugbot findings): trace (requirements
to code), failure (attack the code), test (attack the suite), protocol (the sensor path
and its desync defence), hook (the low-level hook, Raw Input, foreground). Reviewers ran
as Opus subagents; every finding below was re-verified by the maintainer's agent against
the code, and where it depends on runtime behaviour, reproduced in a scratch console
program against the built assemblies before any fix was written. The verdict column is
that independent judgement. Fixes land in the commits after the one that adds this file.

Verdicts: confirmed (real, fixed at the stated severity), downgraded (real but less
severe than claimed, with the reason), rejected (claim does not hold), deferred (real,
owned by a later stage, recorded there).

Reproduced by the maintainer's agent for this ledger: `Thread.Join()` on the calling
thread never returns (B1); an exception thrown inside an `UnmanagedCallersOnly`
callback propagates through the native frame to the managed call site on .NET 10 x64
(B2: for the hook that is the message loop in `Run`, which has no handler, so the thread
and the process die); `System.Threading.Timer.Change` after `Dispose` returns false and
does not throw (the Bugbot verdict below).

## Cursor Bugbot findings, fixed in `cb192b0` before this ledger at the maintainer's request

| # | Finding | Verdict | Outcome |
| --- | --- | --- | --- |
| BB1 | `RawInputPump.ContainerIdOf` cached a handle's container id, including a failed null, and never invalidated it; Raw Input handle values are reused after a replug. | Confirmed medium. | Fixed: a device-change notification forgets the handle; failed lookups are not cached. |
| BB2 | `KeyboardDiscovery.Dispose` disposes the debounce timer while a `DeviceChanged` invocation may be in flight; the claimed `ObjectDisposedException` escapes the window procedure. | Mechanism rejected: `Timer.Change` after `Dispose` returns false without throwing (reproduced). The line was still fatal for another reason the failure reviewer found: the debounce callback ran `Refresh` on a thread-pool thread with no handler, and an exception there (HidSharp enumeration, or a `Changed` subscriber) ends the process. | The `cb192b0` fix stands: the discovery guards its lifetime, the background refresh catches and reports, and the pump counts handler exceptions. Residuals in L32 and L33. |
| BB3 | `PollerConfig.For` keeps the 50-cycle canary for a single polled group with no signature; a missing signature is when a shifted reply is published as depth. | Rejected. Signatures tell groups apart; with one polled group there is no other group to shift in from. The only shifts left are a firmware reply in the sensor slot (the 12-bit check retires the handle), a sensor reply in the canary slot (the canary retires it), and a stale reply of the same group, which its own signature would match anyway. The single-group rule is deliberate and `The_canary_runs_every_five_cycles_when_the_polled_groups_cannot_be_told_apart` asserts it. | No change. |
| BB4 | `FaultReason` keeps `WaitingReason` after a later successful open, so `State` is Running while the reason says no interface was found. | Rejected as documented behaviour: the property is "why the handle was last retired, or why the poller is waiting", kept so the status card can show the last fault after a recovery; `State` is the current condition. Stage 5's card reads `State` first and shows the reason only for Waiting and Faulted. | No change; recorded for the stage 5 card. |

## Blockers

| # | Source | Finding | Verdict | Outcome |
| --- | --- | --- | --- | --- |
| B1 | hook K-B1, failure F-B1 | `KeyboardHook.Stop`, `ForegroundTracker.Stop` and `RawInputPump.Stop` post quit and join their own thread. The design routes the stop hotkey to the hook thread and foreground and device events to their threads, so the natural stage 3 wiring (`StopRequested = session.Stop`) calls `Stop` from the thread it joins, which never returns: the hook stays installed and swallowing, the session never reaches Idle. Reproduced: self-join blocks forever. | Confirmed blocker (a trap set for stage 3 on the design's own event route). | Fixed. `Stop` posts quit and returns without joining when called on the owning thread; the loop tail uninstalls. Documented on the three events. Tests call `Stop` from the handler. |
| B2 | hook K-B2, failure F-H2, protocol P-H1 | No exception boundary on any of the four threads or the three native callbacks. A throwing `StopRequested`, `Timer` or `Changed` handler, or any exception type outside the hand-written catch lists in `VendorInterface.Exchange` and `SensorPoller.TryOpen`, ends the process, bypassing the crash guard that zeros the pad. Reproduced for the poller thread and for propagation out of a native callback. | Confirmed blocker. | Fixed. Each callback body and each thread body is wrapped: handler exceptions are counted (`HandlerFaults`, as the pump already does), the poller routes anything unexpected through `Fault`, and the loops never die on a handler. |
| B3 | protocol P-B1 | The snapshot is stamped at the start of a cycle, so its maximum age is two cycles plus a canary. Five groups need 66 ms against the 60 ms freshness limit: a five-group configuration is stale on every cycle and the engine falls back for a tick 33 times a second. The calibration card must poll all five groups for the learn step. Arithmetic verified. | Confirmed blocker for stage 5's calibration card; no shipped profile uses five groups. | Fixed now. The snapshot is stamped when the last group has landed, so its age at the engine is at most one cycle plus a canary (36 ms at five groups). The design's freshness paragraph says so. |
| B4 | trace T-H3, protocol P-H2, stage 1 ledger M8 | The stage 1 ledger committed stage 2 to checking at open that the polled groups' signatures cross-reject and shortening the canary interval when they do not. Neither exists. Two polled groups with identical absent masks (a JIS board collapses groups 1, 2 and 4 to 0x0000) make a shifted reply invisible for up to 49 cycles, during which the engine reads the other group's depth. The hardware poller test runs with the check switched off. | Confirmed blocker (a recorded commitment, and a live risk on ISO and JIS layouts). | Fixed. At open the poller checks the polled groups' recorded signatures pairwise; when any pair cannot be told apart, or a group has no signature, the canary runs every 5 cycles instead of 50. The hardware test builds signatures from the live board's rest replies. |
| B5 | test S-B1 | `ForegroundTracker` has no test of any kind. Hardcoding the flag false leaves the suite and the hardware run green, because the hardware test observes the record, not the flag, and asserts the flag false only after `Stop`. | Confirmed blocker for the gate (the flag is what makes the hook block anything). | Fixed. `Evaluate` is driven with the fake window tree: flag true on the game, `Changed` once per transition, elevated game leaves it false, `Stop` clears it, `GamePath` re-evaluates. Lifecycle test with `Assert.Skip` when the session cannot hook. |

## Highs

| # | Source | Finding | Verdict | Outcome |
| --- | --- | --- | --- | --- |
| H1 | trace T-H2, hook K-H1, failure F-L6 | An elevated game is marked unhookable from the game's token alone. The design's remedy is "run the mapper as administrator", after which UIPI no longer blocks the hook, but the flag stays false and nothing maps, with no error. | Confirmed high. | Fixed. `IWindowSystem` reports whether this process is elevated; a game is unhookable only when it is elevated and we are not. An unreadable token is reported as its own state. The elevated-game branch still needs one manual check with an elevated window (L34). |
| H2 | trace T-H1, protocol P-H3, test S-M4 | `IVendorStream` and `HidVendorDevices.Open` are public, so any code can write raw bytes to the keyboard around the allowlist; the architecture test asserts only that one file mentions HidSharp; HidSharp flows transitively to the App project. | Confirmed high (a design promise: "enforced in one place with a test"). | Fixed. `IVendorStream`, `Open` and the poller's stream constructor are internal; HidSharp's compile assets do not flow to dependents; the architecture test asserts that only `VendorInterface` calls `IVendorStream.Write`. |
| H3 | hook K-H2 | The hook writes `Digital = true` for a mapped key whose down was passed through (a Ctrl chord, a key held at install, later a key handed back on focus loss). The game gets the real key and the engine drives the pad from the same press: double input. Reproduced against the store. | Confirmed high. | Fixed. When a mapped key's down passes through, the hook gates the slot; the gate already means "contributes nothing until released once" and clears on the key-up. Install-time held keys are gated the same way instead of asserted down. |
| H4 | hook K-H3, failure F-M1, trace T-M3 | Modifier bits are built only from events the hook sees. Ctrl+Alt+Del, a UAC prompt, or a modifier held at install desync them for the rest of the session: nothing is swallowed again (double input) and a bare F12 becomes the stop hotkey. Reproduced. `GetAsyncKeyState` must not be called inside the callback (documented). | Confirmed high. | Fixed. The policy's modifier bits are seeded at install and resynchronised from `GetAsyncKeyState` on the hook thread's 50 ms timer, never in the callback. |
| H5 | hook K-H4 | Windows removes a low-level hook silently when a callback exceeds `LowLevelHooksTimeout`; nothing detects it, `IsInstalled` stays true, mapped keys reach the game while the pad is still driven. | Confirmed high; the reinstall belongs to stage 3's watchdog. | Partly fixed now: the hook and the pump expose an event count and a last-event timestamp so the 50 ms timer can compare them (raw events flowing while the hook's counter stands still means the hook is gone). Stage 3 reinstalls and gates on that signal; recorded in the plan. |
| H6 | hook K-H5 | `HookPolicy.ForegroundLost` has no caller (stage 3 wiring), and an auto-repeat of a swallowed-down key is still swallowed after the flag drops, so a held key is dead on the desktop until released. | Confirmed high; the wiring is stage 3. | Fixed now: a repeat while the game is not foreground passes and the slot becomes passed-down. Stage 3 wires `Changed` to `ForegroundLost` with a test. |
| H7 | failure F-H1, protocol P-M1 | `CycleStats.Percentile` re-reads `_count` after its zero guard; a `Reset` from the poller thread (every reopen) racing a status-card read throws `IndexOutOfRangeException` on the UI thread. Reproduced by both reviewers. | Confirmed high. | Fixed. One local read of the count. |
| H8 | test S-B2, trace T-L7 | No assertion anywhere depends on the hook callback's return value. The swallow hardware test counts the policy's decisions and the store's writes, both insensitive to whether the key was actually swallowed. | Confirmed high. | Fixed. The hardware test samples `GetAsyncKeyState` for W from the test thread: a swallowed event never updates the asynchronous key state, a passed one does. |
| H9 | test S-H4, S-H5 | `Abort_unblocks_a_pending_read_at_once` discards its spin-wait result and passes vacuously if the read never started; a regression in `Abort` hangs the CI job because `SensorPoller.Stop` joins without a bound and no test carries a timeout. Reproduced (two hung test processes). | Confirmed high. | Fixed. The spin-wait is asserted, the reader runs on a dedicated thread, and the three stop and abort tests carry an xunit timeout. |
| H10 | test S-H6, S-H2, S-L7, trace T-M2 | Nothing that runs in CI touches `KeyboardHook`: the 50 ms timer, the extended-key slot, the injected-event rule for the store, the stop chord invocation, `MarkKeysAlreadyDown` and `SetMapped` can all be broken with the suite green. | Confirmed high. | Fixed. The event decode and `Handle` are internal and driven with synthetic `KBDLLHOOKSTRUCT`s (including an allocation loop); a hook lifecycle test in the singleton collection counts timer ticks and skips when the session cannot hook. |
| H11 | test S-H1 | Matching the game by file name instead of full path leaves the suite green. | Confirmed. | Fixed. Same executable name in a different directory is not the game. |
| H12 | test S-H3 | Alt+F12 alone as the stop chord leaves the suite green. | Confirmed. | Fixed. Alt without Ctrl and Win+F12 are asserted not to stop. |
| H13 | protocol P-H4 | `HidVendorDevices.Open` matches vendor id, usage and report length but not product id, so 0x90 is written once a second forever to any SteelSeries device with that interface shape if its container id is selected. | Confirmed high (the 0xD7 guard behind the firmware check is real and keeps it to 0x90). | Fixed. `Open` requires a known Apex Pro product id. |

## Mediums

| # | Source | Finding | Verdict | Outcome |
| --- | --- | --- | --- | --- |
| M1 | hook K-M1, trace T-L2, failure F-L5 | The stop chord is checked before the injected filter (an injected F12 with real Ctrl+Alt held stops the session and is swallowed, against B2); `scanCode & 0xFF` aliases Unicode packets (`VK_PACKET`, U+0058) onto F12's make code; the chord fires on every auto-repeat and its key-up passes through unmatched. | Confirmed. | Fixed. The chord requires a non-injected event (or test mode), Unicode packets and make codes above 0xFF are ignored, the chord is edge-triggered and records F12 as swallowed-down so the key-up is swallowed too. |
| M2 | hook K-M2 | AltGr arrives as left Ctrl plus right Alt, so AltGr+F12 fires the stop chord on every non-US layout. | Confirmed. | Fixed. The chord requires left Alt, which AltGr never produces. README notes that AltGr passes mapped keys through like any Ctrl or Alt chord. |
| M3 | hook K-M3, trace T-L9, failure F-M5 | The hook cannot produce an E1-page slot and reports NumLock and Pause with the opposite extended flag to Raw Input, so a NumLock binding is never swallowed and Pause writes into NumLock's slot. | Confirmed for NumLock and Pause; the Korean and Japanese LANG keys also differ and are left unbindable. | Fixed. The hook normalises make code 0x45: extended is NumLock (plain 0x45), non-extended is Pause (E1 page). Stage 5's key capture refuses other E1 codes and the LANG keys. |
| M4 | hook K-M4 | Work in the 50 ms timer or the stop handler counts against `LowLevelHooksTimeout`, because hook callbacks are delivered by message to the same thread. Stage 3 plans driver calls there. | Confirmed as a stage 3 constraint. | Deferred to stage 3 with the rule recorded in the plan: the timer handler compares timestamps and flips an interlocked token; driver calls go to a worker thread. |
| M5 | hook K-M5, failure F-L2 | `MapVirtualKeyW(MAPVK_VSC_TO_VK_EX)` maps numpad and arrow pairs to the same virtual key and returns 0 for extended NumLock, so the install-time held-key scan can mark the wrong key. Reproduced. | Confirmed medium; rare (an arrow held while the profile maps the matching numpad key). | Fixed with H3: a key reported down at install is gated, not asserted down; an ambiguous pair marks both as passed-down, which costs one passed-through press of the other key. Recorded. |
| M6 | hook K-M6 | Nothing reconciles the foreground flag; one dropped `EVENT_SYSTEM_FOREGROUND` leaves it stuck for the session. | Confirmed. | Fixed. The tracker re-resolves the foreground window every 250 ms on its own thread. |
| M7 | failure F-M2, protocol P-M3 | `PollerConfig`'s record constructor is public and unvalidated; a short signatures array or a bad group kills the poller thread. Reproduced. | Confirmed. | Fixed. `For` is the only constructor. |
| M8 | failure F-M3 | `Stop` before `Start` leaves a poller that starts, exits at once and reports Stopped with no reason. Reproduced. | Confirmed. | Fixed. `Start` after `Stop` throws. |
| M9 | failure F-M4 | `HighResolutionTimer.Dispose` closes handles Windows reuses at once; a stale instance waited on and consumed the live timer's tick. Reproduced. | Confirmed. | Fixed. Handles are zeroed under an interlocked guard; `WaitNext` and `Stop` are no-ops after dispose; the owner must join the waiting thread before disposing, and the doc says so. |
| M10 | protocol P-M2 | A wrong recorded signature faults forever at 1.25 Hz with a reason that blames the protocol. | Confirmed. | Fixed. Repeated signature faults before any successful cycle change the reason to say the recorded signature does not match this keyboard and calibration should be re-run. |
| M11 | protocol P-M4 | One dropped reply costs about a second of fallback, most of it the 1 s backoff, which has no measurement behind it. | Confirmed. | Fixed. The first reopen after a fault is immediate; the backoff applies from the second consecutive fault. |
| M12 | protocol P-M5 | HidSharp's open contention timeouts default to 3 s and 30 s; an exclusive holder pins the poller in Starting and `Stop` behind it. | Confirmed. | Fixed. Both timeouts set to 300 ms. |
| M13 | protocol P-M6 | A firmware change across a reopen silently replaces the reported string and the canary anchor; `VerifiedFirmware` lists only 4.9.1 while the board runs 4.16.8. | Confirmed. | Fixed: the poller keeps the firmware from the first open and reports a change. 4.16.8 joins `VerifiedFirmware` after the maintainer's travel and latency run on it (session 3); until then it is reported as unverified, which is the truth. |
| M14 | test S-M1 | Four poller fault tests assert the reason and count only because the 1 s backoff outruns the test thread; a stalled runner sees the re-fault of the disposed fake. Reproduced with a short backoff. | Confirmed. | Fixed. Tests stop the poller as soon as it faults, then assert. |
| M15 | test S-M2 | The slow-cycle test races wall-clock time; a 200 ms stall of the test thread fails it. Reproduced. | Confirmed. | Fixed. The staleness boundary is asserted arithmetically from the published timestamp. |
| M16 | test S-M3 | Device notifications and the discovery subscription can be switched off with the suite green; the debounce is untested. | Confirmed. | Fixed. The registration flags are a tested constant; a watch-plus-debounce test asserts one `Changed` for two changes inside the window. |
| M17 | test S-M5 | `Select`'s doc claims "SteelSeries only" but the vendor filter lives in enumeration; nothing tests rejection. | Confirmed as a doc error. | Fixed. Doc corrected; the vendor filter stays in enumeration, where the product id also gates `Open` (H13). |
| M18 | test S-M6, protocol P-L4, trace T-H3 | No hardware test checks a real reply against a real signature or times `Stop` against a real HidSharp read. | Confirmed. | Fixed with B4: the hardware poller test builds signatures from live rest replies and times `Stop`. |
| M19 | test S-M7, trace T-M2 | The allocation test covers `Cycle` only, on the test thread, with a fake stream; the period bookkeeping in `Run` and the hook callback are unmeasured. | Confirmed. | Fixed. The period bookkeeping moved into `Cycle`; the hook decode plus policy plus store path has its own allocation loop (H10). The HidSharp read itself stays unmeasured; it is the one part not owned here. |
| M20 | test S-M8 | A mid-loop failure in the hook hardware test leaves an injected W physically down. | Confirmed. | Fixed. The key-up is sent in `finally`; the exact swallow count becomes a lower bound. |
| M21 | test S-M9, hook K-L4 | `HookPolicy` documents a cross-thread caller but `_modifiers` is a plain int and `SetMapped` rewrites the map while the callback reads it; no concurrency test. | Confirmed. | Fixed. `_modifiers` uses volatile access, `SetMapped` is documented and asserted as pre-install only, and a test hammers `Decide` against `ForegroundLost`. |
| M22 | test S-M10, trace T-M4 | The three bounds loosened to 200 ms no longer match the plan's numbers and pass without proving the mechanism. | Confirmed. | Fixed. The plan records 200 ms as the CI gate with the reason; the tests assert the mechanism (`IsDisposed` before the join returns, state Waiting when `Stop` is entered); the measured figures live in the hardware run. |
| M23 | trace T-M1 | The callback duration excludes `CallNextHookEx` and the hardware test only times the swallow path, which stage 0 measured at zero. | Confirmed. | Fixed. The whole callback is timed and the hardware test adds a pass-through pass. |
| M24 | trace T-M5 | `HidVendorDevices.Open`'s selection rule (usage, lengths, container, exactly one) has no seam and no CI test. | Confirmed. | Fixed. A pure `Select` over candidate descriptions with tests for the reject branches. |
| M25 | failure F-L7 | On focus gain the flag is written before `Changed` is raised, so a key pressed in that window is swallowed and then gated by the session's sweep. | Confirmed. | Fixed. On gain, `Changed` runs before the flag flips; on loss, the flag flips first. |

## Lows

| # | Source | Finding | Verdict | Outcome |
| --- | --- | --- | --- | --- |
| L1 | trace T-L1 | The key-up rule carries an extra "and game foreground" term the design does not state. | Confirmed as an unrecorded deviation; it fails safe. | Recorded here and in the rule's comment: it covers the gap between the flag flipping and `ForegroundLost`. |
| L2 | trace T-L3 | `SendInput` and the scan-code helper ship in the product assembly for the tests' sake. | Confirmed. | Fixed. Moved to the test project. |
| L3 | trace T-L4 | The plan says a ring of timestamps; the code keeps callback durations. | Confirmed. | Plan reworded. |
| L4 | trace T-L5 | The sensor thread runs above normal; the design gives that only to the hook thread. | Confirmed. | Fixed. Normal priority. |
| L5 | trace T-L6 | `WaitNext` returns false for `WAIT_FAILED`, indistinguishable from a stop. | Confirmed. | Fixed. A failed wait throws. |
| L6 | trace T-L8 | Nothing consumes the Raw Input ring yet; after 256 events every event is an overflow until stage 5 drains it. | Confirmed. | Recorded on the stage 5 row. |
| L7 | trace T-L10 | The install-time writes are correct only because Start installs the hook before gating. | Confirmed; the install-time write is now a gate (H3), so the order no longer matters. | Closed by H3. |
| L8 | trace T-L11 | `ForegroundTracker` has no lifecycle test in CI. | Confirmed. | Fixed with B5. |
| L9 | hook K-L1 | Ring indices use `%` on a signed counter that wraps negative after 2^31 events. | Confirmed. | Fixed. Power-of-two masks. |
| L10 | hook K-L2 | The hook does not drop make code 0xFF or the fake shifts the decoder drops. | Confirmed. | Fixed. Same filter in both. |
| L11 | hook K-L3 | `PostThreadMessageW`'s result is ignored and the join has no timeout. | Confirmed. | Fixed. The post is retried briefly and the join is bounded. |
| L12 | hook K-L5 | The excursion assertion allows zero excursions at 400 samples, and the first callback pays the JIT. | Confirmed. | Fixed. One excursion allowed below a thousand samples. |
| L13 | hook K-L6 | "Never calls win32k" overstates it: the pass-through path calls `CallNextHookEx`. | Confirmed. | Reworded: no window state, no blocking call. |
| L14 | hook K-L7 | `KeyStateStore.ClearAll` keeps the digital bit, so a key released between sessions is one dead press at the next start. | Confirmed. | Fixed in Core. |
| L15 | hook K-L8 | Injected events never reach the store outside test mode, so a physical down followed by an injected up sticks. | Confirmed. | Fixed. Every event updates the store; B2 governs swallowing, not observation. |
| L16 | hook K-L9 | `OnInput` never checks the header size and drops quietly. | Confirmed. | Fixed. Mismatches are counted. |
| L17 | hook K-L10 | `DeviceChanged` only reports changes after registration; consumers must enumerate first. | Confirmed. | Documented. |
| L18 | protocol P-L1, failure F-L1 | The first period after every open is 0 ms, and each period is recorded one cycle late. | Confirmed. | Fixed. The finished cycle is timed and the first sample of a handle is skipped. |
| L19 | protocol P-L2 | `IsPlausibleGroup` has no production caller. | Confirmed. | Fixed. Called per reply. |
| L20 | protocol P-L3 | Entering Waiting leaves the previous fault reason in place, or none. | Confirmed. | Fixed. Waiting carries its own reason. |
| L21 | protocol P-L5 | `Select` labels a board from whichever interface enumerated first. | Confirmed. | Fixed. A known product id wins. |
| L22 | test S-L1 | The hardware skip is decided in the attribute constructor rather than xunit's `SkipUnless`. | Confirmed. | Fixed. |
| L23 | test S-L2 | Three disposables created without `using`. | Confirmed. | Fixed. |
| L24 | test S-L3 | Pure ring and cache tests sit in the serialised collection. | Confirmed. | Fixed. Split out. |
| L25 | test S-L4 | The canary's trailing-zero check is never exercised. | Confirmed. | Fixed. A canary with the right version and dirty padding faults. |
| L26 | test S-L5 | The firmware-in-a-slot test asserts the reason only. | Confirmed. | Fixed. Retirement and count asserted. |
| L27 | test S-L6 | Three of six modifier slots in the theory. | Confirmed. | Fixed. |
| L28 | test S-L8 | The singleton release is unconditional, so a failed guard test poisons the collection. | Confirmed. | Fixed. Compare-exchange against `this`; the guard test cleans up in `finally`. |
| L29 | failure F-L3 | Discovery residuals: a `Changed` subscriber exception is reported as an enumeration failure; `Watch` twice double-subscribes. | Confirmed. | Fixed. |
| L30 | failure F-L4 | Cache residuals: the lookup holds the lock across cfgmgr32 calls the pump thread also takes; entries for devices unplugged while stopped are never pruned. | Confirmed. | Fixed. The lookup runs outside the lock and the cache is cleared at `Stop`. |
| L31 | failure F-L6 | B10's elevated-game branch has no automated coverage and cannot be exercised from a non-elevated process. | Confirmed. | Open: one manual check with an elevated window by the maintainer before the stage closes (H1 changes what the branch must show). |
| L32 | protocol P-M6 | 4.16.8 is not in `VerifiedFirmware`. | Confirmed. | Open: added after the maintainer's session 3 on that firmware. |

## Requirement changes

- A7: the start-up cross-reject is now "the polled groups' recorded signatures are
  checked pairwise at open; when any pair is indistinguishable, or a group has no
  signature, the canary runs every 5 cycles" (B4).
- B10: "elevated relative to this process" (H1).
- T7: "reads no window state and makes no blocking call" (L13).
- New stage 3 inputs from this ledger: the self-stop rule (B1), the handler rules for
  the hook timer (M4), the unhook detection signal (H5), `ForegroundLost` wiring (H6),
  Start's gate-after-install order (L7, now moot).

## Closing

Filled in by the closing commit: test count, hardware run output, CI run.
