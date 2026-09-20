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

## Rest noise and drift (rest, 10 min)

Pending; see below once the run completes.

## Interactive measurements

Pending the maintainer's keyboard session: `step` (raw versus filtered lag),
`e2e` (sensor crossing to readback), `travel` (span and digital actuation point),
`probe --w-held` (fixtures with W down).

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
