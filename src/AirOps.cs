using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace MainMod;

/// <summary>
/// Shared plumbing for the air-ops fixes (InstantLaunch, InstantLanding): the per-frame hooks on FlightDeck.OnUpdate,
/// the lift of the game's "air ops => 30x" clamp, deck-equipment clean-up and a few diagnostics.
///
/// Why the clamp has to be lifted by hand: FlightDeck.OnUpdate computes
///     _performingAirOps = FlightDeckTasks.Any(t => !t._isRTB &amp;&amp; !(t is CoolDownTask) &amp;&amp; !(t is PendingLaunchTask))
/// and then calls GameTime.SetTimeCompression(Globals._flightDeckTCLimit) if compression is above that limit, BEFORE
/// handleFlightDeckTasks() removes finished tasks. Every launch/landing task therefore trips it. While every such task is
/// one we replace, the limit is raised out of the way (BeforeUpdate); otherwise it is the game's value.
/// </summary>
internal static class AirOps
{
    /// <summary>1 Unity unit = 67.2 m (the game's own conversion).</summary>
    internal const float MetersPerUnit = 67.200066f;

    /// <summary>1 Unity unit = 220.47 ft: the game shows altitudes in feet (Helicopter.setPresetHeight uses this factor).</summary>
    internal const float FeetPerUnit = 220.47266f;

    private const float LiftedLimit = 100000f;

    /// <summary>What Globals._flightDeckTCLimit is for real deck operations (set at mission start from the game / config).</summary>
    internal static float FlightDeckBaseLimit = 30f;

    /// <summary>Tasks whose conversion threw before they were closed: the game's own sequence takes them over.</summary>
    internal static readonly HashSet<FlightDeckTask> Failed = new HashSet<FlightDeckTask>();

    internal static bool WantedLaunch =>
        Plugin.Active && Plugin.InstantLaunch.Value && GameTime.TimeCompression > Plugin.InstantLaunchAbove.Value;

    internal static bool WantedLanding =>
        Plugin.Active && Plugin.InstantLanding.Value && GameTime.TimeCompression > Plugin.InstantLandingAbove.Value;

    internal static void Reset()
    {
        Failed.Clear();
        InstantLaunch.Reset();
        LaunchDelay.Reset();
        CompressionBlockers.Reset();
        _watch.Clear();
        _inoperableSeen.Clear();
    }

    // ------------------------------------------------------------------ per-frame hooks (see Patches.cs)

    /// <summary>Runs just before FlightDeck.OnUpdate: decide which air-ops speed limit the game will apply this frame.</summary>
    internal static void BeforeUpdate(FlightDeck fd)
    {
        if (!Plugin.Active) return;
        bool lift = !GameTime.IsPaused() && (WantedLaunch || WantedLanding) && ClampOnlyBecauseOfReplacedTasks(fd);
        // an enemy deck the player cannot see must not slow time down (that would reveal its air operations)
        bool unseen = Combat.OnlySeenActive && fd._baseObject != null && !Combat.KnownToPlayer(fd._baseObject);
        Globals._flightDeckTCLimit = (lift || unseen) ? LiftedLimit : FlightDeckBaseLimit;

        // what the game would call "performing air ops" on this deck, for the compression-blocker model (GUI)
        int tasks = 0;
        string breakdown = "";
        if (!unseen) tasks = AirOpsTasks(fd, out breakdown);
        CompressionBlockers.ReportDeck(fd, tasks, breakdown, lift);
    }

    /// <summary>The tasks that make FlightDeck.OnUpdate treat the deck as performing air ops (and so clamp compression).</summary>
    private static int AirOpsTasks(FlightDeck fd, out string breakdown)
    {
        int launching = 0, landing = 0, returning = 0, other = 0;
        foreach (FlightDeckTask t in fd.FlightDeckTasks)
        {
            if (t._isRTB || t is CoolDownTask || t is PendingLaunchTask) continue;
            if (t is LaunchTask) launching++;
            else if (t is RecoveryTask) landing++;
            else if (t is ReturnTask) returning++;
            else other++;
        }
        var parts = new List<string>();
        if (launching > 0) parts.Add($"{launching} launching");
        if (landing > 0) parts.Add($"{landing} landing");
        if (returning > 0) parts.Add($"{returning} returning");
        if (other > 0) parts.Add($"{other} other");
        breakdown = string.Join(", ", parts);
        return launching + landing + returning + other;
    }

