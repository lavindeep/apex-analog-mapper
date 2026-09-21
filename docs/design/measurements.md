# Stage 0 measurements

Machine: Windows 11 Home 26200, Apex Pro TKL gen 1 (USB 1038:1614, firmware 4.9.1),
ViGEmBus 1.21.442, .NET 10.0.401. Spike source in `spike/Spike`; console output of
every run is in `spike/out/<command>.log` (not committed) and quoted here. Revised
after the stage 0 review in `reviews/stage-0.md`, then again after keyboard session 2
on 2026-09-21 (the runs listed under "Session 2" at the end).

Processes running during every keyboard measurement: SteelSeriesEngine, SteelSeriesGG,
SteelSeriesGGEZ, SteelSeriesMoments, SteelSeriesPrism, all idle in the tray. No game
was running. See "Still open" for the runs that need a game or GG's actuation page.

## Keyboard settings

GG actuation at about 0.2 mm on every OmniPoint key, rapid trigger on. Firmware 4.9.1
is the August 2023 OmniPoint 2.0 update. Neither setting touches the sensor path; the
design records their effect on the digital events (gate clearing for analog keys uses
the sensor only; fallback is coarse under rapid trigger).

## Vendor interface (probe, containers, listen)

The 0xFFC0:0x0001 interface is `mi_01`, 65-byte input and output reports. All ten HID
interfaces of the board share one container id
(`27373de1-4206-11f1-b9e4-14ac60fcc13e` on this PC), read through cfgmgr32 from the
interface path, so container id is a sound keyboard identity. Session 2 unplugged and
replugged the board: all ten interfaces came back with the same id.

The 0xFFC1:0x0001 interface on `mi_04` (65-byte input only) produced zero reports in
four seconds at rest and zero in five seconds of tapping keys (session 2). It does not
stream on key movement; the request and reply model stands.

Firmware reply to 0x90 is `4.9.1`.

Rest readings, all five groups, raw and filtered within a few counts of each other:

- Slots with a physical key read 830 to 927 counts.
- Nine slots read 4 to 9 counts: no sensor there on this US layout.

So "under 50 counts at rest" identifies an absent slot with a 90x margin. Of the 70
slots, 61 have a sensor on this board. The shipped table maps 65 scan codes, six of
which are ISO or JIS keys absent here (0x7D, 0x56, 0x73, 0x7B, 0x79, 0x70), and two
slots with a sensor present are unmapped: group 2 slot 13 (the US backslash, which the
reference left out as layout-ambiguous) and group 5 slot 9 (Fn). Only W, A, S, D, and
Enter (seen moving in the held-W fixture, group 3 slot 13) are physically verified.
The learn step is therefore the primary way to assign a sensor, and the table is a
default it corrects.

Fixtures for the Core protocol tests are in `fixtures/`.

## Timer resolution (tick, tickres)

`tick`, 8 s per method:

| Method | p50 | p99 | max |
| --- | --- | --- | --- |
| `Thread.Sleep(1)` | 15.28 ms | 16.54 ms | 16.58 ms |
| `WaitHandle.WaitOne(1)` | 15.35 ms | 16.63 ms | 16.87 ms |
| `Thread.Sleep(1)` after `timeBeginPeriod(1)` | 15.34 ms | 16.58 ms | 17.10 ms |
| High-resolution waitable timer | 1.51 ms | 1.94 ms | 2.83 ms |

`tickres`, run later: `NtQueryTimerResolution` reported the system already at 1.000 ms,
`timeBeginPeriod(1)` returned 0, and `Thread.Sleep(1)` then measured p50 1.52 ms. So
sleep-based waits were 15 ms in one run and 1.5 ms in another, in the same kind of
process. Windows 11 applies per-process resolution rules that depend on window and
power state, and the spike cannot control which applied. That is the reason for
decision 3, not the 15 ms figure itself: the waitable timer was consistent both times
and does not depend on a setting other processes control.

The reference engine's actual tick rate was never measured and cannot be inferred
from this; its code requested no resolution and used `WaitOne(1)`, so it ran at
whatever the system gave it, somewhere between 1 and 16 ms.

## Sensor exchange and cycle (cycle, pipeline, pipeline2)

| Run | Exchange p50 | Exchange p99 | Cycle p50 | Cycle p99 | Cycle max | Faults |
| --- | --- | --- | --- | --- | --- | --- |
| Back to back, groups 2 and 3, 30 s (first run) | 6.00 ms | 6.07 ms | 12.00 ms | 12.09 ms | 23.5 ms | 0 |
| Same, 30 s (second run, logged) | 6.00 ms | 6.05 ms | 12.00 ms | 12.05 ms | 22.2 ms | 0 |
| With a 1 ms-timeout read before each write | 5.42 ms | 5.94 ms | 31.0 ms | 33.0 ms | 34.0 ms | 0 |

