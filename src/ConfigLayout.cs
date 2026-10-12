using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;

namespace MainMod;

/// <summary>
/// Writes MainMod.cfg ourselves, grouped by what belongs together, one "# value: (unit) # explanation" line per setting.
/// BepInEx would write its own long format (description, type, default) in alphabetical section order; the ConfigFile is therefore created
/// with saving switched off (see Plugin.BindConfig) and this class renders the file once per game start from the values it has loaded.
/// The file is still plain BepInEx config syntax ("[Section]" / "Key = value"; '#' lines are ignored when reading), so editing it and
/// re-reading it at each mission start works as before. Settings that are bound but missing here are appended under their section.
/// </summary>
internal static class ConfigLayout
{
    private sealed class Item
    {
        public string Section, Key, Unit, Text, Heading;
    }

    private const string Bool = "true/false";

    private static readonly List<Item> Layout = new List<Item>();

    private static void H(string section, string heading) => Layout.Add(new Item { Section = section, Heading = heading });
    private static void S(string section, string key, string unit, string text) =>
        Layout.Add(new Item { Section = section, Key = key, Unit = unit, Text = text });

    static ConfigLayout()
    {
        const string g = "General";
        S(g, "Enabled", Bool, "Master switch; false = the mod does nothing");
        S(g, "VerboseLog", Bool, "Debug logging of launches, landings, slowdowns and speed changes to BepInEx/LogOutput.log");

        const string t = "TimeCompression";
        H(t, "Highest speed");
        S(t, "MaxCompression", "x", "Highest speed of the - / = keys (game: 100); snaps to 100 200 300 500 1000 2000 3000 5000");
        H(t, "Flight decks");
        S(t, "FlightDeckLimit", "x", "Speed forced during real deck work (game: 30); 0 = keep the game's value, a higher number lifts it; instant launches / landings never trigger it");

        const string c = "Combat";
        H(c, "Master switches");
        S(c, "SlowDownInCombat", Bool, "Slow time near fights (rules below) instead of the game's blanket 'any weapon anywhere' rule; false = the game's own limits (weapons 20x, guns 30x, attack runs 20x)");
        S(c, "OnlySeen", Bool, "Only react to things your side has detected, so a slowdown never reveals unseen enemies");
        H(c, "Munitions (missiles, bombs, rockets; not torpedoes, drop tanks, decoys)");
        S(c, "CombatRadiusNm", "nm", "A munition this close to the nearest unit (either side) counts as combat");
        S(c, "CombatSpeed", "x", "Speed forced in combat; also used for guns, CIWS, attack runs and aircraft near ships");
        H(c, "Torpedoes");
        S(c, "TorpedoSlowdown", Bool, "Torpedoes use their own rule below; false = they follow the munition rule");
        S(c, "TorpedoRadiusNm", "nm", "A torpedo this close to any ship or submarine counts");
        S(c, "TorpedoSpeed", "x", "Speed forced while a torpedo is in range");
        H(c, "Aircraft near ships");
        S(c, "AircraftNearShips", Bool, "Slow time while an aircraft is close to a ship");
        S(c, "AircraftShipRadiusNm", "nm", "Distance between aircraft and ship that counts");
        S(c, "AircraftShipHostileOnly", Bool, "Only ships hostile to the aircraft count; false = any ship, even your own");
        S(c, "AircraftShipIncludeHelicopters", Bool, "Helicopters also trigger it; false = fixed-wing only");

        const string a = "AirOps";
        H(a, "Instant launch");
        S(a, "InstantLaunch", Bool, "Above InstantLaunchAbove aircraft skip elevator / taxi / catapult and appear in the air");
        S(a, "InstantLaunchAbove", "x", "Speed above which launches are skipped (30 = where the game's own air-ops limit stopped you)");
        S(a, "InstantLaunchHelicopters", Bool, "Also skip the deck sequence for helicopters");
        S(a, "InstantLaunchInProgress", Bool, "Also finish launches already under way when the speed passes the threshold");
        S(a, "PlaneLaunchHeightFeet", "ft", "Height above the deck where a launched plane appears");
        S(a, "HelicopterLaunchHeightFeet", "ft", "Height above the pad where a launched helicopter appears (keep at 30 or more)");
        S(a, "ForwardOffsetFeet", "ft", "How far ahead of the launch point aircraft appear (about a catapult run)");
        H(a, "Gap between aircraft");
        S(a, "LaunchDelaySeconds", "s", "Game seconds between aircraft leaving the same deck while launches are skipped; 0 = off");
        S(a, "LaunchDelayOnlyWhileSkipping", Bool, "Apply the gap only above InstantLaunchAbove; false = always");
        S(a, "LaunchDelayAiDecks", Bool, "The gap also applies to enemy / AI decks, not only yours");
        H(a, "Instant landing");
        S(a, "InstantLanding", Bool, "Above InstantLandingAbove returning aircraft skip approach, arrest, taxi and elevator (recovered normally, no loss)");
        S(a, "InstantLandingAbove", "x", "Speed above which landings are skipped");

        const string p = "Performance";
        H(p, "Simple physics");
        S(p, "SimplePhysicsAbove", "x", "Above this speed aircraft, helicopters, submarines and animals use the game's simple movement; 0 = game's values");
        H(p, "Adaptive simulation step (fewer, bigger steps at high speed = less lag, coarser movement)");
        S(p, "AdaptiveFixedStep", Bool, "Let the simulation step grow when the simulation loop is the bottleneck");
        S(p, "AdaptiveStepAbove", "x", "Speed above which the adaptive step is used (never during 20x fights)");
        S(p, "AdaptiveStepLoadTarget", "0-1", "Share of wall time the simulation loop may use before the step grows (0.4 = 40%)");
        S(p, "MaxFixedStepSeconds", "s", "Largest step allowed (0.0167 normal, 0.0333 low-FPS, 0.05 / 0.0667 / 0.1 = progressively coarser)");
        H(p, "Profiler");
        S(p, "Profile", Bool, "Write a [perf] breakdown of where the frame time goes to the log");
        S(p, "ProfileIntervalSeconds", "s", "Seconds between profile reports");
    }

