using System;
using AnchorChain;
using UnityEngine;

namespace MainMod;

[ACPlugin(Entry.Guid, "Time compression extras", "0.8.0")]
public class Entry : IAnchorChainMod
{
    public const string Guid = "local.mainmod";

    public void TriggerEntryPoint()
    {
        try
        {
            Plugin.Init();
        }
        catch (Exception e)
        {
            // Both logs: the Unity log and BepInEx/LogOutput.log (the one that is easy to read).
            Debug.LogError($"[MainMod] failed to initialize: {e}");
            try { BepInEx.Logging.Logger.CreateLogSource("MainMod").LogError($"Failed to initialize: {e}"); } catch { }
        }
    }
}
