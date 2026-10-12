using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SeaPower;

namespace MainMod;

internal static class Plugin
{
    internal const string Version = "0.7.0";
    internal const string ConfigFileName = "MainMod.cfg";

    internal static ManualLogSource Log = Logger.CreateLogSource("MainMod");

    // [General]
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> VerboseLog;

    // [TimeCompression]  (0 = leave the game's own value alone)
    internal static ConfigEntry<float> MaxCompression;
    internal static ConfigEntry<float> FlightDeckLimit;

    // [Combat]
    internal static ConfigEntry<bool> SlowDownInCombat;
    internal static ConfigEntry<float> CombatRadiusNm;
    internal static ConfigEntry<float> CombatSpeed;
    internal static ConfigEntry<bool> TorpedoSlowdown;
    internal static ConfigEntry<float> TorpedoRadiusNm;
    internal static ConfigEntry<float> TorpedoSpeed;
    internal static ConfigEntry<bool> OnlySeen;
    internal static ConfigEntry<bool> AircraftNearShips;
    internal static ConfigEntry<float> AircraftShipRadiusNm;
    internal static ConfigEntry<bool> AircraftShipHostileOnly;
    internal static ConfigEntry<bool> AircraftShipIncludeHelicopters;

    // [AirOps]
    internal static ConfigEntry<bool> InstantLaunch;
    internal static ConfigEntry<float> InstantLaunchAbove;
    internal static ConfigEntry<bool> InstantLaunchHelicopters;
    internal static ConfigEntry<bool> InstantLaunchInProgress;
    internal static ConfigEntry<float> LaunchDelaySeconds;
    internal static ConfigEntry<bool> LaunchDelayOnlyWhileSkipping;
    internal static ConfigEntry<bool> LaunchDelayAiDecks;
    internal static ConfigEntry<bool> InstantLanding;
    internal static ConfigEntry<float> InstantLandingAbove;
    internal static ConfigEntry<float> PlaneLaunchHeightFeet;
    internal static ConfigEntry<float> HelicopterLaunchHeightFeet;
    internal static ConfigEntry<float> ForwardOffsetFeet;

    /// <summary>True when the mod is enabled in its config AND ticked in the Mods menu; re-evaluated at every mission start.</summary>
    internal static bool Active;

    private static ConfigFile _config;
    private static bool _initialized;
    private static readonly HashSet<string> _logged = new HashSet<string>();

