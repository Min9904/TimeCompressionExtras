using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace MainMod;

internal enum BlockerKind { LadderMax, FlightDeck, Weapons, Guns, Combat, AircraftAttack }

/// <summary>One thing that limits how fast time may run right now.</summary>
internal sealed class CompressionBlocker
{
    public BlockerKind Kind;
    public string Source = "";      // what / where: "America CV-66", "3 weapons in flight"
    public string Reason = "";      // human readable
    public float GameLimit;         // the limit the unmodded game applies (x)
    public float EffectiveLimit;    // the limit that applies with the mod (x); +infinity when the mod lifts it
    public bool LiftedByMod => EffectiveLimit > GameLimit;
    internal int Frame;             // frame it was last reported (decks report every frame)
}

/// <summary>A moment where the game forced the speed down.</summary>
internal sealed class ClampEvent
{
    public double MissionTime;
    public float From, To;
    public string Caller = "";      // e.g. "WeaponBase.OnUpdateEveryFrame"
    public BlockerKind? Kind;       // what the caller belongs to, when recognised
}

/// <summary>Everything a GUI needs in one object.</summary>
internal sealed class CompressionStatus
{
    /// <summary>Highest speed that can be held right now (x).</summary>
    public float Ceiling;
    /// <summary>The blocker that sets the ceiling; null when only the speed ladder does.</summary>
    public CompressionBlocker Limiting;
    /// <summary>All active blockers, lowest effective limit first (includes ones the mod lifts: check LiftedByMod).</summary>
    public List<CompressionBlocker> Blockers = new List<CompressionBlocker>();
    /// <summary>The most recent forced slow-downs, newest first.</summary>
    public List<ClampEvent> RecentClamps = new List<ClampEvent>();
}

/// <summary>
/// What is holding time compression down, as data. The game does not keep such a list: it drops the speed with one-shot calls to
/// GameTime.SetTimeCompression(limit) from many places (a weapon in flight 20x, guns firing 30x, a busy flight deck 30x), and caps
/// the hotkey ladder at config.ini MaxTimeCompression. This class rebuilds the picture from three sources:
///   * the ladder cap                         (GameTime._maxTimeCompression)
///   * flight decks, reported every frame     (AirOps.BeforeUpdate -> ReportDeck), including whether the mod lifts the limit
///   * weapons in flight                      (optional scan, only when the GUI asks: it walks all objects)
/// plus a log of the actual forced slow-downs (a Prefix on GameTime.SetTimeCompression records who asked for what), which is the only
/// way to see the guns / CIWS limit, since that one has no continuous state.
/// A GUI calls <see cref="Snapshot"/> as often as it likes.
/// </summary>
internal static class CompressionBlockers
{
    private static readonly Dictionary<FlightDeck, CompressionBlocker> _decks = new Dictionary<FlightDeck, CompressionBlocker>();
    private static readonly List<ClampEvent> _recent = new List<ClampEvent>();
    private static readonly Dictionary<string, double> _lastLogged = new Dictionary<string, double>();
    private const int MaxRecent = 20;

    private static FieldInfo _maxField;
    private static FieldInfo _ladderField;
    private static FieldInfo _launchedField; // WeaponBase._isLaunched is protected

    internal static void Reset()
    {
        _decks.Clear();
        _recent.Clear();
        _lastLogged.Clear();
    }

    // ------------------------------------------------------------------ feeders

    /// <summary>Called every frame for every flight deck (AirOps.BeforeUpdate).</summary>
    internal static void ReportDeck(FlightDeck fd, int airOpsTasks, string breakdown, bool lifted)
    {
        if (airOpsTasks <= 0)
        {
            _decks.Remove(fd);
            return;
        }
        if (!_decks.TryGetValue(fd, out CompressionBlocker b))
        {
            b = new CompressionBlocker { Kind = BlockerKind.FlightDeck };
            _decks[fd] = b;
        }
        b.Source = fd._baseObject != null ? fd._baseObject.getUIDAndName() : "flight deck";
        b.Reason = "air operations: " + breakdown;
        b.GameLimit = GameTime_OnStart_Patch.VanillaFlightDeck;
        b.EffectiveLimit = lifted ? float.PositiveInfinity : AirOps.FlightDeckBaseLimit;
        b.Frame = Time.frameCount;
    }

