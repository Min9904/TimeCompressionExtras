# Time Compression Extras

A [Sea Power](https://store.steampowered.com/app/1286220/) mod that makes high time compression usable: a higher speed cap, a combat slowdown that only reacts to what you can see, instant carrier launches and landings, and fixes for weapons that misbehave when the speed changes.

**Requires:** [AnchorChain](https://steamcommunity.com/sharedfiles/filedetails/?id=3380210757) · **Game:** Sea Power 0.8.x · **Version:** 0.7.0

## Install

- **Steam Workshop:** subscribe (and subscribe to AnchorChain), then tick the mod in the in-game Mods menu.
- **Manual:** copy the `TimeCompressionExtras` folder from the release zip into `Sea Power/Sea Power_Data/StreamingAssets/` and tick it in the Mods menu.

Players do not need the .NET SDK; it is only needed to build from source.

## Settings

On first start the mod writes `MainMod.cfg` next to its DLL (for a Workshop install: `steamapps/workshop/content/1286220/<item id>/TimeCompressionExtras/`). It is re-read at the start of every mission. Every setting has a one-line comment in the file. Units: `x` = time compression, `nm` = nautical miles, `ft` = feet, `s` = seconds of game time.

### General
| Setting | Default | What it does |
|---|---|---|
| `Enabled` | true | Master switch; false = the mod does nothing. |
| `VerboseLog` | false | Debug log of launches, landings, slowdowns and every speed change (with its cause) in `BepInEx/LogOutput.log`. |

### Speed cap
| Setting | Default | What it does |
|---|---|---|
| `MaxCompression` | 500 | Highest speed of time compression (game: 100). Snaps to 100, 200, 300, 500, 1000, 2000, 3000, 5000. |
| `FlightDeckLimit` | 0 | Max speed the game forces during real deck work (game: 30). 0 = keep the game's value; a higher number lifts it. |

### Combat slowdown
Replaces the game's "any weapon anywhere = 20x" with distance rules. Time drops to the speed below and is never raised again automatically.

| Setting | Default | What it does |
|---|---|---|
| `SlowDownInCombat` | true | Use the rules below. false = the game's own limits (weapons 20x, guns 30x, attack runs 20x). |
| `OnlySeen` | true | Only things your side has detected (own/allied, or tracked on your plotting table) can slow time, so a slowdown never reveals an unseen enemy. |
| `CombatRadiusNm` | 50 | A missile, bomb or rocket this close to the nearest unit (either side) counts as combat. |
| `CombatSpeed` | 20 | Speed forced in combat; also used for guns, CIWS, aircraft attack runs and aircraft near ships. |
| `TorpedoSlowdown` | true | Torpedoes use their own rule; false = they follow the munition rule. |
| `TorpedoRadiusNm` | 10 | A torpedo this close to any ship or submarine counts. |
| `TorpedoSpeed` | 20 | Speed forced while a torpedo is in range. |
| `AircraftNearShips` | true | Slow time while an aircraft is close to a ship. |
| `AircraftShipRadiusNm` | 10 | Aircraft-to-ship distance that counts. |
| `AircraftShipHostileOnly` | true | Only ships hostile to the aircraft count; false = any ship. |
| `AircraftShipIncludeHelicopters` | false | Helicopters also trigger it; false = fixed-wing only. |

### Carrier and airbase operations
Above the threshold, deck sequences that get stuck at high compression are completed instantly. Campaign-safe: aircraft keep their campaign tag and a landing is not counted as a loss.

| Setting | Default | What it does |
|---|---|---|
| `InstantLaunch` | true | Above `InstantLaunchAbove`, aircraft skip elevator, taxi and catapult and appear in the air. |
| `InstantLaunchAbove` | 30 | Speed above which launches are skipped. |
| `InstantLaunchHelicopters` | true | Also skip the deck sequence for helicopters. |
| `InstantLaunchInProgress` | true | Also finish launches already under way when the speed passes the threshold. |
| `PlaneLaunchHeightFeet` | 200 | Height above the deck where a launched plane appears. |
| `HelicopterLaunchHeightFeet` | 40 | Same for helicopters (keep at 30 or more). |
| `ForwardOffsetFeet` | 330 | How far ahead of the launch point aircraft appear. |
| `LaunchDelaySeconds` | 60 | Game seconds between aircraft leaving the same deck while launches are skipped; 0 = off. |
| `LaunchDelayOnlyWhileSkipping` | true | Apply the gap only above `InstantLaunchAbove`; false = always. |
| `LaunchDelayAiDecks` | true | The gap also applies to enemy / AI decks. |
| `InstantLanding` | true | Above `InstantLandingAbove`, returning aircraft skip approach, arrest, taxi and elevator. |
| `InstantLandingAbove` | 30 | Speed above which landings are skipped. |

### Performance (Dangerous)
| Setting | Default | What it does |
|---|---|---|
| `AdaptiveFixedStep` | true | Let the simulation step grow when the simulation loop is the bottleneck (fewer, bigger steps = less lag, coarser movement). |
| `AdaptiveStepAbove` | 30 | Speed above which the adaptive step is used (never during combat slowdown). |
| `AdaptiveStepLoadTarget` | 0.4 | Share of frame time the simulation may use before the step grows (0.4 = 40%). |
| `MaxFixedStepSeconds` | 0.1 | Largest step allowed (0.0167 normal, 0.0333 low-FPS game step). |
| `SimplePhysicsAbove` | 0 | Above this speed aircraft, helicopters, submarines and animals use the game's simple movement. 0 = game's own physics. |
| `Profile` / `ProfileIntervalSeconds` | false / 10 | Write a frame-time breakdown to the log every N seconds (for finding lag causes). |

### Automatic fixes (no settings)
- **ASROC / SS-N-16 / RBU rockets** no longer jump in speed when the compression drops.
- **Unguided bomb steering** no longer goes unstable at large simulation steps.

## Known limits

- Above 20x the game's weapons lose accuracy (bomb steering, shell fuze timing, seekers that look once per step). The combat slowdown protects against that; it does not fix it.
- With the adaptive step, movement is coarser at high compression.

## Building

Needs the .NET SDK (8.0+) and a Sea Power install with BepInEx and AnchorChain. The repository contains no game files.

```
cd src
dotnet build -c Release \
  -p:GameRoot="/path/to/Sea Power/" \
  -p:AnchorChainDir="/path/to/steamapps/workshop/content/1286220/3380210757/"
```

By default the output goes to `<GameRoot>/Sea Power_Data/StreamingAssets/MainMod/`; use `-p:OutDir=somewhere/` to build elsewhere. `tools/PatchCheck` dry-runs every Harmony patch against the real game DLLs (run it before releasing); `tools/ConfigCheck` binds the config the way the game does.

## License

MIT, see `LICENSE`. Sea Power and its assets belong to their owners; this project contains none of them.
