# Changelog

## 0.8.0
- Fix: sonobuoys can be laid at high time compression. Above 20x the aircraft could not line up on the drop point and circled it forever; it now releases the buoy on the spot once it is near (1 nm at 20x, growing with the speed up to 10 nm; the aircraft always has to get near first). Helicopters and fixed-wing aircraft have separate settings (helicopters: 0.5 nm, up to 3 nm). Settings in `[Sonobuoys]`.
- Fix: helicopters circled the dipping-sonar point forever at high time compression. Above 20x a helicopter with a dip order now uses the game's simple movement model (flies straight to the point and holds still), and the dip waypoint counts as reached within 0.3 nm. Settings in `[Dipping]`.

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
