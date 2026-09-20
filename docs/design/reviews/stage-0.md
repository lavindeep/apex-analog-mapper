# Stage 0 review, 2026-09-20

Reviewer: Opus subagent, charge: measurement method and conclusions. Outcome per
finding. Fixes are in the commit that adds this file, in `measurements.md`, the design,
the plan, `requirements.md`, and the spike's `Extra.cs`.

| # | Severity | Finding | Outcome |
| --- | --- | --- | --- |
| B1 | Blocker | The 12 ms cycle was measured without the drain and the canary, both of which the design shipped; cycle length is profile-dependent. | Fixed. The drain is withdrawn (zero stale replies in 90 s; the only drain available costs 10 ms). The cycle threshold is now `6 x groups + 9 ms`, canary included. Per-reply signature check added as the primary desync defence. |
| B2 | Blocker | `max(3 x p99, 12 ms)` over a rolling ring tracks the jitter it should detect; the multiplier is arbitrary; the floor never binds; a false fallback is sticky. | Fixed. Freshness is a fixed 60 ms with the sticky-fallback reasoning recorded. `CycleStats` keeps p50 and p99 for display and counts consecutive slow cycles for the fault. |
| H1 | High | The e2e headline measured submit-to-readback, not the sensor path, and the "poll cycle phase" explanation was wrong for a single-group loop. | Fixed. Relabelled; only medians quoted; the derivation for the two-group cycle stated; `e2e2` polls both groups and runs in session 2. |
| H2 | High | Anchors were last-seen values, not paired with their press; p99s meaningless. | Fixed. `e2e2` uses consume-once anchors and counts unpaired readbacks; the table now shows medians only. |
| H3 | High | Three threshold rows had a wrong or missing basis (loopback p50 versus p99, watchdog derived from the kill path, hook callback unmeasured). | Fixed. Loopback derived from timer p99 plus readback; watchdog from staleness plus 62.5 ms timer granularity plus the measured 1 ms graceful disconnect; hook callback measured (`hookcost`): p99.9 0.32 ms. |
| H4 | High | Cycle max of 23.5 ms not reproducible from the CSV; four decisions had no surviving logs. | Fixed in part. The cycle was rerun with logging: p99 12.05, max 22.2 ms (one cycle in 2500). Logs for every command now go to `spike/out/<command>.log`. The CSV only holds exchange rows, which is why the reviewer could not reproduce the cycle max. |
| H5 | High | "The reference's feel problem was the 65 Hz engine and 10 Hz output" is overclaimed; W's clip and near-rest compression are competing causes. | Fixed. Sentence rewritten as "sufficient to explain, plus two independent causes"; both causes are now design requirements (A16, A17, A18). `tickres` added; it contradicted the earlier sleep result, which is recorded. |
| H6 | High | Container id was never probed. | Fixed. `containers` reads it through cfgmgr32: one id across all ten interfaces. Replug stability is in session 2. |
| H7 | High | Nothing measured under game load or with GG's actuation page open. | Deferred to session 2 (needs the maintainer to open the page and run a game). Recorded under "Still open". |
| H8 | High | The 0xFFC1 interface on mi_04 was dismissed unexamined. | Fixed in part. `listen` read it for four seconds at rest: zero reports. Tap test in session 2. |
| M1 | Medium | Rest drift measured for two minutes, not ten. | Deferred to session 2. The default noise band was raised from 15 to 20 to leave room for unmeasured drift. |
| M2 | Medium | The 100 ms hard fault sat below the 150 ms read timeout and turned a single stall into an outage. | Fixed. Read timeout is the fault; three consecutive slow cycles are a fault; one slow cycle only stales. |
| M3 | Medium | Kill test was one trial; "5 ms" is a first-poll bound; the observer-hook substitution was unrecorded. | Fixed. Six trials, all gone at the first poll; substitution and its justification recorded. |
| M4 | Medium | `timeBeginPeriod` return value unchecked. | Fixed. `tickres` checks it and reads `NtQueryTimerResolution`. |
| M5 | Medium | Readback sampler was harmonically locked to the submit timer. | Fixed. `readback2` spins: 0.017 ms p50. The 1 ms figure was the instrument. |
| M6 | Medium | Step rise times quantised to 6 ms and reported to one decimal. | Fixed. Reported in samples with the aliasing explained. |
| M7 | Medium | Sensor map verified for five slots; 61 present versus 65 mapped unreconciled. | Fixed. Reconciled: six mapped ISO/JIS keys absent, two present slots unmapped (US backslash, Fn). Learn step is primary; the table is a corrected default. |
| M8 | Medium | Digital fire point measured for W only, the least representative key. | Deferred to session 2 (`travel` will track all four). |
| M9 | Medium | W's clip recorded but not carried into requirements. | Fixed. A16 and A17. |
| M10 | Medium | Per-cycle group identification by absent-slot signature is free and unused. | Fixed. Adopted as the primary desync defence (A7, `GroupSignature`). |
| M11 | Medium | "Firmware does not overlap requests" was asserted, not shown. | Fixed. `pipeline2` timestamps each reply: the first reply waits 12 ms and the second follows in 2 µs, so the firmware queues but holds replies; pipelining is a net loss. |
| L1 to L6 | Low | Wrong count range, rounded percentage, 15 versus 20 band, drain cost mechanism, `Floor` comment, run durations, stale design text. | Fixed. |

Session 2 items (need the maintainer) are listed at the end of `measurements.md`.
Stage 0 closes on the design decisions above; the session 2 runs can only tighten
constants, and each has a stated fallback if it disagrees.