The firmware answers each request on a fixed 6 ms cadence: 5000 exchanges within
5.35 to 6.24 ms. A two-group cycle is 12.0 ms with about one cycle in 2500 reaching
22 ms. A three-group profile would cycle at 18 ms, five groups at 30 ms.

Pipelining: writing both group requests and then reading both replies takes 12.0 ms
until the first reply arrives and 0.002 ms between the two replies (`pipeline2`,
834 cycles). The firmware queues the second request but holds the first reply until
both are done, so pipelining delays the first group by 6 ms and gains nothing. All
1250 cycles of the earlier `pipeline` run returned replies in request order.

The timeout-based drain costs about 10 ms per drain on this PC (31.0 ms cycle less
10.8 ms of exchanges, two drains per cycle) and drained zero stale replies in 90 s of
polling with GG in the tray. HidSharp offers no non-blocking read.

Decision 2: poll back to back with no floor and no drain. Desync defence is the
canary plus a per-reply signature check (below), not a drain.

## Pad path (readback, readback2, disconnect, kill, kill5)

Submit to XInput readback, submits every 2 ms for 10 s:

| Sampler | p50 | p99 | max |
| --- | --- | --- | --- |
| 1 ms waitable timer (quantised) | 0.52 to 1.00 ms | 1.60 ms | 1.85 ms |
| Spinning (`readback2`) | 0.017 ms | 0.032 ms | 0.099 ms |

The driver path is tens of microseconds; the earlier 1 ms figure was the sampler's
own period. Distinct XInput packets per second at 500 submits per second: 473 to 487.

Graceful zero, disconnect, and client dispose from user mode: 0.20 to 0.89 ms over
five trials.

Kill test, six trials in total (`kill` once, `kill5` five times): pad at right trigger
255, child hook swallowing F13 including injected events, message pump alive on the
hook thread, another thread blocked. After `TerminateProcess`, XInput reported
`ERROR_DEVICE_NOT_CONNECTED` at the first poll every time (4 to 5 ms including
`WaitForExit`). Synthetic F13 reached the parent's observer hook 0 times while the
child lived and 2 times after it died, in every trial. The observer-hook check
replaces the plan's foreground-window check; it proves the same thing because
low-level hooks run newest first, so the child's hook sat ahead of the parent's.

Decision 4: in-process ViGEm is safe on process death. No watchdog process.

## Hook callback cost (hookcost)

600 synthetic F13 events through a `WH_KEYBOARD_LL` callback, with GG's own hooks in
the chain:

| Path | p50 | p99 | p99.9 | max | over 1 ms |
| --- | --- | --- | --- | --- | --- |
| Pass through (`CallNextHookEx`) | 0.070 ms | 0.179 ms | 0.322 ms | 0.376 ms | 0 |
| Swallow | 0.000 ms | 0.000 ms | 0.000 ms | 0.001 ms | 0 |

The pass-through cost is the rest of the hook chain, not this process. Both are far
below the 300 ms `LowLevelHooksTimeout`.

## Rest noise and drift (rest, 2 min hands off, 3936 samples, 0 faults; 10 min in session 2)

| Key | Raw mean | Raw p-p | Raw drift | Filtered mean | Filtered p-p | Filtered drift |
| --- | --- | --- | --- | --- | --- | --- |
| W | 877.0 | 14 | -0.1 | 876.5 | 5 | -0.2 |
| A | 843.4 | 15 | -0.2 | 843.0 | 5 | -0.1 |
| S | 846.5 | 17 | 0.0 | 846.0 | 5 | -0.2 |
| D | 847.3 | 14 | -0.1 | 846.8 | 5 | -0.1 |

Worst peak to peak over all 28 sensors in groups 2 and 3: raw 21, filtered 11. Drift
under one count in two minutes.

Session 2 repeated the run for ten minutes hands off (49,998 samples, 0 faults):

| Key | Raw mean | Raw p-p | Raw drift | Filtered mean | Filtered p-p | Filtered drift |
| --- | --- | --- | --- | --- | --- | --- |
| W | 871.0 | 18 | +0.5 | 870.5 | 6 | +0.5 |
| A | 837.9 | 23 | +0.4 | 837.4 | 7 | +0.4 |
| S | 840.4 | 25 | +0.4 | 839.9 | 6 | +0.4 |
| D | 842.4 | 34 | +0.5 | 841.9 | 16 | +0.5 |

