# Changelog

## 0.7.0
- Torpedo rule now triggers within 10 nm and caps at 20x (was 50 nm / 50x).
- Fix: ASROC / SS-N-16 / RBU rockets gained speed when the time compression dropped (their speed was derived from the previous step's movement divided by the new, shorter step).
- Fix: unguided bomb steering went unstable at large simulation steps; the steering step is now limited to what the game itself uses at 20x.
- Launch delay now also spaces out aircraft that were already queued on the deck when instant launch kicked in (previously they all left in the same frame).
- "Simple physics" (helicopters/submarines/animals/fixed-wing) is now off by default.
- Debug logging (`VerboseLog`) is off by default; when on it also logs every time-compression change with its cause.
- Renamed to "Time Compression Extras".

## 0.6.0
- Combat slowdown (munition / aircraft-near-ship / torpedo / guns), no-leak filter, adaptive simulation step, optional simple physics, profiler.

## 0.4.0 - 0.5.0
- Instant launch from any deck state, instant landing, launch delay, configurable thresholds.

## 0.3.0
- Time compression cap 500x, instant launch above 30x.