    internal static void Write(ConfigFile cfg, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Time compression extras settings. Re-read at every mission start; this file is regenerated (comments and order only, your values are kept) when the game starts.");
        sb.AppendLine("# Each setting: '# value: (unit) # what it does', then 'Key = value'. Units: x = time compression, nm = nautical miles, ft = feet, s = seconds.");
        sb.AppendLine();

        var known = new HashSet<string>();
        string[] sectionOrder = Layout.Select(i => i.Section).Distinct().ToArray();
        foreach (string section in sectionOrder)
        {
            sb.AppendLine("[" + section + "]");
            sb.AppendLine();
            foreach (Item item in Layout.Where(i => i.Section == section))
            {
                if (item.Heading != null)
                {
                    sb.AppendLine("# ---- " + item.Heading + " ----");
                    continue;
                }
                known.Add(section + "\n" + item.Key);
                if (!TryGet(cfg, section, item.Key, out ConfigEntryBase entry)) continue;
                sb.AppendLine("# value: (" + item.Unit + ") # " + item.Text);
                sb.AppendLine(item.Key + " = " + entry.GetSerializedValue());
            }
            // anything bound but not listed above, so a new setting can never go missing from the file
            foreach (ConfigDefinition def in cfg.Keys.Where(k => k.Section == section && !known.Contains(k.Section + "\n" + k.Key)))
            {
                ConfigEntryBase entry = cfg[def];
                sb.AppendLine("# value: (" + (entry.SettingType == typeof(bool) ? Bool : "?") + ") # " + (entry.Description?.Description ?? "").Replace('\n', ' '));
                sb.AppendLine(def.Key + " = " + entry.GetSerializedValue());
                known.Add(def.Section + "\n" + def.Key);
            }
            sb.AppendLine();
        }
        foreach (ConfigDefinition def in cfg.Keys.Where(k => !known.Contains(k.Section + "\n" + k.Key)).ToList())
        {
            // a whole section that is not in the layout
            ConfigEntryBase entry = cfg[def];
            sb.AppendLine("[" + def.Section + "]");
            sb.AppendLine("# value: (" + (entry.SettingType == typeof(bool) ? Bool : "?") + ") # " + (entry.Description?.Description ?? "").Replace('\n', ' '));
            sb.AppendLine(def.Key + " = " + entry.GetSerializedValue());
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static bool TryGet(ConfigFile cfg, string section, string key, out ConfigEntryBase entry)
    {
        entry = null;
        var def = new ConfigDefinition(section, key);
        if (!cfg.Keys.Contains(def)) return false;
        entry = cfg[def];
        return true;
    }
}
