using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace MainMod;

/// <summary>
/// Making high time compression less laggy.
///
/// WHY IT LAGS (read from the game code; see notes section 24):
///   Above GameTime._physicsTimeScaleCap (10) Unity's timeScale stays at 10 and the game fakes the rest by stretching the simulation
///   step: GameTime.fixedDeltaTime = Time.fixedDeltaTime * TC / 10. So from 10x up the work per real second is constant - about 10 / Time.fixedDeltaTime
///   = 600 full simulation steps per second (every object's OnFixedUpdate: AI, every weapon / sensor system, flight and ship models, plus the
///   physics jobs) - and raising the compression only makes each step cover more game time. If one step costs more than 1.7 ms the loop
///   cannot keep up: frames get long, FPS drops, Unity clamps the catch-up (Time.maximumDeltaTime) and the game clock runs slower than the
///   number on the screen says.
///
/// WHAT CAN BE DONE:
///   1. FEWER, BIGGER STEPS (AdaptiveStep): the game already switches Time.fixedDeltaTime between 1/60 and 1/30 by FPS. Here, above
///      AdaptiveStepAbove, the step grows in more levels (up to MaxFixedStepSeconds) while the simulation loop uses more than a target share of
///      wall-clock time, and shrinks again when it has room. Cost: coarser integration (the same trade the game makes at 1/30).
///   2. CHEAPER MODELS (SimplePhysics): helicopters, submarines and animals have a built-in kinematic mode that the game only uses above 999x
///      (field _timeCompressionLimt = 999). Lowering it to SimplePhysicsAbove enables that "time skip physics" earlier. Aircraft already switch
///      to kinematic control above 50x by themselves.
///   3. MEASURE (Profiler): opt-in wall-clock breakdown per phase / object type / system type, written to the log, so the next optimisation is
///      chosen from data instead of guesses.
/// </summary>
internal static class Performance
{
    internal static ConfigEntry<bool> AdaptiveFixedStep;
    internal static ConfigEntry<float> AdaptiveStepAbove;
    internal static ConfigEntry<float> AdaptiveStepLoadTarget;
    internal static ConfigEntry<float> MaxFixedStepSeconds;
    internal static ConfigEntry<float> SimplePhysicsAbove;
    internal static ConfigEntry<bool> Profile;
    internal static ConfigEntry<float> ProfileIntervalSeconds;

    internal static void Bind(ConfigFile cfg)
    {
        AdaptiveFixedStep = cfg.Bind("Performance", "AdaptiveFixedStep", true,
            "Above AdaptiveStepAbove compression, let the simulation step grow when the simulation loop is the bottleneck (fewer, bigger steps = " +
            "less lag, coarser movement). It only reacts when the loop uses more than AdaptiveStepLoadTarget of the wall-clock time, so a fast machine " +
            "is not affected. false = the game's own two-level step (1/60 s, 1/30 s when FPS < 30).");
        AdaptiveStepAbove = cfg.Bind("Performance", "AdaptiveStepAbove", 30f,
            "Compression (x) above which the adaptive step is used. At or below it the game's own step logic is untouched.");
        AdaptiveStepLoadTarget = cfg.Bind("Performance", "AdaptiveStepLoadTarget", 0.4f,
            "Share of wall-clock time (0.4 = 40%) the simulation steps may use before the step is enlarged. Lower = smoother frames but a coarser " +
            "simulation sooner; higher = finer simulation but more lag.");
        MaxFixedStepSeconds = cfg.Bind("Performance", "MaxFixedStepSeconds", 0.05f,
            "Largest simulation step the adaptive logic may use, in seconds at normal speed. The game's normal step is 0.0167 (1/60), its low-FPS step " +
            "0.0333 (1/30). 0.05 = 1/20, 0.0667 = 1/15, 0.1 = 1/10. Each level roughly halves to thirds the cost of the loop; movement per step grows with it.");
        SimplePhysicsAbove = cfg.Bind("Performance", "SimplePhysicsAbove", 0f,
            "Compression (x) above which helicopters, submarines and animals use the game's simple 'time skip' movement (no force/depth/pitch " +
            "simulation; the game itself only does this above 999x). Fixed-wing aircraft use their simple controls above this too (game: 50x). " +
            "0 = keep the game's values.");
        Profile = cfg.Bind("Performance", "Profile", false,
            "Write a wall-clock breakdown of where the time goes (simulation steps, update loop, object types, system types) to the log every " +
            "ProfileIntervalSeconds. Meant for finding lag causes: run a mission at 1x for a minute, then at 200x, and send the log.");
        ProfileIntervalSeconds = cfg.Bind("Performance", "ProfileIntervalSeconds", 10f,
            "How often the profile is written.");
    }

