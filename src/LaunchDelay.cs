using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeaPower;

namespace MainMod;

/// <summary>
/// Spacing between aircraft.
///
/// A real deck spaces launches out physically (elevator cycles, taxi, catapult cycle). Skipping the deck sequence removes all of
/// that, so a whole air wing would appear in one instant. This puts a gap back: every aircraft - from any order, any squadron, on
/// any deck, the player's or the enemy's - is held back until LaunchDelaySeconds of GAME time (the time that would pass at normal
/// speed, so it shrinks with time compression) have passed since the previous aircraft left the same deck.
///
/// Implemented as a gate on the game's own spawn step (HandlePendingLaunchTask.onUpdate): while an aircraft is held, the step simply
/// does not run; nothing else about the order changes (ready-up, deck space and ground crew are untouched).
/// </summary>
internal static class LaunchDelay
{
    private static readonly Dictionary<FlightDeck, double> _lastLaunch = new Dictionary<FlightDeck, double>();
    private static readonly HashSet<(PendingLaunchTask, int)> _announced = new HashSet<(PendingLaunchTask, int)>();
    private static readonly Dictionary<FlightDeck, double> _lastConverted = new Dictionary<FlightDeck, double>();
    private static readonly HashSet<LaunchTask> _announcedConversion = new HashSet<LaunchTask>();
    private static FieldInfo _taskField;

    internal static void Reset()
    {
        _lastLaunch.Clear();
        _announced.Clear();
        _lastConverted.Clear();
        _announcedConversion.Clear();
    }

    /// <summary>
    /// Instant launch is about to take this launch (already spawned and queued, e.g. taxiing to the launch point) into the air:
    /// true = not yet, the previous aircraft of this deck left less than LaunchDelaySeconds of game time ago. Without this gate
    /// everything that is queued on the deck when the speed passes InstantLaunchAbove would leave in the same frame (the spawn
    /// gate above only spaces out the spawning of NEW aircraft).
    /// </summary>
    internal static bool HoldConversion(FlightDeck fd, LaunchTask task)
    {
        float delay = Plugin.LaunchDelaySeconds.Value;
        if (!Plugin.Active || delay <= 0f) return false;
        ObjectBase deck = fd?._baseObject;
        if (deck == null) return false;
        if (!Plugin.LaunchDelayAiDecks.Value && !deck.IsPlayerObject) return false;
        if (task._ltp != null && task._ltp._fromScript) return false;
        // already on the catapult / at the end of the takeoff run: its own physics must not be left running at high compression
        IState state = task._stateMachine?.CurrentState;
        if (state is PlaneTakeOff || state is STOLTakeOff || state is VTOLTakeOff) return false;
        if (!_lastConverted.TryGetValue(fd, out double last)) return false;
        double remaining = delay - (GameTime.missionElapsedTime - last);
        if (remaining <= 0.0) return false;
        if (Plugin.VerboseLog.Value && _announcedConversion.Add(task))
            Plugin.Log.LogInfo($"Launch delay: {task.Vehicle?.getUIDAndName()} on {deck.getUIDAndName()} stays queued for {remaining:0} s of game time.");
        return true;
    }

    /// <summary>An aircraft of this deck was just taken into the air by instant launch.</summary>
    internal static void NoteConverted(FlightDeck fd)
    {
        double now = GameTime.missionElapsedTime;
        _lastConverted[fd] = now;
        _lastLaunch[fd] = now;
    }

    /// <summary>Postfix of FlightDeck.launchVehicle: an aircraft just left this deck.</summary>
    internal static void NoteLaunch(FlightDeck fd)
    {
        _lastLaunch[fd] = GameTime.missionElapsedTime;
    }

    /// <summary>Prefix of HandlePendingLaunchTask.onUpdate: true = skip this frame's spawn attempt.</summary>
    internal static bool ShouldHold(HandlePendingLaunchTask state)
    {
        float delay = Plugin.LaunchDelaySeconds.Value;
        if (!Plugin.Active || delay <= 0f) return false;
        if (Plugin.LaunchDelayOnlyWhileSkipping.Value && !AirOps.WantedLaunch) return false;

        _taskField ??= AccessTools.Field(typeof(HandlePendingLaunchTask), "_launchTask");
        if (!(_taskField.GetValue(state) is PendingLaunchTask order)) return false;
        if (order.LaunchCount < 1) return false;

        FlightDeck fd = order._flightDeck;
        ObjectBase deck = fd?._baseObject;
        if (deck == null) return false;
        if (!Plugin.LaunchDelayAiDecks.Value && !deck.IsPlayerObject) return false;
        if (order._ltp != null && order._ltp._fromScript) return false; // mission scripts keep their own timing

        if (!_lastLaunch.TryGetValue(fd, out double last)) return false; // first launch from this deck
        double remaining = delay - (GameTime.missionElapsedTime - last);
        if (remaining <= 0.0) return false;

        if (Plugin.VerboseLog.Value && _announced.Add((order, order.LaunchCount)))
        {
            Plugin.Log.LogInfo($"Launch delay: next aircraft of {order.Squadron} ({order.AircraftType}) from {deck.getUIDAndName()} " +
                               $"waits {remaining:0} s of game time.");
        }
        return true;
    }
}

[HarmonyPatch(typeof(HandlePendingLaunchTask), nameof(HandlePendingLaunchTask.onUpdate))]
internal static class HandlePendingLaunch_OnUpdate_Patch
{
    private static bool Prefix(HandlePendingLaunchTask __instance)
    {
        try
        {
            return !LaunchDelay.ShouldHold(__instance);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("launch-delay", e);
            return true; // never block launches because of a bug here
        }
    }
}

[HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.launchVehicle))]
internal static class FlightDeck_LaunchVehicle_Patch
{
    private static void Postfix(FlightDeck __instance)
    {
        try
        {
            LaunchDelay.NoteLaunch(__instance);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("launch-delay-note", e);
        }
    }
}
