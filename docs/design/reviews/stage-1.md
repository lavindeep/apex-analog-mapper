# Stage 1 review, 2026-09-20

Four reviewers on commit `b082959` (Core, 134 tests): trace (requirements to code),
failure (attack the code), test (attack the suite), feel (drive the engine math with
the measured calibration). Every finding below was re-verified by the maintainer's
agent against the code and, where it depends on runtime behaviour, reproduced in a
scratch console app against the built Core assembly before any fix was written. The
verdict column is that independent judgement, not the reviewer's severity. Fixes land
in the commits after the one that adds this file.

Verdicts: confirmed (real, fixed at the stated severity), downgraded (real but less
severe than claimed, with the reason), rejected (claim does not hold), deferred (real,
owned by a later stage, recorded there).

## Blockers and highs

| # | Source | Finding | Verdict | Outcome |
| --- | --- | --- | --- | --- |
| B1 | failure B1, own | A dead or not-yet-published sensor at session start, reconnect, or alt-tab return leaves every analog key gated forever: only `SetAnalog(slot, 0)` clears an analog key's gate, and that needs a fresh snapshot. Reproduced: gate, NaN analog, key-up, still gated. Fallback never engages and `FallbackCount` is 0, so no warning either. Contradicts the design's "the gate is never set by a sensor fault, fallback engages instead" intent. | Confirmed blocker. | Fixed. While the slot's analog value is the unavailable sentinel, any hook event other than an auto-repeat clears the gate (a key-up, or a key-down from a known-up key). With the sensor dead the key is in digital fallback, so the hook is the release evidence; the rapid-trigger objection only applies while the sensor is alive. Test pins start with a dead sensor, press, ramp, release. |
| B2 | feel B1 | Fallback at partial depth ramps to full and stays there until the key is fully released, because recovery needs an at-rest reading (F5). One 60 ms sensor hiccup on a straight holds full throttle for the rest of the straight; mid-corner it is full lock. Traced: A at 0.8 reaches full lock 10.5 ms after the snapshot stales and stays there with a healthy sensor reporting 0.8 for as long as A is held. The design's own freshness argument ("holding a depth for 60 ms is the lesser evil") assumed the fallback holds the depth; it ramps to 1.0. | Confirmed blocker. Design amendment (F1, F5): the at-rest latch is dropped. Analog drives a key whenever its reading is available; the handover from digital to analog ramps the output from the fallback value to the live depth at the fallback rate (or the binding's rate), then tracks. Cold start is still covered by the gate, which clears at rest. With the key at rest the ramp finishes whatever descent was in progress at the same rate. | Fixed. `SourceSelector` removed; handover state lives in `Mapper`. Tests: fault mid-press then recovery while held sweeps 1.0 to 0.7 over 15 ms with no step; recovery at rest finishes the descent in progress. The maintainer can revert to the at-rest rule if they disagree; the ledger records why it was changed. |
| B3 | trace B1, failure B4, own | Profile load path throws instead of reporting: a file missing `keys` or `axes` gives null lists and `Profile.Validate` NREs; a non-object root, `null`, or a string `version` throws `InvalidOperationException` from `JsonDocuments`; both escape `JsonFile.Load` (which catches only `IOException`), so the `.bak` recovery never runs. Reproduced all four. | Confirmed blocker. | Fixed. `JsonDocuments` checks value kinds; `Profile.Validate` rejects null lists and null elements; `JsonFile` treats any exception from the parse callback as corrupt input (the callback parses untrusted text by construction) and keeps I/O failures separate. Tests for each input. |
| B4 | trace H2, failure B3 | A binding's `Response` is never validated on load and can be null: exponent 0 gives full output at one thousandth of travel; a missing `response` member NREs inside `Tick`. Reproduced both. | Confirmed blocker (the NRE is on the engine thread). | Fixed. `KeyBinding.Validate` and `AxisBinding.Validate` reject a null response and call `Response.Validate`. |
| B5 | failure B2 | `KeyCalibration` is constructible unvalidated (record constructor, which the JSON loader will use in stage 4); `TryCompile` never validates calibrations; a sensor index of 99 compiles and the first `Tick` throws `IndexOutOfRangeException`. | Confirmed blocker for stage 4's file path; nothing in stage 1 produces it. | Fixed now. `TryCompile` validates every calibration and treats an invalid one as missing, naming the key. |
| H1 | trace H3, test H4, failure H2, feel H4 | Gating a key resets its selector and ramp but not the axis's rate or conflict state, so a rate-mode axis glides to centre over `ReturnMs` after `GateUnknown` instead of zeroing. Reproduced: 32767, then 32701 one ms after the gate, 29425 at 51 ms. | Confirmed high. `ResetState()` (called by the session on the same events) masks it in practice, but the plan puts the reset in the tick and the gate must stand on its own. | Fixed. The axis loop resets rate and conflict state when either key becomes gated (on the transition, so a still-live partner key can keep steering from zero). Test pins zero on the first tick after gating a fully deflected rate axis. |
| H2 | failure H1 | `Ramp.Update` snaps to the target when dt is zero, negative, or NaN, folding "no time passed" into "duration zero, snap". Reproduced: seed 0.7, `Update(1, 0, 50)` gives 1.0. A first tick with no previous timestamp, or a clock that did not advance, slams a fallback ramp to full. | Confirmed high. | Fixed. Non-positive or non-finite dt holds the value. `Mapper.Tick` also clamps dt to 0..50 ms so a scheduling stall integrates at most one fallback ramp's worth (feel H3). |
| H3 | failure H4, own | `JsonFile.Load` returns `NotFound` when the primary is missing even with a good `.bak` beside it. `Load` itself creates that window (rename to `.corrupt`, then `Save`); a crash in between loses the user's file. Reproduced. | Confirmed high. | Fixed. A missing primary with a readable backup restores it and reports `Recovered`. |
| H4 | failure H5, test M2 | A locked primary (antivirus, sync client) is indistinguishable from a corrupt one: `TryParse` returns the I/O message as a parse error, `Load` renames the file to `.corrupt` (if the lock allows) and then throws out of the recovery `Save`. Reproduced: `Load` threw `IOException`. | Confirmed high. | Fixed. New `LoadStatus.Unavailable` for I/O failures: nothing is renamed or rewritten and the error says why. Test holds the file with `FileShare.None`. |
| H5 | failure H6 | `default(SensorRequest)` writes command 0x00; the allowlist test asserts `GetConstructors()` is empty, which is true of every struct. Reproduced. | Confirmed high (the allowlist is a design promise). | Fixed. `WriteTo` refuses anything but 0x90/0 and 0xD7/1..5; the test asserts `default` throws and that only two command bytes exist across every constructible request. |
| H6 | failure M1, own | `SensorSnapshot` has no publish protocol. `Begin` stamps the timestamp before any group lands, so a reader between `Begin` and the last `SetGroup` sees a fresh, empty snapshot and drops every analog key into fallback for a tick. No fence orders the array writes before the timestamp. | Confirmed high for stage 2, which cannot be built safely on the current type. | Fixed now. The poller fills a private working snapshot and calls `Publish(working)` on the shared one, which copies under a sequence lock (odd generation while writing). `Mapper` reads under the lock and retries a torn read; three tears count as unavailable. Freshness is now computed inside the snapshot from `FreshnessMs` and the tick rate given at construction (failure M2). |
| H7 | trace H4, feel M4, test note | Requirement A16 (clipping detection) has no Core implementation and the plan's stage 1 file table does not assign it. | Confirmed, downgraded to medium: nothing mishandles 4095 (depth 1.0, steady output, calibration validates); only the flag is missing. The "plateau across the last samples" half cannot be told from bottoming out by value alone. | Fixed. `KeyCalibration.IsClipping` (full press at the ceiling). A16 reworded to drop the plateau clause; the in-session 4095 warning stays with stage 5. |
| H8 | test H1, H2 | No test reaches a fresh snapshot with a missing group (`WasRead` false), so the NaN guard on the analog path can be deleted with the suite green. | Confirmed as a test gap; the code is right. | Fixed. Group-3-missing test asserts W still analog, A and D in fallback. |
| H9 | test H3 | The allocation test walks only the analog happy path. | Confirmed. | Fixed. Second loop alternates fresh, stale, null and gated across a rate-mode profile, plus a profile binding every `PadTarget`. |
| H10 | test H5 | `Begin` clearing the group flags is untested. | Confirmed. | Fixed. |
| H11 | test H6 | The profile round trip uses only default enum values, so a dropped `mode` or `conflict` member would pass. | Confirmed. | Fixed. Round trip with rate mode, neutral, custom ramps and a custom response, asserting full equality and the `"mode": "rate"` text. |
| H12 | feel H1 | The Soft preset as the shipped default compresses the near-rest region the design already flags: the trigger reads 0 at the firmware's own actuation point and needs half the key's travel for a quarter throttle. The reviewer's travel model rests on one stage 0 datum (0.2 mm at 2.5 percent of counts), so its exponent is indicative, not measured. | Downgraded to a stage 5 input. The default is the feel step's decision with the maintainer's hands (Q12); the qualitative point stands and the recommended per-binding table is recorded there. | Deferred to stage 5 (`notes-release.md`, feel step inputs). |
| H13 | feel H2 | Last-input-wins snaps the stick to centre on the tick the opposite key leaves the noise band, and back to the held value when it is released. Inherent to the rule; the same happens with digital SOCD. The reviewer proposes a `Sum` rule or a per-axis handover ramp. | Downgraded to a stage 5 input. The rule the maintainer chose behaves as specified; whether analog steering wants a third rule is a feel decision. | Deferred to stage 5 with the numbers. |
| H14 | feel H3 | No dt clamp: a 500 ms stall completes a fallback ramp or a third of a rate sweep in one tick. | Downgraded to medium: integrating the real elapsed time is not wrong, but an unbounded step after a stall is a surprise. | Fixed with H2 (dt clamped to 50 ms). |
| H15 | failure H3 | `Math.Clamp` throws on NaN inside `RateState.Step`. | Rejected. On .NET 10 `Math.Clamp(float.NaN, -1, 1)` returns NaN; `Step(1, 1, NaN, 100)` returns NaN without throwing. A NaN deflection would poison the axis (packs to zero, never recovers), which is worth a guard but is not the crash claimed. | Guard added: non-finite input or rate resets the deflection. |