    /// <summary>Called at every mission start (after Plugin.Active is known).</summary>
    internal static void OnMissionStart()
    {
        LoadMeter.Reset();
        AdaptiveStep.Reset();
        try
        {
            Plugin.Log.LogInfo($"Engine timing: fixedDeltaTime {Time.fixedDeltaTime:0.0000} s, maximumDeltaTime {Time.maximumDeltaTime:0.000} s, " +
                               $"targetFrameRate {Application.targetFrameRate}, vSync {QualitySettings.vSyncCount}, physics step cap {GameTime._physicsTimeScaleCap}x.");
        }
        catch (Exception e)
        {
            Plugin.LogOnce("engine-timing", e);
        }
        if (Plugin.Active && Profile.Value)
            Profiler.Install();
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------
// load meter: how much of the wall-clock time is spent inside the simulation steps

internal static class LoadMeter
{
    private static long _stepStart;
    private static long _fixedTicks;
    private static int _steps;
    private static long _windowStart = Stopwatch.GetTimestamp();

    internal static void Reset()
    {
        _fixedTicks = 0;
        _steps = 0;
        _windowStart = Stopwatch.GetTimestamp();
    }

    internal static void StepBegin() => _stepStart = Stopwatch.GetTimestamp();

    internal static void StepEnd()
    {
        _fixedTicks += Stopwatch.GetTimestamp() - _stepStart;
        _steps++;
    }

    /// <summary>Share of wall-clock time spent in simulation steps since the last call (0..1), and the steps per second; resets the window.</summary>
    internal static (double duty, double stepsPerSecond) Take()
    {
        long now = Stopwatch.GetTimestamp();
        double wall = (now - _windowStart) / (double)Stopwatch.Frequency;
        double fixedSeconds = _fixedTicks / (double)Stopwatch.Frequency;
        int steps = _steps;
        _windowStart = now;
        _fixedTicks = 0;
        _steps = 0;
        if (wall <= 0.0) return (0.0, 0.0);
        return (Math.Min(1.0, fixedSeconds / wall), steps / wall);
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------
// adaptive simulation step

internal static class AdaptiveStep
{
    // seconds at normal speed; the game itself uses the first two
    private static readonly float[] Levels = { 1f / 60f, 1f / 30f, 0.05f, 1f / 15f, 0.1f };
    private static int _level;
    private static double _lastEval;
    private static bool _owning; // true while we, not the game, decide the step

    /// <summary>For a GUI: current simulation step (s) and the last measured loop load (0..1).</summary>
    internal static float CurrentStep => Time.fixedDeltaTime;
    internal static double LastLoad { get; private set; }
    internal static int Level => _level;

    internal static void Reset()
    {
        _level = 0;
        _lastEval = 0;
        _owning = false;
    }

    /// <summary>Prefix of GameTime.SetAdaptiveFixedDeltaTime. Returns true to let the game's own logic run.</summary>
    internal static bool Handle()
    {
        bool active = Plugin.Active && Performance.AdaptiveFixedStep.Value
                      && GameTime.TimeCompression > Performance.AdaptiveStepAbove.Value && !GameTime.IsPaused();
        if (!active)
        {
            if (_owning)
            {
                // leaving the adaptive regime: hand a sane step back, the game's two-level logic takes over from here
                _owning = false;
                _level = 0;
                Time.fixedDeltaTime = Levels[0];
                DM._usingLowFrameRateTimestep = false;
                LoadMeter.Reset();
            }
            return true;
        }

        if (!_owning)
        {
            _owning = true;
            LoadMeter.Reset();
            _lastEval = Time.realtimeSinceStartupAsDouble;
            return false;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (now - _lastEval < 1.0) return false; // our own once-per-second cadence (the game's check would run every frame)
        _lastEval = now;

        var (duty, stepsPerSecond) = LoadMeter.Take();
        LastLoad = duty;

        int max = 1;
        for (int i = 1; i < Levels.Length; i++)
        {
            if (Levels[i] <= Performance.MaxFixedStepSeconds.Value + 1e-4f) max = i;
        }

        float target = Mathf.Clamp(Performance.AdaptiveStepLoadTarget.Value, 0.1f, 0.9f);
        int before = _level;
        if (duty > target && _level < max) _level++;
        else if (duty < target * 0.5 && _level > 0) _level--;
        if (_level > max) _level = max;

        if (_level != before)
        {
            Time.fixedDeltaTime = Levels[_level];
            DM._usingLowFrameRateTimestep = _level > 0;
            if (Plugin.VerboseLog.Value)
            {
                Plugin.Log.LogInfo($"Adaptive step: {Levels[before]:0.0000} s -> {Levels[_level]:0.0000} s at {GameTime.TimeCompression:0}x " +
                                   $"(simulation loop used {duty * 100:0}% of wall time at {stepsPerSecond:0} steps/s, target {target * 100:0}%).");
            }
        }
        else
        {
            Time.fixedDeltaTime = Levels[_level];
        }
        return false;
    }
}

[HarmonyPatch(typeof(GameTime), "SetAdaptiveFixedDeltaTime")]
internal static class GameTime_SetAdaptiveFixedDeltaTime_Patch
{
    private static bool Prefix()
    {
        try
        {
            return AdaptiveStep.Handle();
        }
        catch (Exception e)
        {
            Plugin.LogOnce("adaptive-step", e);
            return true;
        }
    }
}

[HarmonyPatch(typeof(GameMain), "PerformFixedUpdate")]
internal static class GameMain_PerformFixedUpdate_Patch
{
    private static void Prefix() => LoadMeter.StepBegin();

    private static void Postfix() => LoadMeter.StepEnd();
}

// ---------------------------------------------------------------------------------------------------------------------------------
// simple 'time skip' movement for helicopters / submarines / animals

[HarmonyPatch(typeof(GameMain), nameof(GameMain.addObject))]
internal static class GameMain_AddObject_Patch
{
    private static FieldInfo _submarineLimit;
    private static FieldInfo _biologicLimit;

    private static void Postfix(ObjectBase obj)
    {
        try
        {
            float limit = Performance.SimplePhysicsAbove.Value;
            if (!Plugin.Active || limit <= 0f || obj == null) return;
            if (obj is Helicopter helicopter)
            {
                helicopter._timeCompressionLimt = limit;
            }
            else if (obj is Submarine submarine)
            {
                (_submarineLimit ??= AccessTools.Field(typeof(Submarine), "_timeCompressionLimt"))?.SetValue(submarine, limit);
            }
            else if (obj is Biologic biologic)
            {
                (_biologicLimit ??= AccessTools.Field(typeof(Biologic), "_timeCompressionLimt"))?.SetValue(biologic, limit);
            }
        }
        catch (Exception e)
        {
            Plugin.LogOnce("simple-physics", e);
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------
// profiler (opt-in, installed at mission start only when [Performance] Profile = true)

internal static class Profiler
{
    private static Harmony _harmony;
    private static readonly Dictionary<string, long> _phases = new Dictionary<string, long>();
    private static readonly Dictionary<string, long> _objects = new Dictionary<string, long>();
    private static readonly Dictionary<string, long> _systems = new Dictionary<string, long>();
    private static readonly Dictionary<string, long> _motion = new Dictionary<string, long>();
    private static int _objDepth, _sysDepth, _motDepth;
    private static long _windowStart;
    private static int _frames;
    private static int _steps;

    internal static void Install()
    {
        if (_harmony != null)
        {
            ResetWindow();
            return;
        }
        _harmony = new Harmony(Entry.Guid + ".profile");
        int ok = 0, failed = 0;

        // phases of the frame
        foreach (MethodBase m in Phases())
        {
            if (Hook(m, nameof(PhasePre), nameof(PhasePost))) ok++; else failed++;
        }
        // frame counter + periodic report
        TryPatch(AccessTools.Method(typeof(GameMain), "Update"), null, nameof(FramePost), ref ok, ref failed);

        // every ObjectBase / BaseSystem / MotionController subclass that has its own OnFixedUpdate
        Assembly game = typeof(GameMain).Assembly;
        foreach (Type t in game.GetTypes())
        {
            if (t.IsAbstract) continue;
            MethodInfo m = t.GetMethod("OnFixedUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (m == null) continue;
            if (typeof(ObjectBase).IsAssignableFrom(t)) { if (Hook(m, nameof(ObjPre), nameof(ObjPost))) ok++; else failed++; }
            else if (typeof(BaseSystem).IsAssignableFrom(t)) { if (Hook(m, nameof(SysPre), nameof(SysPost))) ok++; else failed++; }
            else if (typeof(MotionController).IsAssignableFrom(t)) { if (Hook(m, nameof(MotPre), nameof(MotPost))) ok++; else failed++; }
        }
        // abstract-ish bases that run the shared code
        foreach (Type t in new[] { typeof(ObjectBase), typeof(BaseSystem), typeof(MotionController) })
        {
            MethodInfo m = t.GetMethod("OnFixedUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (m == null) continue;
            string pre = t == typeof(ObjectBase) ? nameof(ObjPre) : t == typeof(BaseSystem) ? nameof(SysPre) : nameof(MotPre);
            string post = t == typeof(ObjectBase) ? nameof(ObjPost) : t == typeof(BaseSystem) ? nameof(SysPost) : nameof(MotPost);
            if (Hook(m, pre, post)) ok++; else failed++;
        }

        ResetWindow();
        Plugin.Log.LogInfo($"[perf] profiler installed: {ok} hooks ({failed} failed). A report is written every {Performance.ProfileIntervalSeconds.Value:0} s.");
    }

    private static IEnumerable<MethodBase> Phases()
    {
        yield return AccessTools.Method(typeof(GameMain), "PerformFixedUpdate");
        yield return AccessTools.Method(typeof(GameMain), "Update");
        yield return AccessTools.Method(typeof(GameMain), "LateUpdate");
        yield return AccessTools.Method(typeof(GameMain), "rebuildIncomingWeapons");
        yield return AccessTools.Method(typeof(GameFixedUpdater), "fixedUpdate");
        yield return AccessTools.Method(typeof(GameUpdater), "update");
        yield return AccessTools.Method(typeof(JobPerformer), "CompleteFixedUpdateJobs");
        yield return AccessTools.Method(typeof(JobPerformer), "StartFixedUpdateJobs");
        yield return AccessTools.Method(typeof(TaskforceManager), "OnFixedUpdate");
        yield return AccessTools.Method(typeof(TaskforceManager), "OnUpdate");
        yield return AccessTools.Method(typeof(ProjectileManager), "OnFixedUpdate");
    }

    private static bool Hook(MethodBase target, string pre, string post)
    {
        if (target == null) return false;
        try
        {
            _harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Profiler), pre),
                postfix: new HarmonyMethod(typeof(Profiler), post));
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogOnce("perf-hook/" + target.DeclaringType?.Name + "." + target.Name, e);
            return false;
        }
    }

    private static void TryPatch(MethodBase target, string pre, string post, ref int ok, ref int failed)
    {
        if (target == null) { failed++; return; }
        try
        {
            _harmony.Patch(target,
                prefix: pre == null ? null : new HarmonyMethod(typeof(Profiler), pre),
                postfix: post == null ? null : new HarmonyMethod(typeof(Profiler), post));
            ok++;
        }
        catch (Exception e)
        {
            failed++;
            Plugin.LogOnce("perf-hook/" + target.Name, e);
        }
    }

    // ------------------------------------------------------------ hook bodies

    private static void PhasePre(MethodBase __originalMethod, out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        // a game exception inside an object update would leave the nesting counters wrong: re-sync at the start of every step
        if (__originalMethod.Name == "PerformFixedUpdate") _objDepth = _sysDepth = _motDepth = 0;
    }

    private static void PhasePost(MethodBase __originalMethod, long __state)
    {
        string name = __originalMethod.Name;
        Add(_phases, __originalMethod.DeclaringType.Name + "." + name, Stopwatch.GetTimestamp() - __state);
        if (name == "PerformFixedUpdate") _steps++;
    }

    private static void ObjPre(out long __state) => __state = _objDepth++ == 0 ? Stopwatch.GetTimestamp() : 0L;

    private static void ObjPost(object __instance, long __state)
    {
        _objDepth--;
        if (__state != 0) Add(_objects, __instance.GetType().Name, Stopwatch.GetTimestamp() - __state);
    }

    private static void SysPre(out long __state) => __state = _sysDepth++ == 0 ? Stopwatch.GetTimestamp() : 0L;

    private static void SysPost(object __instance, long __state)
    {
        _sysDepth--;
        if (__state != 0) Add(_systems, __instance.GetType().Name, Stopwatch.GetTimestamp() - __state);
    }

    private static void MotPre(out long __state) => __state = _motDepth++ == 0 ? Stopwatch.GetTimestamp() : 0L;

    private static void MotPost(object __instance, long __state)
    {
        _motDepth--;
        if (__state != 0) Add(_motion, __instance.GetType().Name, Stopwatch.GetTimestamp() - __state);
    }

    private static void FramePost()
    {
        _frames++;
        double wall = (Stopwatch.GetTimestamp() - _windowStart) / (double)Stopwatch.Frequency;
        if (wall >= Math.Max(2f, Performance.ProfileIntervalSeconds.Value))
            Report(wall);
    }

    private static void Add(Dictionary<string, long> d, string key, long ticks)
    {
        d.TryGetValue(key, out long v);
        d[key] = v + ticks;
    }

    private static void ResetWindow()
    {
        _phases.Clear();
        _objects.Clear();
        _systems.Clear();
        _motion.Clear();
        _objDepth = _sysDepth = _motDepth = 0;
        _frames = 0;
        _steps = 0;
        _windowStart = Stopwatch.GetTimestamp();
    }

    private static void Report(double wall)
    {
        try
        {
            double Pct(long ticks) => 100.0 * ticks / Stopwatch.Frequency / wall;
            string Top(Dictionary<string, long> d, int n) =>
                string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(n).Select(kv => $"{kv.Key} {Pct(kv.Value):0.0}%"));
            long Phase(string k) => _phases.TryGetValue(k, out long v) ? v : 0;

            double fps = _frames / wall;
            var sb = new StringBuilder();
            sb.Append($"[perf] {GameTime.TimeCompression:0}x (Unity timeScale {Time.timeScale:0.#}), {fps:0.0} fps, fixed step {Time.fixedDeltaTime:0.0000} s, ")
              .Append($"{(_frames > 0 ? (double)_steps / _frames : 0.0):0.0} sim steps/frame | wall time: ")
              .Append($"sim steps {Pct(Phase("GameMain.PerformFixedUpdate")):0}%, update {Pct(Phase("GameMain.Update")):0}%, late {Pct(Phase("GameMain.LateUpdate")):0}%, ")
              .Append($"rest (render/UI/GC/vsync) {100.0 - Pct(Phase("GameMain.PerformFixedUpdate")) - Pct(Phase("GameMain.Update")) - Pct(Phase("GameMain.LateUpdate")):0}%");
            Plugin.Log.LogInfo(sb.ToString());
            Plugin.Log.LogInfo("[perf]   inside a step: managers " + Pct(Phase("GameFixedUpdater.fixedUpdate")).ToString("0.0") +
                               "% (taskforce " + Pct(Phase("TaskforceManager.OnFixedUpdate")).ToString("0.0") + "%, projectiles " +
                               Pct(Phase("ProjectileManager.OnFixedUpdate")).ToString("0.0") + "%), jobs complete/start " +
                               Pct(Phase("JobPerformer.CompleteFixedUpdateJobs")).ToString("0.0") + "/" + Pct(Phase("JobPerformer.StartFixedUpdateJobs")).ToString("0.0") + "%");
            Plugin.Log.LogInfo("[perf]   inside update: managers " + Pct(Phase("GameUpdater.update")).ToString("0.0") + "% (taskforce " +
                               Pct(Phase("TaskforceManager.OnUpdate")).ToString("0.0") + "%), incoming-weapon rebuild " +
                               Pct(Phase("GameMain.rebuildIncomingWeapons")).ToString("0.0") + "%");
            Plugin.Log.LogInfo("[perf]   object types (OnFixedUpdate, % of wall): " + Top(_objects, 8));
            Plugin.Log.LogInfo("[perf]   system types (inside objects):           " + Top(_systems, 8));
            Plugin.Log.LogInfo("[perf]   motion controllers (inside aircraft):     " + Top(_motion, 5));
        }
        catch (Exception e)
        {
            Plugin.LogOnce("perf-report", e);
        }
        ResetWindow();
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------
// simple controls for fixed-wing aircraft: the game's literal 50f in the two control methods becomes the configured threshold

[HarmonyPatch]
internal static class FixedWing_SimplePhysics_Patch
{
    internal static float Threshold()
    {
        float v = Performance.SimplePhysicsAbove?.Value ?? 0f;
        return Plugin.Active && v > 0f ? v : 50f;
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(FixedWingFlightPhysics), "CalculateControls");
        yield return AccessTools.Method(typeof(FixedWingFlightPhysics), "CalculateQuaternionControls");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getTc = AccessTools.PropertyGetter(typeof(GameTime), nameof(GameTime.TimeCompression));
        var threshold = AccessTools.Method(typeof(FixedWing_SimplePhysics_Patch), nameof(Threshold));
        CodeInstruction prev = null;
        int replaced = 0;
        foreach (CodeInstruction ins in instructions)
        {
            // "GameTime.TimeCompression <= 50f"  ->  "<= Threshold()"
            if (prev != null && prev.Calls(getTc) && ins.opcode == System.Reflection.Emit.OpCodes.Ldc_R4 && ins.operand is float f && f == 50f)
            {
                replaced++;
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, threshold);
            }
            else
                yield return ins;
            prev = ins;
        }
        // expected: exactly one per method (2 methods)
        Plugin.Log.LogInfo($"Fixed-wing simple controls: replaced {replaced} x '50x' threshold in one control method (expected 1).");
    }
}
