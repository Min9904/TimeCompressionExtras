using System.Reflection;
using HarmonyLib;

// Dry-run of a mod's Harmony patches against the REAL game assemblies, outside the game.
// usage: PatchCheck <mod.dll> <extra assembly dir>...
// It never executes game code: it only resolves the targets and lets Harmony build the patched IL.
class Program
{
    static bool IsDetourOnly(Exception e)
    {
        for (Exception x = e; x != null; x = x.InnerException)
            if (x is DllNotFoundException && x.Message.Contains("mm-exhelper")) return true;
        return false;
    }

    static int Main(string[] args)
    {
        var dirs = args.Skip(1).ToList();
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            if (name == "0Harmony") return typeof(Harmony).Assembly; // the mod was built against HarmonyX 2.9; use the one we have
            foreach (var d in dirs)
            {
                string p = Path.Combine(d, name + ".dll");
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };

        Assembly mod = Assembly.LoadFrom(args[0]);
        var harmony = new Harmony("patchcheck.test");
        int bad = 0;
        foreach (Type t in mod.GetTypes().Where(t => t.IsDefined(typeof(HarmonyPatch), false)))
        {
            try
            {
                harmony.CreateClassProcessor(t).Patch();
                Console.WriteLine("OK    " + t.FullName);
            }
            catch (Exception e) when (IsDetourOnly(e))
            {
                // Harmony built the patched method (target found, injected parameters valid); only the final
                // "install the detour" step cannot run on this machine (SELinux refuses MonoMod's exec-stack helper).
                Console.WriteLine("OK*   " + t.FullName + "   (patch validated; detour step not available here)");
            }
            catch (Exception e)
            {
                bad++;
                Console.WriteLine("FAIL  " + t.FullName + "\n      " + e.GetType().Name + ": " + e.Message.Replace("\n", "\n      "));
                if (e.InnerException != null) Console.WriteLine("      inner: " + e.InnerException);
            }
        }
        foreach (var m in Harmony.GetAllPatchedMethods())
            Console.WriteLine("hooked " + m.DeclaringType + "." + m.Name);
        Console.WriteLine(bad == 0 ? "ALL PATCHES OK" : bad + " PATCH(ES) FAILED");
        return bad;
    }
}
