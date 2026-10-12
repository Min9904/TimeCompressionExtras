using System;
using HarmonyLib;
using SeaPower;

namespace MainMod;

/// <summary>
/// Per-frame hook on every flight deck: BeforeUpdate decides which air-ops speed limit the game will apply this frame, AfterUpdate
/// converts the launches / landings that are ready (see AirOps.cs).
/// </summary>
[HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.OnUpdate))]
internal static class FlightDeck_OnUpdate_Patch
{
    private static void Prefix(FlightDeck __instance)
    {
        try
        {
            AirOps.BeforeUpdate(__instance);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("flightdeck-prefix", e);
        }
    }

    private static void Postfix(FlightDeck __instance)
    {
        try
        {
            AirOps.AfterUpdate(__instance);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("flightdeck-postfix", e);
        }
    }
}

/// <summary>
/// Spawn.onUpdate marks the Spawn state finished after ~1 s of mission time, i.e. within one frame at high compression, long
/// before the asynchronously loaded aircraft is active. While instant launch applies the state is kept open (see InstantLaunch).
/// </summary>
[HarmonyPatch(typeof(Spawn), nameof(Spawn.onUpdate))]
internal static class Spawn_OnUpdate_Patch
{
    // Spawn._flightDeckTask is private. Read it with plain reflection instead of Harmony's ___field injection:
    // a wrong injection name makes Harmony refuse the whole patch (that is exactly what broke version 0.2.0).
    private static System.Reflection.FieldInfo _taskField;

    private static void Postfix(Spawn __instance)
    {
        try
        {
            if (!__instance._isFinished) return;
            _taskField ??= AccessTools.Field(typeof(Spawn), "_flightDeckTask");
            var task = _taskField.GetValue(__instance) as FlightDeckTask;
            if (InstantLaunch.ShouldHoldSpawn(task))
                __instance._isFinished = false;
        }
        catch (Exception e)
        {
            Plugin.LogOnce("spawn-hold", e);
        }
    }
}
