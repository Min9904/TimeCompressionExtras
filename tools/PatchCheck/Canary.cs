using HarmonyLib;
using SeaPower;

// Deliberately broken patches: they prove the dry-run really detects the kind of bug that broke version 0.2.0.
[HarmonyPatch(typeof(Spawn), "onUpdate")]
static class CanaryBadInjection
{
    static void Postfix(Spawn __instance, FlightDeckTask ___flightDeckTask) { }   // field is "_flightDeckTask" -> must fail
}

[HarmonyPatch(typeof(Spawn), "thisMethodDoesNotExist")]
static class CanaryBadTarget
{
    static void Postfix() { }
}

[HarmonyPatch(typeof(Spawn), "onUpdate")]
static class CanaryGoodInjection
{
    static void Postfix(Spawn __instance, FlightDeckTask ____flightDeckTask) { }   // correct: 3 underscores + "_flightDeckTask"
}
