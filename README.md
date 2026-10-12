# Time Compression Extras

A [Sea Power](https://store.steampowered.com/app/1286220/) mod (AnchorChain plugin) that makes high time compression usable.

| | |
|---|---|
| Game | Sea Power 0.8.x (naval combat sim) |
| Needs | [AnchorChain](https://steamcommunity.com/sharedfiles/filedetails/?id=3380210757) (Steam Workshop) |
| Version | 0.7.0 |

## Features

- **Speed cap 500x** (`MaxCompression`) instead of the game's 100x.
- **Combat slowdown.** Replaces the game's blanket "any weapon in flight anywhere = 20x" with distance rules: time drops to `CombatSpeed` (20x) when a munition is within `CombatRadiusNm` (50 nm) of a unit, an aircraft is within 10 nm of a hostile ship, guns/CIWS fire or an aircraft starts an attack run; a torpedo within `TorpedoRadiusNm` (10 nm) of a ship or submarine uses `TorpedoSpeed` (20x). The speed is never raised again automatically.
- **No leaks (`OnlySeen`).** Only objects your side knows about (own/allied, or tracked on your plotting table) can slow time, so a speed drop never reveals an unseen enemy. The game's own gun / attack-run / flight-deck slowdowns caused by unseen enemies are suppressed too.
- **Instant carrier ops above 30x.** Launches (including ones already under way, from any deck state) and landings are completed at once instead of getting stuck in the taxi/catapult physics, whose integration step grows with the compression. Campaign-safe: aircraft keep their campaign tag and a landing is not counted as a loss. `LaunchDelaySeconds` (60 s of game time per deck) spaces aircraft out; mission-scripted launches are left alone.
- **Weapon time-step fixes.** ASROC / SS-N-16 / RBU rockets derive their speed from last step's movement divided by the current step, so they jumped in speed when the compression dropped; the mod rescales that. Unguided bomb steering (`v += miss * dt / tti`) is unstable at large steps; the mod limits the step used there to 1/15 s.
- **Adaptive simulation step** above 30x: when the simulation loop uses more than 40% of the frame time, the step grows (up to `MaxFixedStepSeconds`, 0.1 s) for fewer, bigger steps. Off below 30x and during combat.
- Optional (off by default): the game's "simple physics" for helicopters/submarines/animals/fixed-wing above `SimplePhysicsAbove`; a built-in profiler.

## Install

**Steam Workshop:** subscribe, tick it in the in-game Mods menu.

**Manual:** copy the `TimeCompressionExtras` folder (from the release zip) to `Sea Power/Sea Power_Data/StreamingAssets/MainMod/` (any subfolder of `StreamingAssets` shows up in the Mods menu) and tick it in the Mods menu. Needs BepInEx + AnchorChain installed as usual.

On first start the mod writes `MainMod.cfg` next to its DLL. Every setting has a one-line comment; the file is re-read at the start of every mission. `VerboseLog = true` writes a detailed trail (launches, slowdowns and every change of the time compression with its cause) to `BepInEx/LogOutput.log`.

## Known limits

- Above 20x the game's weapons lose accuracy (bomb steering, shell fuze timing, seekers that look once per step): the combat slowdown is a workaround for that, not a fix.
- Land-base (runway) launches were seen working in testing; runway landings are untested.
- Aircraft queued on a deck cannot be cancelled in the game (only orders that have not spawned yet can be aborted), so a long launch delay keeps more of them committed.
- With the adaptive step, movement is coarser at high compression.

## Building

Needs the .NET SDK (8.0+) and a Sea Power install with BepInEx and AnchorChain. The repository does not contain any game files.

```
cd src
dotnet build -c Release \
  -p:GameRoot="/path/to/Sea Power/" \
  -p:AnchorChainDir="/path/to/steamapps/workshop/content/1286220/3380210757/"
```

By default `GameRoot` is two folders above `src` and the output goes to `<GameRoot>/Sea Power_Data/StreamingAssets/MainMod/`; use `-p:OutDir=somewhere/` to build elsewhere.

`tools/PatchCheck` dry-runs every Harmony patch against the real game DLLs (run it before releasing: `dotnet run --project tools/PatchCheck -- <MainMod.dll> <Sea Power_Data/Managed> <BepInEx/core> <AnchorChain dir>`); `tools/ConfigCheck` binds the config the way the game does.

## License

MIT, see `LICENSE`. Sea Power and its assets belong to their owners; this project contains none of them.
