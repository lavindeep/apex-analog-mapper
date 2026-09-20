# Stage 0 measurements

Machine: Windows 11 Home 26200, Apex Pro TKL gen 1 (USB 1038:1614, firmware 4.9.1),
ViGEmBus 1.21.442, .NET 10.0.401. Spike source in `spike/Spike`. Raw CSVs are not
committed; the numbers below are the summaries.

Processes running during every keyboard measurement unless noted: SteelSeriesEngine,
SteelSeriesGG, SteelSeriesGGEZ, SteelSeriesMoments, SteelSeriesPrism. Stopping the
engine service needs an elevated prompt, which this session did not have; see the
open items.

## Vendor interface (probe)

The 0xFFC0:0x0001 interface is `mi_01`, 65-byte input and output reports. The other
interfaces are the boot keyboard and mouse collections, consumer control, and a
0xFFC1:0x0001 interface on `mi_04` (input only, 65 bytes) that the reference never
touched. Firmware reply to 0x90 is `4.9.1`.

Rest readings, all five groups (raw and filtered agree within a few counts):

- Sensor slots with a physical key read 833 to 929 counts.
- Slots with no key on this US layout (group 1 slot 13, group 3 slot 12, group 4
  slots 1 and 12, group 5 slots 3, 5, 6, 12, 13) read 4 to 9 counts.

So "under 50 counts at rest" identifies an absent sensor. The try-it plausibility
check and the learn step both use it. Fixtures are in `fixtures/`.

## Timer resolution (tick, 8 s per method)

| Method | p50 | p99 | max |
| --- | --- | --- | --- |
| `Thread.Sleep(1)` | 15.28 ms | 16.54 ms | 16.58 ms |
| `WaitHandle.WaitOne(1)` | 15.35 ms | 16.63 ms | 16.87 ms |
| `Thread.Sleep(1)` after `timeBeginPeriod(1)` | 15.34 ms | 16.58 ms | 17.10 ms |
| High-resolution waitable timer | 1.51 ms | 1.94 ms | 2.83 ms |

Decision 3: the engine uses `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`. Every sleep-based
wait on this PC is 15 ms regardless of `timeBeginPeriod`, which means the reference
engine ticked at about 65 Hz and its sensor loop cycled at about 16 ms plus two
exchanges, never the 1 ms and 5 ms its code claimed.

## Sensor exchange and cycle (cycle, 30 s each)

| Run | Exchange p50 | Exchange p99 | Cycle (groups 2 and 3) p50 | Cycle p99 | Cycle max | Faults |
| --- | --- | --- | --- | --- | --- | --- |
| Back to back, no drain | 6.00 ms | 6.07 ms | 12.00 ms | 12.09 ms | 23.5 ms | 0 |
| Drain via 1 ms read timeout before each write | 5.42 ms | 5.94 ms | 31.0 ms | 33.0 ms | 34.0 ms | 0 |
| Two requests written back to back, then two reads (pipeline, 15 s) | | | 12.00 ms | 12.09 ms | 12.57 ms | 0, all replies in order |

The firmware answers on a fixed 6 ms cadence and does not overlap requests: writing
both groups first gains nothing, though replies stayed in order for all 1250 cycles.
Draining with a timeout read costs a full 15 ms timer quantum per drain and is
unusable. Zero stale replies were ever drained with GG running.

Decision 2: poll back to back with no floor; a W, A, S, D cycle is 12 ms. Freshness
limit `max(3 x p99, 12 ms)` evaluates to about 36 ms on this board; 100 ms stays the
hard fault. The drain must be a true non-blocking read (overlapped I/O with a read
always pending), not a timeout read; stage 2 implements it that way and the canary
covers the rest.

## Pad readback floor (readback, 10 s, submits every 2 ms)

| | p50 | p99 | max |
| --- | --- | --- | --- |
| Submit to XInput readback | 1.00 ms | 1.64 ms | 2.40 ms |
| Sampler period (high-resolution timer) | 1.03 ms | 1.63 ms | 2.52 ms |