## Mediums

| # | Source | Finding | Verdict | Outcome |
| --- | --- | --- | --- | --- |
| M1 | trace M5, failure M4 | `AnalogDriven` bits are only ever set; rebuilding a `Mapper` over a live store with a profile that drives a former analog key digitally leaves the bit set and the key's gate can never clear. Reproduced. | Confirmed. | Fixed. The constructor clears every slot's flag before setting its own. A `Mapper` is single-session by contract; the session builds a new one per start. |
| M2 | failure M3 | Two bindings on one pad target validate, and the later one's `SetButton(false)` clears the earlier one's bit. Reproduced. | Confirmed. | Fixed. `Profile.Validate` rejects a target bound twice. Two keys for one button is a possible later feature; not now. |
| M3 | failure M5 | `Normalizer.Depth` wraps on `int.MinValue` and divides by zero on a calibration whose span equals its band. | Confirmed for unvalidated inputs; unreachable from the sensor path (readings are `ushort`, calibrations are validated at compile time after B5). | Fixed. Raw is clamped to the 12-bit range; a span at or below the band gives zero. |
| M4 | feel M3 | The 0.5 button threshold is applied to the response-shaped ramp value, so a curve the user thinks only affects analog keys moves button latency between 7 and 101 ms on a 120 ms ramp. | Confirmed. | Fixed. Buttons are digital: pressed when the source is down, no ramp, no response. The gate-mid-ramp test moved to a trigger on a digital key. |
| M5 | trace M6, test L14 | The plan's Interfaces block and file table no longer match the code (`Mapper` and `Tick` signatures, `SourceSelector`, `GateAll` versus `GateUnknown`, `AxisMode.cs`). | Confirmed. | Fixed. Plan updated to the shipped shapes, including the B2 handover change. |
| M6 | test M1 | The concurrency test only checks the final cell, so a plain write in place of the CAS passes most runs. | Confirmed. | Fixed. Start barrier, and a reader thread asserting the analog value never goes backwards and the analog-driven flag never drops. |
| M7 | test M2, failure M8 | `JsonFile`: the both-corrupt branch never runs; the crash test cannot fail; no BOM assertion; a read-only `.bak` or a `Replace` failure throws out of `Save`. | Confirmed. | Fixed. Tests for both-corrupt, a directory squatting on `.tmp`, and the BOM; `Save` falls back to copy-then-move when `Replace` throws. |
| M8 | test M3, failure M6 | Only two of five absent-slot patterns are asserted; three fixtures unused. Separately, group 2's empty mask makes its signature "all values in range", so two fully populated groups on an unverified board would not cross-reject. | Confirmed as a test gap; the second half is a stage 2 poller rule. | Tests: the full 5x5 matrix on the real fixtures. Stage 2: the poller checks at start that the needed groups' rest replies cross-reject and shortens the canary interval if not. |
| M9 | test M4, M5, M6 | Validation branches, ascending beyond-full and wrong-side normalisation, and `LearnStep` boundary rows have no tests. | Confirmed. | Fixed. |
| M10 | test M7, M8 | The 15 ms figure is never asserted as a time; preset outputs are computed by the code under test; `CountFor` is unanchored. | Confirmed. | Fixed. 255 at 15 ms and still 255 at 20; `Soft.Map(0.5) = 0.3299`, `Aggressive.Map(0.5) = 0.6627`; one anchoring assertion. |
| M11 | test M9, trace L10 | `ResetState()` is called from nowhere. | Confirmed. | Test added (session-side contract). |
| M12 | failure M7 | `LearnStep.Observe` indexes past a short span. | Confirmed. | Fixed: length check. `WasRead` keeps throwing on a bad index, which after B5 is a programming error rather than a data error. |
| M13 | feel M2 | Rate mode has no deadzone, so a finger resting a few counts past the band walks the stick to full lock in 12 to 80 s. | Confirmed as a property of rate mode; Position is the default and a light press in rate mode is a slow turn by definition. | Noted for stage 5: a rate-mode default needs a deadzone of about 0.01. |
| M14 | feel M1 | Recovery at rest mid-descent steps the output to zero in one tick. | Confirmed, harmless (the key is released). | Disappears with B2's handover ramp. |