    /// <summary>Prefix of GameTime.SetTimeCompression: remember forced slow-downs and who caused them.</summary>
    internal static void NoteSetTimeCompression(float requested)
    {
        if (!Plugin.VerboseLog.Value) return; // debug bookkeeping only (builds a stack trace)
        float from = GameTime.TimeCompression;
        if (!(requested < from - 0.001f)) return; // only drops are interesting

        string caller = FindCaller();
        var ev = new ClampEvent
        {
            MissionTime = GameTime.missionElapsedTime,
            From = from,
            To = requested,
            Caller = caller,
            Kind = Classify(caller),
        };
        _recent.Insert(0, ev);
        if (_recent.Count > MaxRecent) _recent.RemoveAt(_recent.Count - 1);

        if (Plugin.VerboseLog.Value)
        {
            double now = Time.unscaledTime;
            if (!_lastLogged.TryGetValue(caller, out double last) || now - last > 10.0)
            {
                _lastLogged[caller] = now;
                Plugin.Log.LogInfo($"Time compression forced down {from:0}x -> {requested:0}x by {caller}" +
                                   (ev.Kind.HasValue ? $" ({ev.Kind.Value} limit)." : "."));
            }
        }
    }

    private static FieldInfo _indexField;

    /// <summary>Point the speed-ladder index at the current compression (what GameTime.SetTimeCompression does; the property setter does not).</summary>
    internal static void SyncLadderIndex()
    {
        _indexField ??= AccessTools.Field(typeof(GameTime), "_currentTimeScaleIndex");
        _ladderField ??= AccessTools.Field(typeof(GameTime), "_timeScaleValues");
        var ladder = (List<float>)_ladderField.GetValue(null);
        float tc = GameTime.TimeCompression;
        int index = 3; // the game's default step (1x) when the value is not on the ladder
        for (int i = 0; i < ladder.Count; i++)
        {
            if (Mathf.Abs(ladder[i] - tc) < float.Epsilon) { index = i; break; }
        }
        _indexField.SetValue(null, index);
    }

    /// <summary>The setter Prefix shortened or lifted the aircraft attack-run clamp (EngageSurfaceContact).</summary>
    internal static void NoteAircraftAttack(float from, float to)
    {
        _recent.Insert(0, new ClampEvent
        {
            MissionTime = GameTime.missionElapsedTime, From = from, To = to,
            Caller = "EngageSurfaceContact.LevelAttack", Kind = BlockerKind.AircraftAttack,
        });
        if (_recent.Count > MaxRecent) _recent.RemoveAt(_recent.Count - 1);
        double now = Time.unscaledTime;
        if (Plugin.VerboseLog.Value && (!_lastLogged.TryGetValue("attack", out double last) || now - last > 10.0))
        {
            _lastLogged["attack"] = now;
            Plugin.Log.LogInfo($"Aircraft attack run: game wanted {from:0}x -> 20x, using {to:0}x.");
        }
    }

    // ------------------------------------------------------------------ the GUI side

    /// <param name="scanWeapons">also count weapons in flight (walks every object: only ask while the GUI is visible, a few times a second)</param>
    internal static CompressionStatus Snapshot(bool scanWeapons = false)
    {
        var status = new CompressionStatus();
        float ladder = LadderTop();

        status.Blockers.Add(new CompressionBlocker
        {
            Kind = BlockerKind.LadderMax,
            Source = "speed ladder",
            Reason = "highest step of the - / = keys (config.ini MaxTimeCompression)",
            GameLimit = ladder,
            EffectiveLimit = ladder,
        });

        int frame = Time.frameCount;
        foreach (CompressionBlocker b in _decks.Values)
        {
            if (frame - b.Frame <= 2) status.Blockers.Add(b);
        }

        if (Combat.Active)
        {
            status.Blockers.Add(new CompressionBlocker
            {
                Kind = BlockerKind.Combat,
                Source = Combat.Reason,
                Reason = $"a munition is within {Plugin.CombatRadiusNm.Value:0} nm of a unit",
                GameLimit = GameTime_OnStart_Patch.VanillaWeapons,
                EffectiveLimit = Combat.Limit,
            });
        }

        if (scanWeapons && !Combat.Enabled)
        {
            var (count, first) = CountWeaponsInFlight();
            if (count > 0)
            {
                status.Blockers.Add(new CompressionBlocker
                {
                    Kind = BlockerKind.Weapons,
                    Source = count == 1 ? first : $"{count} weapons in flight (e.g. {first})",
                    Reason = "a weapon in flight makes the game drop the speed",
                    GameLimit = GameTime_OnStart_Patch.VanillaWeapons,
                    EffectiveLimit = Globals._weaponsTCLimit,
                });
            }
        }

        status.Blockers = status.Blockers.OrderBy(b => b.EffectiveLimit).ToList();
        status.Ceiling = status.Blockers[0].EffectiveLimit;
        status.Limiting = status.Blockers[0].Kind == BlockerKind.LadderMax ? null : status.Blockers[0];
        status.RecentClamps = new List<ClampEvent>(_recent);
        return status;
    }

