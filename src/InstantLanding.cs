using System;
using System.Collections.Generic;
using System.Linq;
using SeaPower;

namespace MainMod;

/// <summary>
/// Landing fix for high time compression.
///
/// A recovery is a RecoveryTask state machine: Approach -> Rollout (touchdown, arrested stop) -> ClearRunway -> AlignAtElevator
/// -> EmbarkElevator -> LowerElevator -> OpenHangarDoors -> DisembarkElevator -> (extend hangar) -> CloseHangarDoors ->
/// RaiseElevator -> DeSpawn -> Finish. Everything from the touchdown to the elevator is physics (arrestor-wire deceleration,
/// GroundTaxiPhysics) integrated with GameTime.fixedDeltaTime, which is huge at high compression.
///
/// While compression is above InstantLandingAbove the whole sequence after the circuit is skipped and the aircraft goes
/// straight to the game's own DeSpawn state. DeSpawn is exactly what ends every normal landing, so the bookkeeping is the
/// game's:
///   * the aircraft's remaining ammunition goes back into the deck stores,
///   * its campaign tag is put back on the deck's on-board list (FlightDeck.RegisterCampaignUnitOnBoard) - that is how the
///     returned unit stays tied to its original campaign unit,
///   * a CoolDownTask is queued which, when it ends, adds the aircraft back to the hangar inventory (AddVehicle) and frees its
///     serial number,
///   * the aircraft object is removed with destroyObject(score: false, LeftArea): NO loss is recorded (setDestroyedFlag only
///     books _lossScore / RegisterUnitStatus(destroyed) when score is true) and, because _spawnTime is set to MaxValue, no
///     pilot ejects.
/// </summary>
internal static class InstantLanding
{
    /// <summary>The landing is in a state we can complete, and its aircraft can take part.</summary>
    internal static bool CanConvert(RecoveryTask rt)
    {
        ObjectBase v = rt.Vehicle;
        if (v == null || v.IsDestroyed || rt._elevator == null) return false;
        IState s = rt._stateMachine?.CurrentState;
        if (s == null || s is DeSpawn || s is FinishFlightDeckTask) return false;
        return v is Aircraft || v is Helicopter;
    }

    private static bool AmmunitionReadable(ObjectBase v)
    {
        try
        {
            foreach (KeyValuePair<string, int> item in v.AmmunitionAmountDictionary)
            {
                if (Utils.openIniFile("/ammunition", "/" + item.Key + ".ini", logErrors: false) == null)
                    return false;
            }
        }
        catch
        {
            return false;
        }
        return true;
    }

    internal static void Land(RecoveryTask task)
    {
        FlightDeck fd = task._flightDeck;
        ObjectBase v = task.Vehicle;
        Elevator elevator = task._elevator;
        RecoveryPoint recoveryPoint = task._recoveryPoint;
        IState state = task._stateMachine.CurrentState;
        string was = state.GetType().Name;
        string vehicleName = v.getUIDAndName();
        string deckName = fd._baseObject.getUIDAndName();
        string campaignTag = v._campaignTag;
        float tc = GameTime.TimeCompression;

        // DeSpawn.onEnter gives up half-way (without finishing, without removing the aircraft, without putting its campaign tag back)
        // if it cannot read an ammunition definition. In that case leave the landing to the game rather than strand the aircraft in
        // a dead state; the task is marked so this check is not repeated every frame.
        if (!AmmunitionReadable(v))
        {
            AirOps.Failed.Add(task);
            Plugin.Log.LogWarning($"Instant landing skipped for {vehicleName}: an ammunition definition cannot be read, so the game's own landing sequence is left to run.");
            return;
        }

        // 1. Leave the current state through its own onExit and enter the game's DeSpawn (see the class comment for what it does).
        var despawn = new DeSpawn(task);
        task._stateMachine.setState(despawn);
        if (!despawn._isFinished)
        {
            // Cannot happen after the ammunition check unless the game's DeSpawn changed. Say so; the task is still closed below
            // (DeSpawn has no way out in this state machine, so it would otherwise linger and keep the elevator marked busy).
            Plugin.Log.LogError($"Instant landing of {vehicleName}: the game's DeSpawn did not complete (aircraft may still be in the air).");
        }

        // 2. DeSpawn has no outgoing transition in this state machine (the wired one belongs to the original DeSpawn object),
        //    so finish the task ourselves, exactly as the wired transition would.
        task._stateMachine.setState(new FinishFlightDeckTask(task, isRecoveryTask: true));

        // 3. The sequence we skipped would have released the runway and left the elevator at rest.
        if (recoveryPoint != null)
        {
            recoveryPoint.ReleaseObject(v);
            recoveryPoint.Release(v);
            recoveryPoint.CleanUp();
        }
        elevator.ReleaseObject(v);
        if (elevator._incomingObjects.Count == 0)
            AirOps.RestoreElevator(elevator);

        if (Plugin.VerboseLog.Value)
        {
            // Campaign integrity, visible in the log: a tagged unit must be back on the deck's list and must not be a loss.
            string campaign = string.IsNullOrEmpty(campaignTag)
                ? "no campaign tag"
                : $"campaign tag '{campaignTag}' back on the deck's on-board list: {fd._campaignUnitsOnBoard.Any(c => c != null && string.Equals(c.CampaignTag, campaignTag, StringComparison.OrdinalIgnoreCase))}";
            Plugin.Log.LogInfo($"Instant landing: {vehicleName} on {deckName} at {tc:0}x, was {was} ({campaign}; removed with score=false, not counted as a loss).");
        }
    }
}