Distinct XInput packets per second: 487 at 500 submits per second. The pad path is not
a bottleneck. Threshold for the loopback test: p99 under 4 ms, packets per second at
least 400 while input changes every tick.

## Kill test (kill)

Pad at right trigger 255, child hook swallowing W including injected events. After
`TerminateProcess`, XInput reported `ERROR_DEVICE_NOT_CONNECTED` after 5 ms. Synthetic
W reached the parent's observer hook 0 times while the child lived and 2 times after
it died.

Decision 4: in-process ViGEm is safe on process death. No watchdog process. The
in-process watchdog still covers a wedged engine. Kill test threshold: 500 ms.

## Keyboard settings

The maintainer runs GG actuation at about 0.2 mm on every OmniPoint key with rapid
trigger enabled. Firmware 4.9.1 is the August 2023 OmniPoint 2.0 update that brought
the legacy Apex Pro TKL to 0.1 mm actuation and rapid trigger. Consequences are
recorded in the design (gate clearing for analog keys uses the sensor only; fallback
is coarse under rapid trigger).

## Rest noise and drift (rest, 2 min hands off, 3936 samples, 0 faults)

| Key | Raw mean | Raw p-p | Raw drift | Filtered mean | Filtered p-p | Filtered drift |
| --- | --- | --- | --- | --- | --- | --- |
| W | 877.0 | 14 | -0.1 | 876.5 | 5 | -0.2 |
| A | 843.4 | 15 | -0.2 | 843.0 | 5 | -0.1 |
| S | 846.5 | 17 | 0.0 | 846.0 | 5 | -0.2 |
| D | 847.3 | 14 | -0.1 | 846.8 | 5 | -0.1 |

Worst peak to peak over all 28 sensors in groups 2 and 3: raw 21, filtered 11. Drift
over two minutes is under one count. A noise band of 15 counts is right for raw and
generous for filtered.

An earlier 10-minute run was contaminated by typing and is discarded, but it agrees
on range: W rose to 4095, A to 3491, S to 3552, D to 3630.

## Travel (travel)

| Key | Rest | Full press | Span | Direction | Digital key-down fires at |
| --- | --- | --- | --- | --- | --- |
| W | 878 | 4095 | 3217 | up | 957 counts, 2% of travel |
| A | 843 | 3558 | 2715 | up | not measured (hook tracks W only) |
| S | 847 | 3559 | 2712 | up | |
| D | 850 | 3623 | 2773 | up | |

W reaches the 12-bit ceiling before or at bottom-out, so its top of travel is
unmeasurable and the calibration's full-press value for W is 4095. A, S, and D do not
clip. The maintainer's 0.2 mm actuation setting fires at 2% of W's count span; the
sensor response is not linear in millimetres near rest.

Held-W fixtures (`fixtures/w-held-*.hex`): W at 4095 in group 2 slot 2; the Enter key
pressed to confirm the prompt shows in group 3 slot 13 (871 to 987), which confirms
the map entry for Enter.

## Step response and end-to-end latency

First runs of `step` and `e2e` were invalidated by the spike itself: the timeout-based
drain made the sample period 15 ms, and the e2e sampler fired at a shallower depth
than its anchor, so half the readings carried the previous tap's timestamp (p50
0.72 ms, p99 236 ms, a bimodal artefact). Both commands were fixed (no drain, sampler
level equal to the anchor level) and rerun; results below.

Pending rerun.

## Decisions

1. Raw versus filtered: pending `step`.
2. Sensor pacing and freshness: back to back, freshness about 36 ms, hard fault 100 ms.
3. Engine timer: high-resolution waitable timer, 1 ms period.
4. Process death: safe, no watchdog process.
5. Thresholds: see `HardwareThresholds.cs` when written in stage 3. Readback p99 4 ms;
   packets per second 400; kill 500 ms; watchdog bound 200 + 50 + 5 + margin, set
   to 400 ms; timer p99 3 ms.

## Open items

- Cycle with the SteelSeries engine service stopped was not measured (needs an
  elevated prompt). With it running, zero faults and zero stale replies in 90 s of
  polling, so the shared-interface risk did not show up here.