## Lows

| # | Source | Finding | Verdict | Outcome |
| --- | --- | --- | --- | --- |
| L1 | trace L7, feel L2 | `FallbackCount` counted healthy keys waiting to re-latch and skipped gated keys. | Confirmed. | With B2 there is no waiting state; the count is now every analog-driven key whose reading is unavailable this tick, gated or not. Stage 5 shows the warning from `SnapshotFresh` and lists the count. Both are written with `Volatile` for the UI thread (failure L2). |
| L2 | trace L8, L9; test L2, L3, L4, L5, L6, L7, L9, L10, L11, L13 | Missing direct assertions: K6, A17, allowlist bytes, partial percentile, reset percentile, non-analog gated key with zero depth, pad report throws, envelope branches, capture export errors, response combination, copy independence, button threshold. | Confirmed. | Fixed. |
| L3 | test L1, failure M2 | `FreshnessMs` was a constant compared to its own literal and never applied by Core. | Confirmed. | Fixed with H6: the snapshot derives its limit; the test checks behaviour at 59 and 60 ms. |
| L4 | test L8 | `ScanCode` as a dictionary key is never serialised. | Confirmed. | Round trip added now; stage 4's calibration store uses it. |
| L5 | test L12, feel L3 | `Conflict` treats any positive shaped value as active; simultaneous rising edges pick positive. | Confirmed as intended: a rest reading is exactly zero, so "active" means the key left the band; the tie is deterministic and pinned. | Comment added. |
| L6 | failure L1 | `GateUnknown` races a key pressed during its sweep, which stays ungated. | Confirmed as correct: a press in that millisecond is a genuine new press. | Comment added. |
| L7 | failure L3 | `CycleStats.Record(NaN)` masks a fault and poisons every percentile; percentile reads race the writer. | Confirmed. | Non-finite periods are ignored. The UI-side percentile copy can see a window in motion, which only blurs the status number; noted in the type. |
| L8 | failure L4, L5 | `Profile` record equality is reference-based over its lists; `AnalogAtRest` relies on `Normalizer` returning literal zero. | Confirmed. | Comments; stage 4 compares serialised text for the "profile edited" rule. |
| L9 | feel L1, L4 | Camera arrows snap to full deflection; fallback release follows the binding's ramp. | Confirmed, both are stage 5 default choices. | Recorded with H12. |
| L10 | failure note | The allocation test measured 64 to 88 bytes in a standalone console host but 0 under xunit. | Not reproducible under the test host; the code allocates nothing by inspection. | Left as an exact zero; if CI ever flakes on it the note explains where to look. |