Worst peak to peak over all 28 sensors: raw 64, filtered 16. Drift is half a count in
ten minutes, so rest does not wander, but the raw peak to peak roughly doubles between
two minutes and ten. Calibration samples rest for seconds and will see the smaller
figure, so the floor of the noise band is set from the ten-minute data: 40 counts,
twice D's 34, and still under the 54 to 80 counts at which the keyboard's own digital
key-down fires (travel table below). Calibration raises the band to 1.5 times the
measured rest noise when that is larger. The cost is about 0.12 mm of travel at the
start of every key that reads as released; the feel step looks at that region.

## Travel (travel)

| Key | Rest | Full press | Span | Direction | Digital key-down fires at |
| --- | --- | --- | --- | --- | --- |
| W | 878 | 4095 | 3217 | up | 957 counts, 2.5% of W's span |
| A | 843 | 3558 | 2715 | up | |
| S | 847 | 3559 | 2712 | up | |
| D | 850 | 3623 | 2773 | up | |

Session 2, two days and a replug later:

| Key | Rest | Full press | Span | Direction | Digital key-down fires at |
| --- | --- | --- | --- | --- | --- |
| W | 872 | 4095 | 3223 | up | 952 counts, 80 above rest, 2.5% of span |
| A | 838 | 3568 | 2730 | up | 906 counts, 68 above rest, 2.5% |
| S | 841 | 3559 | 2718 | up | 901 counts, 60 above rest, 2.2% |
| D | 842 | 3679 | 2837 | up | 896 counts, 54 above rest, 1.9% |

W clips at 4095 both times, so the clip is a property of that key, not a one-off. Rest
moved by 5 to 8 counts between sessions and full press by up to 56 (D), within the
noise band and two percent of span respectively, so a stored calibration stays valid
across days and a replug.

Consequences carried into the design:

- W reaches the 12-bit ceiling before or at bottom-out. Calibration must detect a
  clipping key (full press reads 4095) and tell the user the top of that key's travel
  produces no change. A key
  sitting at 4095 for a long time is a plausibility warning, not a valid depth.
- W's span is 18% larger than A, S, and D's, so per-key calibration is mandatory.
- Counts grow slowly near rest: 0.2 mm of the roughly 4 mm of travel is 2.5% of the
  count span, so the bottom of travel is compressed. A response curve that expects
  the first 5% of counts to be the first 5% of travel is wrong by a factor of two
  there. The feel step in stage 5 must look at this region specifically.

Held-W fixtures: W at 4095 in group 2 slot 2; Enter, pressed to confirm the prompt,
moved from 871 to 987 in group 3 slot 13.

## Step response (step, 15 s, 72 fast W taps, 6.00 ms sample period)

Everything here is quantised to the 6 ms sample period; rise times are counts of
samples, not continuous measurements.

| | p50 | p99 | max | mean |
| --- | --- | --- | --- | --- |
| Raw 10 to 90 percent rise | 1 sample | 3 samples | 4 samples | 0.85 samples |
| Filtered 10 to 90 percent rise | 2 samples | 3 samples | 4 samples | 1.76 samples |
| Filtered lag behind raw at 50 percent | 1 sample | 1 sample | 1 sample | 0.51 samples |

A fast tap rises within one raw sample, so the rise itself is below 6 ms and
unresolved. The filtered value lags raw by one sample on about half the presses and
zero on the rest: a sub-sample lag of roughly 3 ms aliased to 0 or 6. Filtered noise
at rest is 5 counts against raw's 14 to 17 on a span of about 3000.

Decision 1: use the raw bytes (offsets 1..28). Three to six milliseconds of lag on a
12 ms cycle for 0.3 percent of travel in noise is a bad trade on a throttle.

## Latency (e2e)

Only the medians here are trustworthy. The anchors were last-seen values rather than
paired with each press, so the tails include readings attributed to a previous tap;
`e2e2` fixes that (consume-once anchors, both groups polled) and runs in session 2.

| Anchor to first XInput readback at 5 percent, group 2 only polled | n | p50 |
| --- | --- | --- |
| Sensor reading crosses 5 percent (the anchor is the read itself) | 94 | 0.70 ms |
| Keyboard's own digital key-down (fires at 2.5 percent) | 93 | 3.2 ms |

The first row is submit to readback with the sampler's quantisation on top, the same
quantity `readback2` measured at 0.017 ms; it says nothing about the sensor path.
The second row is the informative one: on a 6 ms single-group loop, the analog output
trailed the keyboard's own digital report by 3.2 ms median. On the shipped two-group
12 ms cycle the expected figure was about 6 ms median and 15 ms worst case.

