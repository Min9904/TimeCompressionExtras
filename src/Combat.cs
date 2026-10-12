using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace MainMod;

/// <summary>
/// Combat slowdown: when a munition is within CombatRadiusNm of the nearest unit of either side, or an aircraft is within
/// AircraftShipRadiusNm of a ship hostile to it, time compression drops to CombatSpeed.
///
/// Replaces the game's blanket rule "any weapon in flight anywhere on the map -> 20x" (Globals._weaponsTCLimit, lifted by TimeCap while
/// this is on) with a distance test, and brings the guns / CIWS limit and the hidden aircraft attack-run clamp
/// (AircraftStates.EngageSurfaceContact.LevelAttack: direct assignment TimeCompression = 20) down to CombatSpeed as well.
/// Evaluated every battle frame (hooked on GameUpdater.update, which only runs in battle: GameTime.OnUpdate also runs on the campaign map).
/// The ring is widened by the distance the mover can cover during one frame (frame time x compression), so that at very high
/// compression and a low frame rate a fast missile or aircraft cannot jump across the ring between two checks.
/// It never raises the speed: after the fight the player presses = again, like with the game's own clamps.
/// </summary>
internal static class Combat
{
    // 1 nautical mile = 1852 m, 1 game unit = 67.200066 m
    private const float UnitsPerNm = 1852f / 67.200066f;
    private const float KnotsToUnitsPerSecond = 0.514444f / 67.200066f;

    // what triggered last (kept as references so that no string is built unless something is logged / shown)
    private static ObjectBase _trigA, _trigB;
    private static float _trigDistanceUnits;
    private static bool _trigAircraft;
    private static double _lastLog = -1000;

    /// <summary>True while at least one trigger is inside its ring.</summary>
    internal static bool Active { get; private set; }

    /// <summary>Why the last slow-down happened, for the log and the GUI.</summary>
    internal static string Reason
    {
        get
        {
            if (_trigA == null || _trigB == null) return "";
            try
            {
                float nm = _trigDistanceUnits / UnitsPerNm;
                if (!_trigAircraft)
                    return $"{_trigA.getUIDAndName()} is {nm:0} nm from {_trigB.getUIDAndName()}";
                return $"aircraft {_trigA.getUIDAndName()} ({SideOf(_trigA)}) is {nm:0.0} nm from ship {_trigB.getUIDAndName()} ({SideOf(_trigB)})";
            }
            catch
            {
                return "";
            }
        }
    }

    internal static bool Enabled => Plugin.Active && Plugin.SlowDownInCombat.Value;

    /// <summary>The no-leak filter is on: only objects the player's side knows about may cause a slowdown.</summary>
    internal static bool OnlySeenActive => Enabled && Plugin.OnlySeen.Value;

    internal static float Speed => Math.Max(1f, Plugin.CombatSpeed.Value);

    internal static float TorpedoSpeed => Math.Max(1f, Plugin.TorpedoSpeed.Value);

    /// <summary>The speed the current trigger asks for (CombatSpeed, or TorpedoSpeed when only torpedoes are in range); for the GUI.</summary>
    internal static float Limit { get; private set; }

    internal static void Reset()
    {
        Active = false;
        Limit = 0f;
        _trigA = _trigB = null;
        _lastLog = -1000;
    }

    private static Taskforce.TfType SideOf(ObjectBase o) => o._taskforce != null ? o._taskforce.Side : Taskforce.TfType.None;

    /// <summary>Per battle frame (Postfix of GameUpdater.update).</summary>
    internal static void Tick()
    {
        if (!Enabled) { Active = false; return; }
        if (GameTime.IsPaused()) return;
        // never create the manager as a side effect (Singleton.Instance would): outside a battle there is nothing to check
        if (!Singleton<ObjectsManager>.InstanceExists()) { Active = false; return; }

        ObjectsManager om = Singleton<ObjectsManager>.Instance;
        if (om == null) { Active = false; return; }
        List<WeaponBase> weapons = om._listOfAllWeapons;
        List<ObjectBase> units = om._listOfAllUnits;
        if (weapons == null || units == null) { Active = false; return; }

        // game seconds that pass between two checks: how far a mover can get before we look again
        float frameGameSeconds = Mathf.Min(Time.unscaledDeltaTime, 0.25f) * GameTime.TimeCompression;

        float target = Speed;
        bool hit = FindAircraftNearShip(units, frameGameSeconds) || FindMunitionNearUnit(weapons, units, frameGameSeconds);
        // torpedoes have their own, usually higher, speed; they only matter when nothing else asks for less
        if (Plugin.TorpedoSlowdown.Value && (!hit || TorpedoSpeed < target) && FindTorpedoNearSeaUnit(weapons, units, frameGameSeconds))
        {
            target = hit ? Math.Min(target, TorpedoSpeed) : TorpedoSpeed;
            hit = true;
        }
        Active = hit;
        if (hit)
        {
            Limit = target;
            Slow(target);
        }
    }