    /// <summary>Runs just after FlightDeck.OnUpdate: convert the launches / landings that are ready.</summary>
    internal static void AfterUpdate(FlightDeck fd)
    {
        TickWatch();

        ObjectBase deck = fd._baseObject;
        if (deck == null || deck.IsDestroyed) return;
        if (fd.Inoperable.Value)
        {
            NoteInoperable(fd);
            return;
        }

        bool launch = WantedLaunch, landing = WantedLanding;
        if ((!launch && !landing) || GameTime.IsPaused()) return;

        List<LaunchTask> launches = null;
        List<RecoveryTask> landings = null;
        foreach (FlightDeckTask t in fd.FlightDeckTasks)
            Scan(t, launch, landing, ref launches, ref landings);
        foreach (FlightDeckTask t in fd._flightDeckTasksToAdd)
            Scan(t, launch, landing, ref launches, ref landings);

        if (launches != null)
        {
            // A flight's mission setup runs in the LEADER's Spawn.onExit (wingmen skip it): leaders first, and a wingman
            // waits while its leader is still in Spawn. The game's own sequence guarantees this order by construction.
            launches.Sort((a, b) => a.Vehicle.IsFormationWingman.CompareTo(b.Vehicle.IsFormationWingman));
            foreach (LaunchTask task in launches)
            {
                try
                {
                    if (InstantLaunch.MustWaitForLeader(fd, task)) continue; // next frame
                    if (LaunchDelay.HoldConversion(fd, task)) continue;      // keeps the gap between aircraft
                    InstantLaunch.Launch(task);
                    LaunchDelay.NoteConverted(fd);
                }
                catch (Exception e)
                {
                    Failed.Add(task);
                    Plugin.LogOnce("instant-launch", e);
                }
            }
        }

        if (landings != null)
        {
            foreach (RecoveryTask task in landings)
            {
                try
                {
                    InstantLanding.Land(task);
                }
                catch (Exception e)
                {
                    Failed.Add(task);
                    Plugin.LogOnce("instant-landing", e);
                }
            }
        }
    }

    private static void Scan(FlightDeckTask t, bool launch, bool landing, ref List<LaunchTask> launches, ref List<RecoveryTask> landings)
    {
        if (Failed.Contains(t)) return;
        if (launch && t is LaunchTask lt && InstantLaunch.CanConvert(lt))
            (launches ??= new List<LaunchTask>()).Add(lt);
        else if (landing && t is RecoveryTask rt && InstantLanding.CanConvert(rt))
            (landings ??= new List<RecoveryTask>()).Add(rt);
    }

    // ------------------------------------------------------------------ the clamp

