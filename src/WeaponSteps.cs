using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace MainMod;

/// <summary>
/// Weapons whose speed depends on the step length of the PREVIOUS step.
///
/// ASROC / SS-N-16 style rounds (class ASROC) and RBU rockets fly a Verlet-like ballistic arc: every step they take the distance moved since the
/// last step and divide it by the CURRENT GameTime.fixedDeltaTime to get the speed (ASROC.performBallisticFlight, RBU TerminalApproach). The step
/// length is stretched by the time compression (and by the adaptive step), so when the compression drops (50x -> 20x) the distance covered
/// at the old, long step is divided by the new, short one and the round suddenly flies 2-3x faster - the "jolt". StepRescale rewrites _lastPos
/// before the step so that the displacement is expressed in the new step length; at a constant step it changes nothing.
/// </summary>
internal static class StepRescale
{
    private static readonly ConditionalWeakTable<WeaponBase, StrongBox<float>> Previous = new ConditionalWeakTable<WeaponBase, StrongBox<float>>();

    /// <summary>Call at the start of OnFixedUpdate of the weapon; <paramref name="derivesSpeedFromMove"/> = it is in the stage that does it.</summary>
    internal static void Apply(WeaponBase w, bool derivesSpeedFromMove)
    {
        float dt = GameTime.fixedDeltaTime;
        StrongBox<float> box = Previous.GetValue(w, _ => new StrongBox<float>(dt));
        float prev = box.Value;
        box.Value = dt;
        if (!Plugin.Active || !derivesSpeedFromMove || !(prev > 0f) || !(dt > 0f)) return;
        if (Mathf.Abs(dt / prev - 1f) < 0.001f) return;
        Vector3 p = w.transform.position;
        w._lastPos = p - (p - w._lastPos) * (dt / prev);
    }
}

[HarmonyPatch(typeof(ASROC), nameof(ASROC.OnFixedUpdate))]
internal static class ASROC_StepRescale_Patch
{
    private static void Prefix(ASROC __instance)
    {
        try
        {
            WeaponBase.FlightStage s = __instance._flightStage;
            StepRescale.Apply(__instance, s == WeaponBase.FlightStage.ASROCFlight || s == WeaponBase.FlightStage.ASROCSeparation);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("asroc-step", e);
        }
    }
}

[HarmonyPatch(typeof(RBU), nameof(RBU.OnFixedUpdate))]
internal static class RBU_StepRescale_Patch
{
    private static void Prefix(RBU __instance)
    {
        try
        {
            StepRescale.Apply(__instance, __instance._flightStage == WeaponBase.FlightStage.TerminalApproach);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("rbu-step", e);
        }
    }
}

/// <summary>
/// Bomb.OnFixedUpdate steers an unguided bomb onto its aim point with  v += miss * dt / max(timeToImpact, 0.1)  once per step, an undamped
/// lateral acceleration. The step dt is stretched by the time compression, so at high compression (or with the adaptive step) the loop
/// gain dt/tti exceeds the stability limit, the lateral speed grows every step and stays on the bomb when the compression drops again.
/// The transpiler limits the dt used in that one term to what the game itself uses at 20x (1/30 s of step x 2 = 0.067 s).
/// </summary>
[HarmonyPatch(typeof(Bomb), nameof(Bomb.OnFixedUpdate))]
internal static class Bomb_Steering_Patch
{
    private const float MaxSteeringStep = 1f / 15f;

    internal static float SteeringDt()
    {
        float dt = GameTime.fixedDeltaTime;
        return Plugin.Active ? Math.Min(dt, MaxSteeringStep) : dt;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo getDt = AccessTools.PropertyGetter(typeof(GameTime), nameof(GameTime.fixedDeltaTime));
        MethodInfo steer = AccessTools.Method(typeof(Bomb_Steering_Patch), nameof(SteeringDt));
        bool seenImpact = false, done = false;
        int replaced = 0;
        foreach (CodeInstruction ins in instructions)
        {
            if (!done && ins.operand is MethodInfo mi && mi.Name == "GetEstimatedBombImpactPoint") seenImpact = true;
            if (seenImpact && !done && ins.Calls(getDt))
            {
                done = true;
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, steer);
                continue;
            }
            yield return ins;
        }
        Plugin.Log.LogInfo($"Bomb steering: limited the step in {replaced} x 'miss * dt / tti' (expected 1).");
    }
}