`e2e2` (session 2, both groups polled, consume-once anchors, 20 s of W taps, 43
presses seen by the hook, 0 unpaired readbacks) confirms it:

| Anchor to first XInput readback at 5 percent | n | p50 | p99 | max |
| --- | --- | --- | --- | --- |
| Sensor reading crosses 5 percent | 36 | 6.01 ms | 6.18 ms | 6.24 ms |
| Keyboard's own digital key-down | 35 | 9.87 ms | 15.54 ms | 15.89 ms |

The first row is one exchange, as expected: the anchor read is followed by the other
group's exchange before the engine ticks. The second row is the keyboard-to-pad figure
a player feels on the fallback path: 10 ms median, 16 ms worst, on a 12 ms cycle.

The reference's 10 Hz output and its unmeasured but resolution-dependent engine tick
are sufficient to explain its feel, and this design removes both. Two independent
causes also stand and this design must address them: W's clip at 4095 and the
compressed near-rest response above. The reference's ramps did nothing and its
calibration had a step at the start of travel, both already in the design's "Why".

## Decisions

1. Raw bytes for depth. Noise band 40 counts, or 1.5 times the measured rest
   peak-to-peak, whichever is larger. A key that clips stores 4095 as full press and
   is flagged as clipping.
2. Poll back to back, no floor, no drain. Freshness is a fixed 60 ms (about five
   two-group cycles) because a false fallback is sticky and costs more than holding a
   depth for 60 ms; the adaptive `3 x p99` rule is withdrawn. A read timeout (150 ms)
   is a fault; three consecutive cycles of 100 ms or more are a fault; a single slow
   cycle only makes the snapshot stale. Desync defence: the 0x90 canary every 50
   cycles, plus a per-reply signature check (the slots that read under 50 at rest,
   recorded at calibration, must match on every reply; any value above 4095 marks a
   firmware reply in a sensor slot). Both checks are free.
3. Engine timer: the high-resolution waitable timer, 1 ms period.
4. Process death unplugs the pad at the first poll, six trials. No watchdog process.
5. Thresholds for `HardwareThresholds.cs`:

| Threshold | Measured | Value |
| --- | --- | --- |
| Timer period p99 | 1.94 ms | 3 ms |
| Sensor cycle p99 for N groups (canary included) | 12.05 ms for 2, plus a 6 ms canary 2% of cycles | 6 x N + 9 ms (21 ms for two) |
| Readback p99, spin sampler | 0.032 ms | 1 ms |
| XInput packets per second while changing every tick | 473 to 487 | 400 |
| Loopback engine-to-readback p99 (timer p99 plus readback) | 1.94 + 0.03 ms | 4 ms |
| Kill: pad gone after TerminateProcess | first poll, 4 to 5 ms, six trials | 500 ms |
| Watchdog: zero and unplug after the engine stalls | 200 ms staleness + up to 62.5 ms timer granularity + 1 ms disconnect | 400 ms |
| Hook callback p99.9, and events over 1 ms per 1000 | 0.32 ms, 0 | 1 ms, 1 |

Response representation: exponent, saturation point, deadzone, chosen on editing and
serialisation grounds.

## Session 2 (2026-09-21, the maintainer at the keyboard)

All six open runs done, in this order: `rest --minutes 10`, `e2e2 --seconds 20`,
`travel`, `listen --seconds 5` while tapping, unplug and replug then `containers`,
`cycle --no-drain --seconds 30` twice. Results are folded into the sections above;
the cycle runs are here.

`cycle --no-drain`, 30 s each, SteelSeriesEngine, GG, GGClient, GGEZ, Moments and
Prism all running. First with GG open on the OmniPoint actuation page (live per-key
depth on screen), then with Forza Horizon 6 in the foreground and being played (hands
on W, A, S, D):

| Foreground | Exchange p50 | Exchange p99 | Exchange max | Cycle p50 | Cycle p99 | Cycle max | Faults | Stale replies |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| GG actuation page | 5.998 ms | 6.017 ms | 6.058 ms | 12.001 ms | 12.021 ms | 21.826 ms | 0 | 0 |
| Forza Horizon 6, played | 5.998 ms | 6.085 ms | 7.674 ms | 12.000 ms | 12.109 ms | 23.199 ms | 0 | 0 |

Identical to the stage 0 idle runs. GG reading the keys for its own display, a game in
front, and key presses do not touch the sensor path. (The GG run's figures come from
the console; its log was overwritten by the second run.)

Nothing is open from the measurement plan. Decisions 1 to 5 stand, with the noise band
floor raised to 40 in decision 1.