## Requirement changes

- F1: "Analog drives a key while its reading is available (snapshot fresh, group read,
  key calibrated)." The at-rest latch is gone.
- F5: "Recovery from fallback ramps the output from the fallback value to the live
  depth at the fallback rate; with the key at rest the ramp finishes its descent."
  Replaces "resumes only after a reading at rest".
- K4: rate and conflict state reset when a key of the axis becomes gated.
- K5: adds "a hook key-up clears an analog-driven key's gate when the sensor reading is
  unavailable".
- A16: plateau clause dropped; the flag is `IsClipping`.

## Closing

After the fixes: 187 tests, all passing with the CI command (`dotnet test
ApexAnalogMapper.slnx -c Release`). Note for the next stage: passing `--nologo` to
`dotnet test` under the Microsoft.Testing.Platform runner forwards it to the test host,
which rejects it and reports "zero tests ran" with exit code 5; run without it.

## Sound

Every reviewer independently confirmed: the worked example reproduces to the tick,
the packed-long CAS store loses nothing under contention, `Tick` allocates nothing, the
protocol offsets match the stage 0 captures, the signature check rejects shifted and
firmware replies, `Map(1) = 1` for every allowed response, raw 4095 is handled, alt-tab
gating never releases a stale value, and the command allowlist is the only way to build
a request (now enforced at write time as well).
