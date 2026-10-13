using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace MainMod;

/// <summary>
/// Laying sonobuoys at high time compression.
///
/// A buoy is a "bomb" for the game: the aircraft flies to the drop waypoint (AttackAtWaypoint), the waypoint creates an engage task and the
/// bomb hardpoint then waits until the aircraft is inside the buoy's kinematic range, within 5 degrees of straight at the spot and almost
/// wings level (WeaponSystemHardpoint.CheckMaxRange / CheckFiringConstraints / CheckRange). The aircraft turns by physics steps that grow with
/// the time compression, so above ~20x it often cannot line up and just circles the spot for ever, never dropping.
///
/// Above SonobuoyDropAbove the buoy is simply released on the spot: once the aircraft is within SonobuoyDropRadiusNm of the drop point the
/// waypoint task fires and the hardpoint releases the buoy at the exact spot (no alignment needed). The buoy still falls on its parachute
/// and deploys like a normal one; only the aircraft's approach geometry is skipped. The game's own "slow down to 20x when a weapon is about to
/// be released" rule does not run for these drops, since the release no longer waits for that moment.
/// </summary>
internal static class Sonobuoys
{
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> Above;
    internal static ConfigEntry<float> RadiusNm;
    internal static ConfigEntry<float> MaxRadiusNm;

    private const float UnityPerNm = 1852f * 0.0148809375f; // 1 Unity unit = 67.2 m

    private static FieldInfo _isEngageTriggered, _isCompleted, _isLaunchingSalvo, _currentShotInSalvo;
    private static MethodInfo _attackCalculations, _onFinished;

    internal static void Bind(ConfigFile cfg)
    {
        Enabled = cfg.Bind("Sonobuoys", "DropOnTheSpot", true,
            "Above SonobuoyDropAbove compression, an aircraft or helicopter that has been ordered to lay a sonobuoy releases it as soon as it is within " +
            "SonobuoyDropRadiusNm of the drop point, at exactly that point, instead of having to line up on it (which fails at high compression: the " +
            "aircraft circles the point and never drops). The buoy still falls on its parachute and deploys normally. false = the game's own behaviour.");
        Above = cfg.Bind("Sonobuoys", "SonobuoyDropAbove", 20f,
            "Compression (x) above which buoys are released on the spot. At or below it the game's own drop logic is used.");
        RadiusNm = cfg.Bind("Sonobuoys", "SonobuoyDropRadiusNm", 1f,
            "How close (nm) the aircraft must be to the drop point before the buoy is released on it.");
        MaxRadiusNm = cfg.Bind("Sonobuoys", "SonobuoyDropMaxRadiusNm", 10f,
            "The faster the compression, the coarser the aircraft's steering, so the release radius grows with it: SonobuoyDropRadiusNm x (compression / " +
            "SonobuoyDropAbove), never more than this many nm. The aircraft always has to get within that distance of the drop point first.");
    }

    internal static bool On => Plugin.Active && Enabled.Value && GameTime.TimeCompression > Above.Value;

    /// <summary>Release radius: grows with the compression (steering gets coarser), capped by SonobuoyDropMaxRadiusNm.</summary>
    private static float RadiusUnity
    {
        get
        {
            float nm = Mathf.Max(0.1f, RadiusNm.Value) * Mathf.Max(1f, GameTime.TimeCompression / Mathf.Max(1f, Above.Value));
            return Mathf.Min(nm, Mathf.Max(RadiusNm.Value, MaxRadiusNm.Value)) * UnityPerNm;
        }
    }

    private static bool Near(Vector3 a, Vector3 b) => FlatDistance(a, b) <= RadiusUnity;

    private static bool IsBuoy(Ammunition a) => a?._ap != null && a._ap._subType == Ammunition.Type.Sonobuoy;

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ------------------------------------------------------------------ waypoint task

