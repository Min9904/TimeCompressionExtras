using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;

namespace MainMod;

/// <summary>
/// Raises the native time-compression cap.
///
/// The game has two separate limits:
///  1. GameTime._maxTimeCompression (config.ini [General] MaxTimeCompression, 100) - the highest step the
///     - / = keys can reach. Re-read from config.ini on every GameTime.OnStart, so we patch after it.
///     (Dynamic campaigns set it to 3000 themselves; we never lower it.)
///  2. Globals._weaponsTCLimit (20) / _gunsTCLimit (30) / _flightDeckTCLimit (30) - when a weapon is
///     launched / a gun fires / the flight deck is busy the game calls GameTime.SetTimeCompression(limit),
///     dropping the speed to that value if it is higher.
/// </summary>
[HarmonyPatch(typeof(GameTime), nameof(GameTime.OnStart))]
internal static class GameTime_OnStart_Patch
{
    private static bool _captured;
    private static float _vanillaWeapons = 20f, _vanillaGuns = 30f, _vanillaFlightDeck = 30f;

    /// <summary>The game's own limits (read before the mod touched them); used to show what the mod raised or lifted.</summary>
    internal static float VanillaWeapons => _vanillaWeapons;
    internal static float VanillaGuns => _vanillaGuns;
    internal static float VanillaFlightDeck => _vanillaFlightDeck;

    private static void Postfix(bool isDynamicCampaign)
    {
        try
        {
            // The very first call happens before we touched anything, so these are the game's own values.
            if (!_captured)
            {
                _captured = true;
                _vanillaWeapons = Globals._weaponsTCLimit;
                _vanillaGuns = Globals._gunsTCLimit;
                _vanillaFlightDeck = Globals._flightDeckTCLimit;
            }

            AirOps.Reset();
            Combat.Reset();
            Plugin.ReloadConfig();
            Plugin.Active = Plugin.Enabled.Value && Plugin.EnabledInModsMenu();
            if (!Plugin.Active)
            {
                // Mod was unticked since the last mission: put everything back the way the game has it.
                Globals._weaponsTCLimit = _vanillaWeapons;
                Globals._gunsTCLimit = _vanillaGuns;
                Globals._flightDeckTCLimit = _vanillaFlightDeck;
                AirOps.FlightDeckBaseLimit = _vanillaFlightDeck;
                Plugin.Log.LogInfo(Plugin.Enabled.Value
                    ? "Time compression extras is not ticked in the Mods menu; nothing changed for this mission."
                    : "Time compression extras is switched off in MainMod.cfg; nothing changed for this mission.");
                Performance.OnMissionStart();
                return;
            }

            ApplyMax();
            if (Plugin.SlowDownInCombat.Value)
            {
                // The proximity rule (Combat.cs) replaces "any weapon anywhere -> 20x"; guns / CIWS engagements are always local.
                Globals._weaponsTCLimit = 100000f;
                Globals._gunsTCLimit = Combat.Speed;
                Plugin.Log.LogInfo($"Combat slowdown: munitions within {Plugin.CombatRadiusNm.Value:0} nm of a unit, guns / CIWS and aircraft attack runs -> {Combat.Speed:0}x " +
                                   $"(the game's weapons-anywhere limit of {_vanillaWeapons:0}x is lifted).");
            }
            else
            {
                // the game's own rules, untouched
                Globals._weaponsTCLimit = _vanillaWeapons;
                Globals._gunsTCLimit = _vanillaGuns;
            }
            // The flight-deck limit is re-written every frame by InstantLaunch (it is lifted while only instant
            // launches are pending); this is the value it falls back to for real deck operations.
            AirOps.FlightDeckBaseLimit = Raised(_vanillaFlightDeck, Plugin.FlightDeckLimit.Value, "flight deck");
            Globals._flightDeckTCLimit = AirOps.FlightDeckBaseLimit;
            Performance.OnMissionStart();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError("Applying time-compression cap failed: " + e);
        }
    }

    private static void ApplyMax()
    {
        float wanted = Plugin.MaxCompression.Value;
        var maxField = AccessTools.Field(typeof(GameTime), "_maxTimeCompression");
        var idxField = AccessTools.Field(typeof(GameTime), "_maxTimeCompressionIndex");
        var ladder = (List<float>)AccessTools.Field(typeof(GameTime), "_timeScaleValues").GetValue(null);

        float current = (float)maxField.GetValue(null);
        if (wanted <= current)
        {
            Plugin.Log.LogInfo($"Max compression stays {current}x (wanted {wanted}x).");
            return;
        }
        maxField.SetValue(null, wanted);

        // Same loop the game uses: index ends on the first step ABOVE the max (IncreaseTimeCompression
        // clamps to index-1) or on the last step if none is above.
        int index = 0;
        for (int i = 0; i < ladder.Count; i++)
        {
            index = i;
            if (!(ladder[i] <= wanted)) break;
        }
        idxField.SetValue(null, index);
        float top = ladder[Math.Max(0, index == ladder.Count - 1 && ladder[index] <= wanted ? index : index - 1)];
        Plugin.Log.LogInfo($"Max compression raised {current}x -> {top}x (ladder index {index}).");
    }

    private static float Raised(float vanilla, float wanted, string name)
    {
        if (wanted <= 0f || wanted <= vanilla) return vanilla;
        Plugin.Log.LogInfo($"Auto-slowdown for {name} raised {vanilla}x -> {wanted}x.");
        return wanted;
    }
}