    /// <summary>Highest step of the ladder the hotkeys can reach.</summary>
    private static float LadderTop()
    {
        try
        {
            _maxField ??= AccessTools.Field(typeof(GameTime), "_maxTimeCompression");
            _ladderField ??= AccessTools.Field(typeof(GameTime), "_timeScaleValues");
            float max = (float)_maxField.GetValue(null);
            var ladder = (List<float>)_ladderField.GetValue(null);
            float top = ladder[0];
            foreach (float step in ladder)
            {
                if (step <= max) top = step;
            }
            return top;
        }
        catch
        {
            return float.PositiveInfinity;
        }
    }

    private static (int, string) CountWeaponsInFlight()
    {
        int n = 0;
        string first = null;
        if (!Singleton<ObjectsManager>.InstanceExists()) return (0, null);
        var all = Singleton<ObjectsManager>.Instance?._listOfAllObjects;
        if (all == null) return (0, null);
        _launchedField ??= AccessTools.Field(typeof(WeaponBase), "_isLaunched");
        foreach (ObjectBase o in all)
        {
            // same exclusions as WeaponBase.OnUpdateEveryFrame
            if (o is WeaponBase w && (bool)_launchedField.GetValue(w) && w._type != ObjectBase.ObjectType.Noisemaker
                && w._ap._subType != Ammunition.Type.Sonobuoy && w._ap._subType != Ammunition.Type.Paratrooper)
            {
                n++;
                first ??= w.getUIDAndName();
            }
        }
        return (n, first);
    }

    // ------------------------------------------------------------------ caller attribution

    /// <summary>First frame above the Harmony wrapper that belongs to the game, e.g. "WeaponBase.OnUpdateEveryFrame".</summary>
    private static string FindCaller()
    {
        var trace = new StackTrace(2, false);
        int frames = Math.Min(trace.FrameCount, 10);
        for (int i = 0; i < frames; i++)
        {
            MethodBase m = trace.GetFrame(i)?.GetMethod();
            Type t = m?.DeclaringType;
            if (t == null || t == typeof(GameTime) || t.Namespace == "MainMod" || t.Namespace == "HarmonyLib") continue;
            return t.Name + "." + m.Name;
        }
        return "unknown";
    }

    private static BlockerKind? Classify(string caller)
    {
        if (caller.StartsWith("EngageSurfaceContact")) return BlockerKind.AircraftAttack;
        if (caller.StartsWith("WeaponSystemGun") || caller.StartsWith("WeaponSystemCIWS")) return BlockerKind.Guns;
        if (caller.StartsWith("WeaponBase") || caller.StartsWith("WeaponSystemHardpoint") || caller.StartsWith("WeaponSystemLauncher")) return BlockerKind.Weapons;
        if (caller.StartsWith("FlightDeck")) return BlockerKind.FlightDeck;
        return null;
    }
}

[HarmonyPatch(typeof(GameTime), nameof(GameTime.SetTimeCompression))]
internal static class GameTime_SetTimeCompression_Patch
{
    private static bool Prefix(float timeScale)
    {
        try
        {
            // a game clamp (guns, CIWS, ...) caused by an object the player does not know about is cancelled: it would reveal the object
            ObjectBase owner = ClampContext.Owner;
            if (owner != null && Combat.OnlySeenActive && timeScale < GameTime.TimeCompression && !Combat.KnownToPlayer(owner))
                return false;
            CompressionBlockers.NoteSetTimeCompression(timeScale);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("clamp-note", e);
        }
        return true;
    }
}