    /// <summary>The folder this mod lives in (Sea Power_Data/StreamingAssets/MainMod).</summary>
    internal static string ModDirectory
    {
        get
        {
            string dir = null;
            try { dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location); } catch { }
            return string.IsNullOrEmpty(dir) ? Paths.ConfigPath : dir;
        }
    }

    internal static string ConfigPath => Path.Combine(ModDirectory, ConfigFileName);

    internal static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        BindConfig();
        ApplyPatches();
        Log.LogInfo($"Time compression extras {Version} loaded. Config: {ConfigPath}");
    }

    // ------------------------------------------------------------------ config (lives next to the mod's dll)

    private static void BindConfig()
    {
        // BepInEx would rewrite the file in its own alphabetical long format on every change; ConfigLayout renders it instead (end of this method)
        _config = new ConfigFile(ConfigPath, false);
        _config.SaveOnConfigSet = false;

        Enabled = _config.Bind("General", "Enabled", true,
            "Master switch. false = the mod does nothing (same as unticking it in the Mods menu).");
        VerboseLog = _config.Bind("General", "VerboseLog", false,
            "Debug logging: every instant launch / landing, launch delay, combat slowdown and every change of the time compression (with its cause) in BepInEx/LogOutput.log. Off by default.");

        MaxCompression = _config.Bind("TimeCompression", "MaxCompression", 500f,
            "Highest speed reachable with the - / = keys (game default 100). Snaps to the game's speed steps: 100 200 300 500 1000 2000 3000 5000.");
        FlightDeckLimit = _config.Bind("TimeCompression", "FlightDeckLimit", 0f,
            "Speed the game forces you down to during REAL deck operations: launches already under way at low speed, and landings (game default 30). " +
            "0 = keep the game's value. Instant launches (see [AirOps]) never trigger this limit.");

        SlowDownInCombat = _config.Bind("Combat", "SlowDownInCombat", true,
            "When a munition is within CombatRadiusNm of the nearest unit (either side, including the shooter), time compression drops to CombatSpeed " +
            "so the fight stays stable. Replaces the game's 'any weapon anywhere on the map -> 20x' rule; guns / CIWS and aircraft attack runs also use " +
            "CombatSpeed. false = the game's own rules (weapons 20x, guns 30x, attack runs 20x). The speed is never raised again automatically.");
        CombatRadiusNm = _config.Bind("Combat", "CombatRadiusNm", 50f,
            "Distance in nautical miles between a munition and the nearest unit that counts as 'in combat'.");
        CombatSpeed = _config.Bind("Combat", "CombatSpeed", 20f,
            "Time compression (x) forced while in combat.");
        OnlySeen = _config.Bind("Combat", "OnlySeen", true,
            "No information leaks: the combat slowdown only reacts to things your side knows about, so a speed drop can never tell you that an " +
            "unseen enemy fired or is operating nearby. A munition, torpedo, aircraft, ship, gun, CIWS, attack run or flight deck counts only if it " +
            "belongs to you or an ally, or your side has a track on it (the contact picture of your map). The game's own guns / attack-run / " +
            "flight-deck slowdowns caused by unseen enemies are suppressed too. Applies while SlowDownInCombat = true. false = every object counts, seen or not.");
        TorpedoSlowdown = _config.Bind("Combat", "TorpedoSlowdown", true,
            "Torpedoes get their own rule: a launched torpedo within TorpedoRadiusNm of any ship or submarine (either side, including the one that " +
            "fired it) drops time compression to TorpedoSpeed instead of CombatSpeed. Torpedoes move slowly, so a higher speed is usually fine. " +
            "A torpedo farther than that from every ship / submarine does not slow time at all. Needs SlowDownInCombat = true (see OnlySeen). false = torpedoes " +
            "follow the normal munition rule (CombatRadiusNm / CombatSpeed).");
        TorpedoRadiusNm = _config.Bind("Combat", "TorpedoRadiusNm", 10f,
            "Distance in nautical miles between a torpedo and the nearest ship or submarine for the torpedo rule.");
        TorpedoSpeed = _config.Bind("Combat", "TorpedoSpeed", 20f,
            "Time compression (x) forced while a torpedo is in range. If CombatSpeed is lower and something else is in range, the lower one wins.");
        AircraftNearShips = _config.Bind("Combat", "AircraftNearShips", true,
            "Also drop to CombatSpeed while an aircraft or helicopter is within AircraftShipRadiusNm of a ship (close-in defence weapons and " +
            "aircraft attack runs misbehave at high compression). Part of the combat slowdown: needs SlowDownInCombat = true.");
        AircraftShipRadiusNm = _config.Bind("Combat", "AircraftShipRadiusNm", 10f,
            "Distance in nautical miles between an aircraft and a ship for the rule above.");
        AircraftShipHostileOnly = _config.Bind("Combat", "AircraftShipHostileOnly", true,
            "true = only ships hostile to the aircraft count (an aircraft near its own fleet does not slow time). false = any ship.");
        AircraftShipIncludeHelicopters = _config.Bind("Combat", "AircraftShipIncludeHelicopters", false,
            "true = helicopters also trigger the aircraft-near-ship rule. false = fixed-wing aircraft only (a helicopter loitering near a ship " +
            "would otherwise hold time at CombatSpeed for as long as it stays there; a helicopter that fires something still triggers the munition rule).");

        Performance.Bind(_config);

        InstantLaunch = _config.Bind("AirOps", "InstantLaunch", true,
            "Above InstantLaunchAbove compression, aircraft skip the elevator / taxi / catapult sequence (which gets stuck at high compression) " +
            "and appear in the air at their launch point.");
        InstantLaunchAbove = _config.Bind("AirOps", "InstantLaunchAbove", 30f,
            "Compression (x) above which launches are skipped. 30 is where the game's own air-ops limit used to stop you: at or below it " +
            "the game's own launch sequence is used.");
        InstantLaunchHelicopters = _config.Bind("AirOps", "InstantLaunchHelicopters", true,
            "Also skip the deck sequence for helicopters.");
        InstantLaunchInProgress = _config.Bind("AirOps", "InstantLaunchInProgress", true,
            "Also complete launches that were already under way (taxiing, on the elevator, on the catapult) when compression went above " +
            "InstantLaunchAbove. false = only launches that have not started yet are replaced.");
        LaunchDelaySeconds = _config.Bind("AirOps", "LaunchDelaySeconds", 60f,
            "Gap between aircraft, in GAME seconds (the time that would pass at normal speed, so it shrinks with time compression: 60 s is " +
            "two seconds at 30x). A deck normally spaces launches out by itself (elevators, taxi, catapult); skipping the deck sequence " +
            "removes that, so a whole air wing would appear at once. Every aircraft waits until this much game time has passed since the " +
            "previous aircraft left the same deck. 0 = off.");
        LaunchDelayOnlyWhileSkipping = _config.Bind("AirOps", "LaunchDelayOnlyWhileSkipping", true,
            "Apply the gap only while launches are being skipped (compression above InstantLaunchAbove). At or below it the real deck cycle " +
            "already spaces launches out. false = always apply it.");
        LaunchDelayAiDecks = _config.Bind("AirOps", "LaunchDelayAiDecks", true,
            "true = the gap also applies to enemy and other AI-controlled decks, not only your own. (Launches ordered by mission scripts are never delayed.)");
        InstantLanding = _config.Bind("AirOps", "InstantLanding", true,
            "Above InstantLandingAbove compression, a returning aircraft skips the final approach, arrested landing, taxi and elevator ride: " +
            "it is handed to the game's own 'put away in the hangar' step. The unit is recovered exactly like a normal landing " +
            "(ammunition back to the deck stores, campaign tag back on the deck, turnaround timer started) and is NOT counted as a loss.");
        InstantLandingAbove = _config.Bind("AirOps", "InstantLandingAbove", 30f,
            "Compression (x) above which landings are skipped.");
        PlaneLaunchHeightFeet = _config.Bind("AirOps", "PlaneLaunchHeightFeet", 200f,
            "Height above the deck, in feet, where a launched aircraft appears (200 ft = the height the game treats as 'off the ground').");
        HelicopterLaunchHeightFeet = _config.Bind("AirOps", "HelicopterLaunchHeightFeet", 40f,
            "Height above the helipad, in feet, where a launched helicopter appears; it climbs from there on its own. " +
            "Not lower than ~30 ft: at high compression the helicopter's colliders switch on a frame after launch.");
        ForwardOffsetFeet = _config.Bind("AirOps", "ForwardOffsetFeet", 330f,
            "How far ahead of the launch point (along its heading), in feet, aircraft appear; about the length of a catapult run. " +
            "Keeps them clear of the superstructure and of each other.");

        try
        {
            ConfigLayout.Write(_config, ConfigPath);
        }
        catch (Exception e)
        {
            Log.LogWarning("Could not write " + ConfigFileName + ": " + e.Message);
        }
    }

    /// <summary>Re-read MainMod.cfg so edits made between missions apply without restarting the game.</summary>
    internal static void ReloadConfig()
    {
        try
        {
            _config?.Reload();
        }
        catch (Exception e)
        {
            Log.LogWarning("Could not re-read " + ConfigFileName + ": " + e.Message);
        }
    }

    // ------------------------------------------------------------------ Harmony

    /// <summary>
    /// Apply every [HarmonyPatch] class on its own. PatchAll() stops at the first class that fails, which once took
    /// the whole mod down because of a single bad injection name; here a failing patch is reported and the rest still apply.
    /// </summary>
    private static void ApplyPatches()
    {
        var harmony = new Harmony(Entry.Guid);
        int ok = 0, failed = 0;
        foreach (Type type in typeof(Plugin).Assembly.GetTypes().Where(t => t.IsDefined(typeof(HarmonyPatch), false)))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                ok++;
            }
            catch (Exception e)
            {
                failed++;
                Log.LogError($"Patch {type.Name} FAILED, that feature is off: {e}");
            }
        }

        string[] hooked = Harmony.GetAllPatchedMethods()
            .Where(m => Harmony.GetPatchInfo(m)?.Owners?.Contains(Entry.Guid) == true)
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .OrderBy(n => n)
            .ToArray();
        Log.LogInfo($"Patches applied: {ok} ok, {failed} failed. Hooked: {string.Join(", ", hooked)}");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>True if this mod's folder is ticked in the game's Mods menu.</summary>
    internal static bool EnabledInModsMenu()
    {
        try
        {
            if (!Singleton<FileManager>.InstanceExists(false)) return true;
            string mine = Path.GetFullPath(ModDirectory).TrimEnd('/', '\\');
            foreach (var d in Singleton<FileManager>.Instance.Directories)
            {
                string p = d?.DirectoryInfo?.FullName;
                if (!string.IsNullOrEmpty(p) && string.Equals(Path.GetFullPath(p).TrimEnd('/', '\\'), mine, StringComparison.OrdinalIgnoreCase))
                    return d.IsChecked;
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Could not read Mods menu state: " + e.Message);
        }
        return true;
    }

    /// <summary>Log an exception once per key so a per-frame failure cannot flood the log.</summary>
    internal static void LogOnce(string key, Exception e)
    {
        if (_logged.Add(key))
            Log.LogError($"[{key}] {e}");
    }
}