    private static bool FindMunitionNearUnit(List<WeaponBase> weapons, List<ObjectBase> units, float frameGameSeconds)
    {
        if (weapons.Count == 0) return false;
        float radius = Math.Max(0.1f, Plugin.CombatRadiusNm.Value) * UnitsPerNm;
        bool torpedoRule = Plugin.TorpedoSlowdown.Value;
        bool onlySeen = OnlySeenActive;
        for (int i = 0; i < weapons.Count; i++)
        {
            WeaponBase w = weapons[i];
            if (w == null || w.IsDestroyed || !w.isLaunched()) continue;
            if (w._ap._subType == Ammunition.Type.Paratrooper || w._ap._subType == Ammunition.Type.Fueltank) continue; // not munitions
            if (torpedoRule && w is Torpedo) continue;
            float reach = radius + Math.Max(0f, w._velocityInKnots) * KnotsToUnitsPerSecond * frameGameSeconds;
            float r2 = reach * reach;
            Vector3 wp = w.transform.position;
            for (int j = 0; j < units.Count; j++)
            {
                ObjectBase u = units[j];
                if (u == null || u.IsDestroyed || u is Biologic) continue;
                float d2 = (wp - u.transform.position).sqrMagnitude;
                if (d2 <= r2)
                {
                    // the (cheap) distance test first, the contact-table lookups only for pairs that are in range
                    if (onlySeen && !(KnownToPlayer(w) && KnownToPlayer(u))) continue;
                    Remember(w, u, d2, aircraft: false);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>A launched torpedo within TorpedoRadiusNm of any ship or submarine (either side, including the one that fired it).</summary>
    private static bool FindTorpedoNearSeaUnit(List<WeaponBase> weapons, List<ObjectBase> units, float frameGameSeconds)
    {
        float radius = Math.Max(0.1f, Plugin.TorpedoRadiusNm.Value) * UnitsPerNm;
        bool onlySeen = OnlySeenActive;
        for (int i = 0; i < weapons.Count; i++)
        {
            WeaponBase w = weapons[i];
            if (!(w is Torpedo) || w == null || w.IsDestroyed || !w.isLaunched()) continue;
            if (onlySeen && !KnownToPlayer(w)) continue;
            float reach = radius + Math.Max(0f, w._velocityInKnots) * KnotsToUnitsPerSecond * frameGameSeconds;
            float r2 = reach * reach;
            Vector3 wp = w.transform.position;
            for (int j = 0; j < units.Count; j++)
            {
                ObjectBase u = units[j];
                if (!(u is Vessel || u is Submarine) || u.IsDestroyed) continue;
                if (onlySeen && !KnownToPlayer(u)) continue;
                float d2 = (wp - u.transform.position).sqrMagnitude;
                if (d2 <= r2)
                {
                    Remember(w, u, d2, aircraft: false);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// True when the player's side may know about this object: it belongs to the player or an ally, or the player's plotting table (the
    /// contact picture the map shows) holds a track for it. Fails open (true) if the player's side cannot be determined.
    /// </summary>
    internal static bool KnownToPlayer(ObjectBase o)
    {
        try
        {
            Taskforce player = Globals._playerTaskforce;
            if (player == null || o == null) return true;
            Taskforce tf = o._taskforce;
            if (tf != null && (tf == player || tf.Side == Taskforce.TfType.Player || tf.Side == Taskforce.TfType.Ally)) return true;
            return o.GetMapVehicle(player) != null;
        }
        catch (Exception e)
        {
            Plugin.LogOnce("known-to-player", e);
            return true;
        }
    }

    private static bool Hostile(Taskforce.TfType a, Taskforce.TfType b)
    {
        bool aGood = a == Taskforce.TfType.Player || a == Taskforce.TfType.Ally;
        bool bGood = b == Taskforce.TfType.Player || b == Taskforce.TfType.Ally;
        return (aGood && b == Taskforce.TfType.Enemy) || (a == Taskforce.TfType.Enemy && bGood);
    }

    /// <summary>An aircraft within AircraftShipRadiusNm of a ship (hostile to it, unless AircraftShipHostileOnly = false).</summary>
    private static bool FindAircraftNearShip(List<ObjectBase> units, float frameGameSeconds)
    {
        if (!Plugin.AircraftNearShips.Value) return false;
        float radius = Math.Max(0.1f, Plugin.AircraftShipRadiusNm.Value) * UnitsPerNm;
        bool hostileOnly = Plugin.AircraftShipHostileOnly.Value;
        bool includeHelicopters = Plugin.AircraftShipIncludeHelicopters.Value;
        bool onlySeen = OnlySeenActive;
        for (int i = 0; i < units.Count; i++)
        {
            ObjectBase a = units[i];
            if (a == null || a.IsDestroyed) continue;
            if (!(a is Aircraft || (includeHelicopters && a is Helicopter))) continue;
            float reach = radius + Math.Max(0f, a._velocityInKnots) * KnotsToUnitsPerSecond * frameGameSeconds;
            float r2 = reach * reach;
            Taskforce.TfType aSide = SideOf(a);
            Vector3 ap = a.transform.position;
            for (int j = 0; j < units.Count; j++)
            {
                ObjectBase s = units[j];
                if (!(s is Vessel) || s.IsDestroyed) continue;
                if (hostileOnly && !Hostile(aSide, SideOf(s))) continue;
                float d2 = (ap - s.transform.position).sqrMagnitude;
                if (d2 <= r2)
                {
                    if (onlySeen && !(KnownToPlayer(a) && KnownToPlayer(s))) continue;
                    Remember(a, s, d2, aircraft: true);
                    return true;
                }
            }
        }
        return false;
    }

    private static void Remember(ObjectBase a, ObjectBase b, float d2, bool aircraft)
    {
        _trigA = a;
        _trigB = b;
        _trigDistanceUnits = Mathf.Sqrt(d2);
        _trigAircraft = aircraft;
    }

    private static void Slow(float speed)
    {
        float from = GameTime.TimeCompression;
        if (from <= speed + 0.001f) return; // already at or below the combat speed
        GameTime.SetTimeCompression(speed);
        double now = Time.unscaledTime;
        if (Plugin.VerboseLog.Value && now - _lastLog > 5.0)
        {
            _lastLog = now;
            Plugin.Log.LogInfo($"Combat slowdown: {from:0}x -> {speed:0}x: {Reason}.");
        }
    }
}

[HarmonyPatch(typeof(GameUpdater), nameof(GameUpdater.update))]
internal static class GameUpdater_Update_Combat_Patch
{
    private static void Postfix()
    {
        try
        {
            Combat.Tick();
        }
        catch (Exception e)
        {
            Plugin.LogOnce("combat-tick", e);
        }
    }
}

/// <summary>
/// Direct assignments to GameTime.TimeCompression bypass SetTimeCompression. The only one that matters is the aircraft attack-run
/// clamp in EngageSurfaceContact (TimeCompression = 20). With the combat slowdown on it becomes min(current, CombatSpeed) - or, with the
/// slowdown off it is left alone. Like the game's own clamp it leaves the speed-ladder index alone, so Postfix
/// re-syncs it (otherwise the next - / = key press would start from the old, higher step).
/// </summary>
[HarmonyPatch(typeof(GameTime), nameof(GameTime.TimeCompression), MethodType.Setter)]
internal static class GameTime_TimeCompression_Setter_Patch
{
    private static bool _changed;

    private static void Prefix(ref float value)
    {
        _changed = false;
        try
        {
            TcTrace.Note(value);
            if (!Plugin.Active || !(value > 0f)) return;
            float current = GameTime.TimeCompression;
            // only the clamp case: a drop to exactly 20 from above
            if (value != 20f || !(current > 20f)) return;
            if (!CalledFromAttackRun()) return;

            // an aircraft the player does not know about must not slow time (and its own vanilla clamp to 20x is cancelled)
            if (Combat.OnlySeenActive && ClampContext.Owner != null && !Combat.KnownToPlayer(ClampContext.Owner))
            {
                value = current;
                return;
            }

            if (!Plugin.SlowDownInCombat.Value) return; // the game's own 20x attack-run clamp stays

            float limit = Math.Min(current, Combat.Speed);
            if (limit != value)
            {
                CompressionBlockers.NoteAircraftAttack(current, limit);
                value = limit;
                _changed = true;
            }
        }
        catch (Exception e)
        {
            Plugin.LogOnce("tc-setter", e);
        }
    }

    private static void Postfix()
    {
        if (!_changed) return;
        _changed = false;
        try
        {
            CompressionBlockers.SyncLadderIndex();
        }
        catch (Exception e)
        {
            Plugin.LogOnce("tc-setter-sync", e);
        }
    }

    private static bool CalledFromAttackRun()
    {
        var trace = new System.Diagnostics.StackTrace(2, false);
        int frames = Math.Min(trace.FrameCount, 8);
        for (int i = 0; i < frames; i++)
        {
            Type t = trace.GetFrame(i)?.GetMethod()?.DeclaringType;
            if (t == null) continue;
            string name = t.FullName ?? "";
            if (name.Contains("EngageSurfaceContact")) return true;
        }
        return false;
    }
}

/// <summary>
/// Logs EVERY change of the time compression with the call chain that caused it (key press, pause, game clamp, this mod), so that an
/// unexpected speed change can be traced afterwards in the BepInEx log. At most 60 lines per minute.
/// </summary>
internal static class TcTrace
{
    private static double _windowStart;
    private static int _inWindow;

    internal static void Note(float value)
    {
        if (!Plugin.VerboseLog.Value) return;
        float current = GameTime.TimeCompression;
        if (Mathf.Abs(value - current) < 0.001f) return;
        double now = Time.unscaledTime;
        if (now - _windowStart > 60.0) { _windowStart = now; _inWindow = 0; }
        if (++_inWindow > 60) return;

        var trace = new System.Diagnostics.StackTrace(2, false);
        var chain = new List<string>();
        for (int i = 0; i < trace.FrameCount && chain.Count < 5; i++)
        {
            var m = trace.GetFrame(i)?.GetMethod();
            Type t = m?.DeclaringType;
            if (t == null || t.Namespace == "HarmonyLib" || t.Namespace == "System.Reflection" || t.Namespace == "MonoMod.RuntimeDetour") continue;
            if (m.Name == "set_TimeCompression" || m.Name.StartsWith("DMD<")) continue;
            chain.Add(t.Name + "." + m.Name);
        }
        Plugin.Log.LogInfo($"[tc] {current:0.##}x -> {value:0.##}x  by {string.Join(" < ", chain)}");
    }
}

/// <summary>
/// Who is asking the game to slow time down. The game's own clamps (guns, CIWS, aircraft attack runs) are plain calls buried in long methods,
/// so the enclosing methods are wrapped: Owner is the object whose gun / aircraft is running while the clamp may fire. With OnlySeen the
/// SetTimeCompression Prefix and the attack-run setter Prefix drop the clamp when that object is not known to the player.
/// </summary>
internal static class ClampContext
{
    internal static ObjectBase Owner;
}

[HarmonyPatch]
internal static class ClampContext_Patch
{
    private static System.Reflection.FieldInfo _levelAttackAircraft;

    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(WeaponSystemGun), "AlignTurret");
        yield return AccessTools.Method(typeof(WeaponSystemCIWS), "AquireTarget");
        yield return AccessTools.Method(typeof(WeaponSystemCIWS), "FireAtTarget");
        Type levelAttack = AccessTools.TypeByName("AircraftStates.EngageSurfaceContact+LevelAttack");
        if (levelAttack != null)
            yield return AccessTools.Method(levelAttack, "onUpdate");
    }

    private static void Prefix(object __instance, out ObjectBase __state)
    {
        __state = ClampContext.Owner;
        try
        {
            if (__instance is WeaponSystem ws)
            {
                ClampContext.Owner = ws._baseObject;
            }
            else if (__instance != null)
            {
                _levelAttackAircraft ??= AccessTools.Field(__instance.GetType(), "_aircraft");
                ClampContext.Owner = _levelAttackAircraft?.GetValue(__instance) as ObjectBase;
            }
        }
        catch (Exception e)
        {
            ClampContext.Owner = null;
            Plugin.LogOnce("clamp-context", e);
        }
    }

    private static void Finalizer(ObjectBase __state)
    {
        ClampContext.Owner = __state;
    }
}
