using System;
using System.Collections.Generic;
using System.Linq;
using SeaPower;
using UnityEngine;

namespace MainMod;

/// <summary>
/// Launch fix for high time compression.
///
/// A launch is a LaunchTask state machine: Spawn -> elevator/hangar animation states -> EmbarkElevator ->
/// TaxiToLaunchPoint -> OffsetAtLaunchPoint -> PreFlightAnim -> PlaneTakeOff (catapult). The animation states are driven
/// by mission time and survive any compression, but the taxi and takeoff states are PHYSICS (GroundTaxiPhysics,
/// CatapultTakeoffPhysics) integrated with GameTime.fixedDeltaTime, which grows with compression (about 0.8 s per step at
/// 500x): the taxi controllers overshoot their waypoints and the aircraft sits on the deck forever.
///
/// While compression is above InstantLaunchAbove a launch is completed at once, from whatever state it is in:
///   * Spawn (nothing on the elevator touched yet; the state is held open until the asynchronously loaded aircraft is
///     active): leave it through the game's own Spawn.onExit, which is where the mission setup happens (waypoints, formation
///     station, patrol zone, strike package), then put the aircraft into the air.
///   * any state between Spawn and the takeoff (a launch that was already under way, e.g. "mid taxi"): the same, plus the
///     deck equipment is put back to its resting state because the skipped states would have done it.
///   * PlaneTakeOff / STOLTakeOff / VTOLTakeOff: the game's own takeoff exit does the hand-over to the flight model, we only
///     reposition the aircraft ahead of the ship and give it flying speed.
/// </summary>
internal static class InstantLaunch
{
    // Launch points are never marked busy, so aircraft converted in the same frame at the same launch point would appear on
    // top of each other. Space them out along the launch heading (feet).
    private const float StaggerFeet = 260f;
    private static readonly Dictionary<object, (int frame, int count)> _used = new Dictionary<object, (int, int)>();

    internal static void Reset() => _used.Clear();

    // ------------------------------------------------------------------ eligibility

    /// <summary>The launch is in a state we know how to complete (or that finishes by itself), and its aircraft can take part.</summary>
    internal static bool IsHandled(LaunchTask lt)
    {
        ObjectBase v = lt.Vehicle;
        if (v == null || v.IsDestroyed || lt._elevator == null) return false;
        if (!VehicleAllowed(v)) return false;
        IState s = lt._stateMachine?.CurrentState;
        if (s == null) return false;
        if (s is Spawn || s is HelicopterTakeOff) return true; // HelicopterTakeOff has no physics and finishes by itself
        return Plugin.InstantLaunchInProgress.Value;
    }

    private static bool VehicleAllowed(ObjectBase v)
    {
        if (v is Aircraft) return true;
        return v is Helicopter && Plugin.InstantLaunchHelicopters.Value;
    }

    /// <summary>Ready to be converted this frame.</summary>
    internal static bool CanConvert(LaunchTask lt)
    {
        ObjectBase v = lt.Vehicle;
        if (v == null || v.IsDestroyed || lt._elevator == null || !VehicleAllowed(v)) return false;
        IState s = lt._stateMachine?.CurrentState;
        if (s == null || s is FinishFlightDeckTask || s is HelicopterTakeOff) return false;
        if (!(s is Spawn) && !Plugin.InstantLaunchInProgress.Value) return false;
        return IsLoaded(v);
    }

    /// <summary>
    /// Spawn.onUpdate marks the Spawn state finished after ~1 s of mission time, i.e. within one frame at high compression,
    /// long before the asynchronously loaded aircraft is active. While instant launch applies we keep that state open so the
    /// aircraft can be converted once it has loaded.
    /// </summary>
    internal static bool ShouldHoldSpawn(FlightDeckTask task)
    {
        return task is LaunchTask lt
               && AirOps.WantedLaunch && !GameTime.IsPaused()
               && !AirOps.Failed.Contains(lt)
               && lt._stateMachine?.CurrentState is Spawn
               && lt.Vehicle != null && !lt.Vehicle.IsDestroyed && lt._elevator != null
               && VehicleAllowed(lt.Vehicle);
    }