    /// <summary>Prefix of AttackAtWaypoint.OnUpdate: true = let the game run its own update.</summary>
    internal static bool WaypointUpdate(AttackAtWaypoint task)
    {
        if (!On || !IsBuoy(task._ammo) || task._baseObject == null || !task._baseObject.IsAirUnit) return true;
        if (GameTime.IsPaused()) return true;

        _isCompleted ??= AccessTools.Field(typeof(AttackAtWaypoint), "_isCompleted");
        _isEngageTriggered ??= AccessTools.Field(typeof(AttackAtWaypoint), "_isEngageTriggered");
        if ((bool)_isCompleted.GetValue(task) || (bool)_isEngageTriggered.GetValue(task)) return true;
        if (task._hasTargetObject && task._targetObject == null) return true; // the game ends the task itself

        if (!Near(task._baseObject.transform.position, task._waypointGeoPos.value.ToUnity())) return true;

        _attackCalculations ??= AccessTools.Method(typeof(AttackAtWaypoint), "AttackCalculations");
        _onFinished ??= AccessTools.Method(typeof(GoToWaypointTask), "OnFinished");

        // the same sequence the game runs when its own trigger range is reached
        _isEngageTriggered.SetValue(task, true);
        _attackCalculations.Invoke(task, null);
        _onFinished.Invoke(task, null);
        task.EndAction(success: true);
        _isCompleted.SetValue(task, true);
        if (Plugin.VerboseLog.Value)
            Plugin.Log.LogInfo($"Sonobuoy: {task._baseObject.getUIDAndName()} drop point reached at {GameTime.TimeCompression:0}x, engage task created.");
        return false;
    }

    // ------------------------------------------------------------------ hardpoint

    /// <summary>Prefix of WeaponSystemHardpoint.OnUpdate: true = let the game run its own update.</summary>
    internal static bool HardpointUpdate(WeaponSystemHardpoint hp)
    {
        if (!On || !hp._executingEngageTask || !IsBuoy(hp._ammoForEngage)) return true;
        ObjectBase carrier = hp._baseObject;
        if (carrier == null || !carrier.IsAirUnit || hp.Inoperable.Value || hp._isOffline) return true;
        if (hp._targetObject != null || hp._weapons.Count == 0) return true; // buoys are dropped on a position
        if (hp.getLoadedAmmoCount(hp._ammoForEngage._ap._ammunitionFileName) <= 0) return true;

        Vector3 target = hp._targetPosition;
        if (!Near(carrier.transform.position, target)) return true;

        string ammo = hp._ammoForEngage._ap._ammunitionFileName;
        WeaponBase buoy = hp._weapons.FirstOrDefault(w => w._ap._ammunitionFileName == ammo);
        if (buoy == null) return true;

        Vector3 platformVelocity = carrier.getVelocityInUnity() * carrier.getRootGameObject().transform.forward;
        hp.launch(null, new Vector3(target.x, Mathf.Max(target.y, 0f), target.z), Vector3.zero, platformVelocity);
        if (!buoy.isLaunched()) return true;

        // put it straight above the spot and let it fall vertically
        Vector3 p = buoy.transform.position;
        p.x = target.x;
        p.z = target.z;
        buoy.transform.position = p;
        buoy._velocityVecInUnity = new Vector3(0f, buoy._velocityVecInUnity.y, 0f);
        buoy.setGeoPosition(Utils.worldPositionFromUnityToLongLat(p, Globals._currentCenterTile));

        // close the engage task the way WeaponSystemHardpoint.ExecuteLaunch does after the last shot
        hp.EndEngageTask(true);
        hp._offsetCalculated = false;
        _isLaunchingSalvo ??= AccessTools.Field(typeof(WeaponSystemHardpoint), "_isLaunchingSalvo");
        _currentShotInSalvo ??= AccessTools.Field(typeof(WeaponSystemHardpoint), "_currentShotInSalvo");
        _isLaunchingSalvo.SetValue(hp, false);
        _currentShotInSalvo.SetValue(hp, 0);
        hp._lastLaunchTime = GameTime.missionElapsedTime;

        if (Plugin.VerboseLog.Value)
            Plugin.Log.LogInfo($"Sonobuoy: {carrier.getUIDAndName()} released {buoy.getUIDAndName()} on the spot at {GameTime.TimeCompression:0}x.");
        return false;
    }
}

[HarmonyPatch(typeof(AttackAtWaypoint), "OnUpdate")]
internal static class AttackAtWaypoint_Sonobuoy_Patch
{
    private static bool Prefix(AttackAtWaypoint __instance)
    {
        try
        {
            return Sonobuoys.WaypointUpdate(__instance);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("sonobuoy-waypoint", e);
            return true; // never block the game's own logic because of a bug here
        }
    }
}

[HarmonyPatch(typeof(WeaponSystemHardpoint), nameof(WeaponSystemHardpoint.OnUpdate))]
internal static class Hardpoint_Sonobuoy_Patch
{
    private static bool Prefix(WeaponSystemHardpoint __instance)
    {
        try
        {
            return Sonobuoys.HardpointUpdate(__instance);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("sonobuoy-hardpoint", e);
            return true;
        }
    }
}
