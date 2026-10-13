using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace MainMod;

/// <summary>
/// Helicopters dipping their sonar at high time compression.
///
/// A helicopter flies by physics (cyclic, torque, lift and a hover-stabilising controller) in steps that grow with the time compression.
/// Above ~20x that controller no longer settles: the helicopter overshoots the dip point, turns back, overshoots again and circles it
/// without ever coming within the waypoint tolerance - or, if it does arrive, its speed keeps drifting out of the window the sonar needs
/// and the cable is pulled up again.
///
/// The game has a second, kinematic flight model for helicopters (it moves straight towards the order with a speed that falls to nothing
/// at the last waypoint; the game switches to it above 999x, field Helicopter._timeCompressionLimt). While a helicopter has a dip order
/// (current waypoint deploys the towed sonar, or the dip window of the order is still running, or the sonar is on) and the compression is
/// above DipSimpleAbove, that helicopter alone uses the kinematic model, so it flies to the point and holds still. Everything else keeps the
/// normal model. On top of that a dip waypoint counts as reached once the helicopter is within DipRadiusNm of it.
/// </summary>
internal static class Dipping
{
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> Above;
    internal static ConfigEntry<float> RadiusNm;

    private const float UnityPerNm = 1852f * 0.0148809375f; // 1 Unity unit = 67.2 m
    private const float GameDefaultLimit = 999f;

    // IsDipping walks the helicopter's order list, so it is asked at most every few frames per helicopter instead of every physics step
    private sealed class Memo { public int Frame = int.MinValue; public bool Dipping; }
    private static readonly ConditionalWeakTable<Helicopter, Memo> Memos = new ConditionalWeakTable<Helicopter, Memo>();
    private const int RecheckFrames = 20;

    private static FieldInfo _isFinished;
    private static MethodInfo _onFinished;

    internal static void Bind(ConfigFile cfg)
    {
        Enabled = cfg.Bind("Dipping", "DipHelicopters", true,
            "Above DipSimpleAbove compression, a helicopter with a dipping-sonar order flies with the game's simple movement model (straight to the point, " +
            "then holds still) instead of its physics model, which circles the dip point forever at high compression. Only that helicopter is affected, " +
            "only while it has a dip order or its sonar is on. false = the game's own behaviour.");
        Above = cfg.Bind("Dipping", "DipSimpleAbove", 20f,
            "Compression (x) above which a dipping helicopter uses the simple movement model.");
        RadiusNm = cfg.Bind("Dipping", "DipRadiusNm", 0.3f,
            "A dip waypoint counts as reached when the helicopter is this close (nm), above DipSimpleAbove. 0 = only the game's own tolerance.");
    }

    private static bool On => Plugin.Active && Enabled.Value;

    /// <summary>The helicopter has been told to dip, is waiting out the dip window, or has its dipping sonar switched on.</summary>
    internal static bool IsDipping(Helicopter h)
    {
        if (h.CurrentWaypointDeploysTowedSonar()) return true;
        if (h._ai != null && h._ai._turnOffTowedSonarsTime > GameTime.missionElapsedTime) return true;
        var sonars = h._obp?._towedSonarSystems;
        if (sonars != null)
        {
            foreach (SensorSystemSonar s in sonars)
                if (s != null && s._isDippingSonar && s.IsOn.Value) return true;
        }
        return false;
    }

    private static bool IsDippingCached(Helicopter h)
    {
        Memo m = Memos.GetValue(h, _ => new Memo());
        int frame = Time.frameCount;
        if (frame - m.Frame >= RecheckFrames || frame < m.Frame)
        {
            m.Frame = frame;
            m.Dipping = IsDipping(h);
        }
        return m.Dipping;
    }

    /// <summary>Prefix of Helicopter.OnFixedUpdate: pick the movement model for this step.</summary>
    internal static void Select(Helicopter h)
    {
        if (!On) return;
        // the value a helicopter has when it is not dipping: the game's, or the one Performance.SimplePhysicsAbove gave it
        float baseline = Performance.SimplePhysicsAbove != null && Performance.SimplePhysicsAbove.Value > 0f
            ? Performance.SimplePhysicsAbove.Value
            : GameDefaultLimit;
        float want = baseline;
        if (GameTime.TimeCompression > Above.Value && IsDippingCached(h))
            want = Mathf.Min(baseline, Above.Value);
        if (h._timeCompressionLimt != want)
        {
            h._timeCompressionLimt = want;
            if (Plugin.VerboseLog.Value)
                Plugin.Log.LogInfo($"Dipping: {h.getUIDAndName()} now uses the {(want < baseline ? "simple" : "normal")} movement model ({GameTime.TimeCompression:0}x).");
        }
    }

    /// <summary>Postfix of GoToWaypointTask.OnUpdate: finish a dip waypoint the helicopter is close enough to.</summary>
    internal static void AfterWaypointUpdate(GoToWaypointTask task)
    {
        if (!On || RadiusNm.Value <= 0f || GameTime.TimeCompression <= Above.Value) return;
        if (!(task._baseObject is Helicopter h) || h.Aligning) return;
        if (!task._setTowedSonars.setSonars || !task._setTowedSonars.enable) return;
        if (GameTime.IsPaused()) return;

        _isFinished ??= AccessTools.Field(typeof(GoToWaypointTask), "_isFinished");
        if ((bool)_isFinished.GetValue(task)) return;

        Vector3 here = h.transform.position;
        Vector3 there = task._waypointGeoPos.value.ToUnity();
        float dx = here.x - there.x, dz = here.z - there.z;
        if (Mathf.Sqrt(dx * dx + dz * dz) > RadiusNm.Value * UnityPerNm) return;

        _onFinished ??= AccessTools.Method(typeof(GoToWaypointTask), "OnFinished");
        _isFinished.SetValue(task, true);
        _onFinished.Invoke(task, null);
        if (Plugin.VerboseLog.Value)
            Plugin.Log.LogInfo($"Dipping: {h.getUIDAndName()} reached the dip point at {GameTime.TimeCompression:0}x.");
    }
}

[HarmonyPatch(typeof(Helicopter), nameof(Helicopter.OnFixedUpdate))]
internal static class Helicopter_Dipping_Patch
{
    private static void Prefix(Helicopter __instance)
    {
        try
        {
            Dipping.Select(__instance);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("dipping-model", e);
        }
    }
}

[HarmonyPatch(typeof(GoToWaypointTask), "OnUpdate")]
internal static class GoToWaypoint_Dipping_Patch
{
    private static void Postfix(GoToWaypointTask __instance)
    {
        try
        {
            Dipping.AfterWaypointUpdate(__instance);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("dipping-waypoint", e);
        }
    }
}