    /// <summary>The aircraft is created with loadAsync: true; wait until the game says it can update.</summary>
    private static bool IsLoaded(ObjectBase v)
    {
        if (!v._canUpdate) return false;
        if (v is Aircraft a) return a.Ap?._rootGameObject != null;
        if (v is Helicopter h) return h._hp?._rootGameObject != null;
        return false;
    }

    /// <summary>True for a flight wingman whose leader is still sitting in its own Spawn state (mission setup not done yet).</summary>
    internal static bool MustWaitForLeader(FlightDeck fd, LaunchTask task)
    {
        ObjectBase v = task.Vehicle;
        if (!v.IsFormationWingman) return false;
        ObjectBase leader = v.Formation?.LeaderStation?.UnitObject;
        if (leader == null || leader == v) return false;
        return IsInSpawn(fd.FlightDeckTasks, leader) || IsInSpawn(fd._flightDeckTasksToAdd, leader);
    }

    private static bool IsInSpawn(IEnumerable<FlightDeckTask> tasks, ObjectBase vehicle)
    {
        foreach (FlightDeckTask t in tasks)
        {
            if (t is LaunchTask lt && lt.Vehicle == vehicle && lt._stateMachine?.CurrentState is Spawn)
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ the launch

    internal static void Launch(LaunchTask task)
    {
        FlightDeck fd = task._flightDeck;
        ObjectBase deck = fd._baseObject;
        ObjectBase v = task.Vehicle;
        Elevator elevator = task._elevator;
        IState state = task._stateMachine.CurrentState;
        string was = state.GetType().Name;
        bool fromSpawn = state is Spawn;
        bool inTakeoff = state is PlaneTakeOff || state is STOLTakeOff || state is VTOLTakeOff;
        bool isHelicopter = v is Helicopter;
        double now = GameTime.missionElapsedTime;
        float tc = GameTime.TimeCompression;
        string vehicleName = v.getUIDAndName();
        string deckName = deck.getUIDAndName();

        // LaunchTask picks the takeoff state from the vehicle type: Plane (and the default) -> PlaneTakeOff; VTOL / STOL ->
        // STOLTakeOff or VTOLTakeOff, both of which head along the launch point's VTOL rotation.
        SeaPower.VehicleType type = task.VehicleType._type;
        bool vtolOrStol = type == SeaPower.VehicleType.VTOL || type == SeaPower.VehicleType.STOL;

        // 1. where the aircraft will appear: over the launch point it uses (or would have used), facing along it
        LaunchPoint used = task._launchPoint;
        LaunchPoint lp = used ?? PickLaunchPoint(task);
        Vector3 local = lp != null ? lp._position : elevator._spawnPosition;
        float localYaw = lp != null ? (vtolOrStol ? lp._vtolRotation : lp._rotation) : elevator._spawnRotation;
        Transform deckTransform = deck.transform;
        Vector3 position = deckTransform.TransformPoint(local);
        Quaternion rotation = Quaternion.Euler(0f, deckTransform.eulerAngles.y + localYaw, 0f);
        float forward = (Mathf.Max(0f, Plugin.ForwardOffsetFeet.Value) + Stagger(lp, elevator)) / AirOps.FeetPerUnit;
        position += rotation * Vector3.forward * forward;
        position.y += (isHelicopter ? Plugin.HelicopterLaunchHeightFeet.Value : Plugin.PlaneLaunchHeightFeet.Value) / AirOps.FeetPerUnit;

        // 2. close the task. StateMachine.setState runs the CURRENT state's onExit before it changes state, so if that throws
        //    (Spawn.onExit does the mission setup) the task is still where it was and nothing below has happened yet.
        //    For the takeoff states this is also where the game hands the aircraft to the flight model.
        task._stateMachine.setState(new FinishFlightDeckTask(task, isRecoveryTask: false));

        // free the equipment the way the skipped states would have
        elevator.ReleaseObject(v);
        elevator._lastUseTime = now;
        AirOps.ReleaseLaunchPoint(used, v);
        AirOps.ReleaseLaunchPoint(lp, v);
        if (lp != null) lp._lastAssigned = now;
        if (!fromSpawn && elevator._incomingObjects.Count == 0)
            AirOps.RestoreElevator(elevator);

        // Spawn.onExit has chosen the climb altitude; the airborne-spawn call below overwrites it with the current height.
        float missionAltitude = v.DesiredAltitude.Value;

        // 3. into the air. From here on the task is finished, so there is no falling back to the game's sequence:
        //    log loudly if the essential steps fail, and always do the bookkeeping.
        try
        {
            if (inTakeoff)
                RepositionAfterTakeoffExit((Aircraft)v, position, rotation);
            else if (v is Aircraft aircraft)
                LaunchAircraft(aircraft, vtolOrStol, lp, deck, position, rotation, missionAltitude, state is PreFlightAnim);
            else if (v is Helicopter helicopter)
                LaunchHelicopter(helicopter, deck, position, rotation, state is PreFlightAnim);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Instant launch of {vehicleName} FAILED after the launch task was closed; the aircraft may be stuck on deck: {e}");
        }
        if (!inTakeoff)
            fd.AircraftCurrentlyOnBoard--; // the takeoff states' own exit already did this one

        if (Plugin.VerboseLog.Value)
        {
            // Campaign integrity, visible in the log: the game's own launchVehicle gave this unit its campaign tag and took it off
            // the deck's on-board list; nothing here changes that.
            string tag = v._campaignTag;
            string campaign = string.IsNullOrEmpty(tag)
                ? "no campaign tag"
                : $"campaign tag '{tag}', still on the deck's on-board list: {fd._campaignUnitsOnBoard.Any(c => c != null && string.Equals(c.CampaignTag, tag, StringComparison.OrdinalIgnoreCase))}";
            Plugin.Log.LogInfo(
                $"Instant launch: {vehicleName} from {deckName} at {tc:0}x, was {was} " +
                $"(launch point {(lp != null ? lp._sectionName : "none")}, alt {position.y * AirOps.FeetPerUnit:0} ft, hasControl {v.HasControl}; {campaign}).");
            AirOps.Watch(v, deckName, "launch");
        }
    }

    private static float Stagger(LaunchPoint lp, Elevator elevator)
    {
        object key = (object)lp ?? elevator;
        int frame = Time.frameCount;
        int index = 0;
        if (_used.TryGetValue(key, out var last) && last.frame == frame)
            index = last.count + 1;
        _used[key] = (frame, index);
        return index * StaggerFeet;
    }

    private static LaunchPoint PickLaunchPoint(LaunchTask task)
    {
        FlightDeck fd = task._flightDeck;
        Elevator elevator = task._elevator;
        LaunchPoint lp = null;
        try
        {
            lp = elevator.getLaunchPoint(task.VehicleType, fd._baseObject is LandUnit, task._ltp != null ? task._ltp._loadoutIndex : -1);
        }
        catch (Exception e)
        {
            Plugin.LogOnce("pick-launchpoint", e);
        }
        if (lp != null) return lp;

        // everything busy: take any launch point of this elevator that allows the vehicle type, busy or not
        foreach (int index in elevator._launchPoints)
        {
            string section = $"LaunchPoint{index}";
            foreach (LaunchPoint candidate in fd._launchPoints)
            {
                if (candidate._sectionName == section && candidate._allowedTypes.Contains(task.VehicleType._type))
                    return candidate;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ fixed wing

    private static void LaunchAircraft(Aircraft ac, bool vtolOrStol, LaunchPoint lp, ObjectBase deck, Vector3 position, Quaternion rotation, float missionAltitude, bool tacViewDone)
    {
        // Essential steps first: unparent from the ship, place, and hand the aircraft to the flight model.
        bool plane = !vtolOrStol;
        ac.giveControl(deck._velocityInKnots, resetAltitude: false);
        Place(ac, position, rotation);
        ac._isInFlight = true;
        Step(() => ac.ImmediateStartEngines(), "engines");
        if (!plane)
            ac.setTelegraph(3); // STOLTakeOff / VTOLTakeOff do this on exit
        // Same call the game uses when it spawns an aircraft already flying: starts at the demanded speed.
        MotionController flight = ac.SetFlightPhysicsModel(isStartingAirborne: true);
        // A launch that was under way still carries the taxi controller's walking speed into the new flight model.
        if (flight != null && flight.Velocity < 0.5f * flight.CommandVelocity)
            flight.SetVelocity(flight.CommandVelocity);
        if (missionAltitude > ac.transform.position.y)
            ac.DesiredAltitude.Value = missionAltitude;

        // What PreFlightAnim / PlaneTakeOff / STOLTakeOff / VTOLTakeOff do to the airframe.
        Step(() =>
        {
            ac.PlayCodeAnim("Canopy_Retract");
            ac.PlayCodeAnim("Wings_Extend");
            ac.PlayCodeAnimsForFlight(playSTOLAnims: true);
            if (!plane)
                ac.PlayCodeAnim("Nozzles_VTOL_Retract");
            if (ac.Ap._exhaustObject != null)
                ac.Ap._exhaustObject.SetActive(value: true);
        }, "airframe-anims");

        if (vtolOrStol)
            Step(() => ApplyVtolRangePenalty(ac, lp), "vtol-range");
        Step(() => SwapFormationLeaderIfNeeded(ac), "formation");
        Step(() => SwitchCamera(deck, ac), "camera");
        if (!tacViewDone)
            Step(() => NotifyTakenOff(ac), "tacview");
    }

    /// <summary>The takeoff states' own exit (run by setState) did the hand-over, camera, formation and counters; the aircraft
    /// is still at the spot where its takeoff run stopped, so move it out ahead of the ship at flying speed.</summary>
    private static void RepositionAfterTakeoffExit(Aircraft ac, Vector3 position, Quaternion rotation)
    {
        Place(ac, position, rotation);
        MotionController flight = ac.Motioncontroller;
        if (flight == null) return;
        float demanded = 0f;
        try { demanded = ac.SpeedCommand.Value.GetDemandedSpeedInMetresPerSecond(ac); } catch { }
        float minimum = ac.Ap._stallSpeed * 1.5f * 0.514444f;
        float speed = Mathf.Max(demanded, minimum);
        if (flight.Velocity < 0.5f * speed)
            flight.SetVelocity(speed);
    }

    /// <summary>VTOLTakeOff.onEnter shortens the range of vertical takeoffs; keep that cost.</summary>
    private static void ApplyVtolRangePenalty(Aircraft ac, LaunchPoint lp)
    {
        // For both VTOL and STOL vehicles LaunchTask uses STOLTakeOff when the launch point also takes planes and
        // VTOLTakeOff (which applies this penalty) otherwise.
        if (lp != null && lp._allowedTypes.Contains(SeaPower.VehicleType.Plane))
            return;
        ac.RangeInKm.Value *= ac.Ap._vtolRangeMultiplier;
        ac.ActualRangeInKm.Value = ac.RangeInKm.Value;
        float half = ac.RangeInKm.Value / 2f * 1000f * 0.0148809375f;
        float loadFactor = 1f + ac.Ap._loadoutMass / ac.Ap._emptyMass;
        ac.RangeOnMap.Value = half / loadFactor;
    }

    /// <summary>Copy of the block at the end of PlaneTakeOff.onExit: if this aircraft got airborne before the
    /// formation leader (still on deck), make it the leader and hand it the leader's waypoints.</summary>
    private static void SwapFormationLeaderIfNeeded(Aircraft ac)
    {
        if (ac.Formation == null || ac.Formation.LeaderStation == null) return;
        ObjectBase leader = ac.Formation.LeaderStation.UnitObject;
        if (leader == null || leader == ac || leader.transform.parent == null) return;

        List<(GeoPosition, WaypointData.WaypointHeightState)> waypoints = leader.GetAllWaypointsGeoPositions();
        ac.Formation.SwapLeader(ac);
        ac.Formation.Reform(ac.Formation.Pattern, ac.Formation.Spacing);
        ac.RemoveWaypoints();
        for (int i = 0; i < waypoints.Count; i++)
            ac.setWaypointTask(waypoints[i].Item1, $"WP_{i}", waypoints[i].Item2);
    }

    // ------------------------------------------------------------------ helicopters

    private static void LaunchHelicopter(Helicopter heli, ObjectBase deck, Vector3 position, Quaternion rotation, bool tacViewDone)
    {
        // A launch that was under way still has its taxi controller attached; PreFlightAnim / OffsetAtLaunchPoint destroy it
        // for helicopters, the flight control system is the only thing that may move the helicopter from here on.
        if (heli.Motioncontroller != null)
            UnityEngine.Object.Destroy(heli.Motioncontroller);

        // Same calls HelicopterTakeOff makes once the rotors are up to speed.
        Step(() => { heli.powerUpEngines(); heli.engageRotors(); }, "heli-start");
        heli.GiveControl(deck._velocityInKnots);
        Place(heli, position, rotation);
        heli._ignoreInCollisionAvoidanceAfterLaunch = true;
        heli.setImmediateFlightConditions();
        heli._isInFlight = true;

        Step(() =>
        {
            heli.PlayCodeAnim("Canopy_Retract"); // PreFlightAnim does these for every vehicle type
            heli.PlayCodeAnim("Wings_Extend");
            heli.PlayCodeAnimsForFlight();
        }, "heli-anims");
        Step(() => SwitchCamera(deck, heli), "camera");
        if (!tacViewDone)
            Step(() => NotifyTakenOff(heli), "tacview");
    }

    // ------------------------------------------------------------------ shared

    /// <summary>Teleport the (already unparented) aircraft. SetTeleportProtection makes the game re-read the geo position
    /// from the transform so the floating-origin logic does not drag it back.</summary>
    private static void Place(ObjectBase v, Vector3 position, Quaternion rotation)
    {
        v.transform.SetPositionAndRotation(position, rotation);
        Physics.SyncTransforms();
        v.SetTeleportProtection();
        Rigidbody rb = v.RigidBody;
        if (rb != null)
        {
            rb.position = position;
            rb.rotation = rotation;
        }
    }

    /// <summary>Same option check as PlaneTakeOff.giveControl / HelicopterTakeOff.giveControl.</summary>
    private static void SwitchCamera(ObjectBase deck, ObjectBase v)
    {
        bool follow = false;
        switch (Globals._autoSwitchToLaunchedWeapon)
        {
            case Globals.CameraSwitchTo.All:
                follow = true;
                break;
            case Globals.CameraSwitchTo.Player:
                follow = deck._taskforce.Side == Taskforce.TfType.Player;
                break;
            case Globals.CameraSwitchTo.Enemy:
                follow = deck._taskforce.Side != Taskforce.TfType.Player;
                break;
            case Globals.CameraSwitchTo.Local:
                follow = deck == Singleton<RenderPosition>.Instance.getSelectedObject();
                break;
        }
        if (follow)
            Singleton<RenderPosition>.Instance.switchToObject(v, performSmoothMove: true);
    }

    private static void NotifyTakenOff(ObjectBase v)
    {
        if (Singleton<TacView>.InstanceExists())
            Singleton<TacView>.Instance.OnEvent(TacView.TCEvent.TakenOff, v);
    }

    /// <summary>Cosmetic / optional steps must never undo an aircraft that is already flying.</summary>
    private static void Step(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Plugin.LogOnce("instant-launch/" + what, e);
        }
    }
}