    /// <summary>Would the game's "air ops" speed limit apply to this deck only because of tasks we are about to replace?</summary>
    private static bool ClampOnlyBecauseOfReplacedTasks(FlightDeck fd)
    {
        bool launch = WantedLaunch, landing = WantedLanding;
        foreach (FlightDeckTask t in fd.FlightDeckTasks)
        {
            if (t._isRTB || t is CoolDownTask || t is PendingLaunchTask) continue;
            if (Failed.Contains(t)) return false;

            // A task that was just closed (by us, or by the game) stays in this list for one more frame: the delayed
            // removal is applied in handleFlightDeckTasks(), AFTER the clamp has been evaluated.
            if (t._stateMachine?.CurrentState is FinishFlightDeckTask) continue;

            if (t is LaunchTask lt)
            {
                if (launch && InstantLaunch.IsHandled(lt)) continue;
                return false;
            }
            // Returning aircraft: RTB / holding / circuit are ordinary flight; only the final approach and the deck
            // handling are replaced.
            if (t is RecoveryTask || t is ReturnTask)
            {
                if (landing) continue;
                return false;
            }
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ deck equipment

    /// <summary>Free a launch point the way the end of a real takeoff / taxi does.</summary>
    internal static void ReleaseLaunchPoint(LaunchPoint p, ObjectBase v)
    {
        if (p == null) return;
        p.ReleaseObject(v);
        p.Release(v);
        p.CleanUp();
    }

    /// <summary>
    /// Put an elevator back into the state it has between operations. The sequence states only set these flags when they
    /// finish, so a sequence that is cut short mid-way would leave a lowered elevator / open doors marked as busy forever.
    /// </summary>
    internal static void RestoreElevator(Elevator e)
    {
        e._isLowered = e._startsLowered;
        e._hangarOpen = false;
        if (e._startsLowered)
            Pose(e._moveDownAnimation, e._moveUpAnimation);
        else
            Pose(e._moveUpAnimation, e._moveDownAnimation);
        Pose(e._hangarCloseAnimation, e._hangarOpenAnimation);
    }

    /// <summary>Snap a moving part to the end of <paramref name="completed"/> (or the start of <paramref name="fallbackStart"/>).</summary>
    private static void Pose(ObjectCodeAnimation completed, ObjectCodeAnimation fallbackStart)
    {
        try
        {
            if (completed != null && completed.isPlayable())
                completed.setOpenState();
            else if (fallbackStart != null && fallbackStart.isPlayable())
                fallbackStart.setClosedState();
        }
        catch (Exception e)
        {
            Plugin.LogOnce("restore-pose", e);
        }
    }

    // ------------------------------------------------------------------ diagnostics

    private sealed class Watched
    {
        public ObjectBase Vehicle;
        public string Name, Deck, Kind;
        public double Time0;
        public int Frame0;
        public bool Reported;
    }

    private static readonly List<Watched> _watch = new List<Watched>();
    private static int _watchFrame = -1;
    private static readonly HashSet<FlightDeck> _inoperableSeen = new HashSet<FlightDeck>();

    /// <summary>Remember an aircraft we put into the air so its first moments can be written to the log.</summary>
    internal static void Watch(ObjectBase v, string deck, string kind)
    {
        if (!Plugin.VerboseLog.Value || v == null) return;
        _watch.Add(new Watched { Vehicle = v, Name = v.getUIDAndName(), Deck = deck, Kind = kind, Time0 = GameTime.missionElapsedTime, Frame0 = Time.frameCount });
    }

    /// <summary>A few frames after a conversion log what the aircraft actually looks like, and say so if it vanishes.</summary>
    private static void TickWatch()
    {
        if (_watch.Count == 0 || _watchFrame == Time.frameCount) return;
        _watchFrame = Time.frameCount;
        for (int i = _watch.Count - 1; i >= 0; i--)
        {
            Watched w = _watch[i];
            double age = GameTime.missionElapsedTime - w.Time0;
            if (w.Vehicle == null || w.Vehicle.IsDestroyed)
            {
                Plugin.Log.LogWarning($"[watch] {w.Name} ({w.Kind} from {w.Deck}) is gone {age:0} s of mission time later " +
                                      "(destroyed in the game: see the in-game event log for the cause).");
                _watch.RemoveAt(i);
                continue;
            }
            if (!w.Reported && Time.frameCount - w.Frame0 >= 3)
            {
                w.Reported = true;
                ObjectBase v = w.Vehicle;
                string controller = v is Aircraft a && a.Motioncontroller != null ? a.Motioncontroller.GetType().Name : (v is Helicopter ? "helicopter flight control" : "none");
                Plugin.Log.LogInfo($"[watch] {w.Name} ({w.Kind} from {w.Deck}) +3 frames: alt {v.transform.position.y * FeetPerUnit:0} ft, " +
                                   $"speed {v._velocityInKnots:0} kn, controller {controller}, parent {(v.transform.parent == null ? "none" : v.transform.parent.name)}, " +
                                   $"hasControl {v.HasControl}.");
            }
            if (age > 180.0)
                _watch.RemoveAt(i);
        }
    }

    private static void NoteInoperable(FlightDeck fd)
    {
        if (!_inoperableSeen.Add(fd)) return;
        Plugin.Log.LogWarning($"Flight deck of {fd._baseObject.getUIDAndName()} is INOPERABLE (damaged). The game itself destroys the aircraft that " +
                              "were in its launch/recovery sequence and halts deck operations; the mod does not touch this deck.");
    }
}
