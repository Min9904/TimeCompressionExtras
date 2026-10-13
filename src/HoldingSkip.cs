using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace MainMod;

/// <summary>
/// Returning aircraft: no holding pattern and no landing circuit at high time compression.
///
/// A ReturnTask is: ReturnToBase (fly home) -> HoldingPattern -> Circuit -> (RecoveryTask = the landing, which InstantLanding already skips).
/// In the holding pattern an aircraft queues behind the others and flies the pattern's waypoints in laps; only the first in the queue may ask
/// for a recovery point, and only when it is on the last waypoint of a lap (HoldingPattern.onFixedUpdate: PatternIndex == 0 and fewer than
/// two waypoints left). Then the Circuit flies one more set of waypoints. All of that is ordinary flight, which crawls at high compression,
/// and the aircraft land one after the other, so a returning air wing takes for ever.
///
/// While InstantLanding applies (above InstantLandingAbove) and SkipHoldingPattern is on:
///   * the holding pattern asks for a recovery point at once (the game's own TryGetRecoveryPoint, so landing intervals, runway / elevator
///     choice, sea-state and formation rules are unchanged), from any place in the queue and without finishing a lap; an aircraft that is
///     still above the pattern's altitude is brought down to it first;
///   * the circuit counts as finished at once, so the landing task is created straight away and InstantLanding completes it.
/// Nothing else about the landing changes: the aircraft is recovered through the game's own DeSpawn, as before.
/// </summary>
internal static class HoldingSkip
{
    internal static ConfigEntry<bool> Enabled;

    private static FieldInfo _task, _deck, _obj, _maxAlt;
    private static MethodInfo _tryGetRecoveryPoint, _setFinished, _repeatHold;

    internal static void Bind(ConfigFile cfg)
    {
        Enabled = cfg.Bind("AirOps", "SkipHoldingPattern", true,
            "While instant landings apply (above InstantLandingAbove), returning aircraft do not queue in the holding pattern or fly the landing circuit: " +
            "they ask for a landing spot at once and are handed to the instant landing. The game's landing intervals and runway / elevator choice " +
            "still apply. false = they fly the holding pattern and circuit like normal (very slow at high compression).");
    }

    internal static bool Applies => Plugin.Active && Enabled.Value && AirOps.WantedLanding;

    /// <summary>Prefix of HoldingPattern.onFixedUpdate: true = let the game run its own update.</summary>
    internal static bool HoldingUpdate(HoldingPattern hp)
    {
        if (!Applies) return true;

        _task ??= AccessTools.Field(typeof(HoldingPattern), "_flightDeckTask");
        _deck ??= AccessTools.Field(typeof(HoldingPattern), "_flightDeck");
        _obj ??= AccessTools.Field(typeof(HoldingPattern), "_objectToReturn");
        _maxAlt ??= AccessTools.Field(typeof(HoldingPattern), "_maxCircuitAlt");
        _tryGetRecoveryPoint ??= AccessTools.Method(typeof(HoldingPattern), "TryGetRecoveryPoint");
        _setFinished ??= AccessTools.PropertySetter(typeof(HoldingPattern), "IsFinished");
        _repeatHold ??= AccessTools.Method(typeof(HoldingPattern), "RepeatHoldPattern");

        var task = (FlightDeckTask)_task.GetValue(hp);
        var deck = (FlightDeck)_deck.GetValue(hp);
        var obj = (ObjectBase)_obj.GetValue(hp);
        if (task == null || deck == null || obj == null) return true;

        // the game cancels the return when the order changed or the unit is busy aligning: leave that to it
        if (obj.CurrentOrder.Value.OrderType != Order.Type.ReturnToBase || obj.Aligning) return true;

        // like the game: an aircraft that has flown its last hold waypoint while it waits (landing interval, no free elevator) keeps circling
        if (obj.GetNumberOfWaypoints() < 1) _repeatHold.Invoke(hp, null);

        if (task._recoveryPoint == null || task._elevator == null)
        {
            // bring an aircraft that is still above the pattern down to it (TryGetRecoveryPoint waits for that otherwise)
            float maxAlt = (float)_maxAlt.GetValue(hp);
            float limit = maxAlt + deck._baseObject.transform.position.y + 2.5f;
            Vector3 p = obj.transform.position;
            if (maxAlt > -999f && p.y > limit)
            {
                p.y = limit;
                obj.transform.position = p;
            }

            if (obj.Formation != null && obj.Formation.Stations.Count(x => x.UnitObject != null) > 1)
            {
                // same wingman rule as the game: a wingman that is still in the formation goes first
                bool leaderOnly = true;
                for (int i = obj.Formation.Stations.Count - 1; i >= 0; i--)
                {
                    Station s = obj.Formation.Stations[i];
                    if (s != obj.Formation.LeaderStation && s.UnitObject != null && !s.UnitObject.Aligning && !s.UnitObject.ActsIndependentlyInFormation.Value)
                    {
                        _tryGetRecoveryPoint.Invoke(hp, new object[] { s.UnitObject });
                        leaderOnly = false;
                        break;
                    }
                }
                if (leaderOnly) _tryGetRecoveryPoint.Invoke(hp, new object[] { obj });
            }
            else
            {
                _tryGetRecoveryPoint.Invoke(hp, new object[] { obj });
            }
        }

        bool done = task._recoveryPoint != null && task._elevator != null;
        _setFinished.Invoke(hp, new object[] { done });
        if (done && Plugin.VerboseLog.Value)
            Plugin.Log.LogInfo($"Holding pattern skipped: {obj.getUIDAndName()} got a landing spot at {GameTime.TimeCompression:0}x.");
        return false;
    }
}

[HarmonyPatch(typeof(HoldingPattern), nameof(HoldingPattern.onFixedUpdate))]
internal static class HoldingPattern_Skip_Patch
{
    private static bool Prefix(HoldingPattern __instance)
    {
        try
        {
            return HoldingSkip.HoldingUpdate(__instance);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("holding-skip", e);
            return true; // never block the game's own logic because of a bug here
        }
    }
}

/// <summary>The landing circuit counts as flown at once; its onExit then creates the landing task the game's way.</summary>
[HarmonyPatch(typeof(Circuit), nameof(Circuit.IsFinished), MethodType.Getter)]
internal static class Circuit_Skip_Patch
{
    private static void Postfix(ref bool __result)
    {
        try
        {
            if (!__result && HoldingSkip.Applies) __result = true;
        }
        catch (Exception e)
        {
            Plugin.LogOnce("circuit-skip", e);
        }
    }
}
