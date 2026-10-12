using System.Reflection;

// Loads the mod DLL outside the game, binds its config exactly like the game would, prints every value,
// then edits the file and checks that ReloadConfig() picks the change up.
class Program
{
    static int Main(string[] args)
    {
        string modDll = args[0];
        var dirs = args.Skip(1).ToList();
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string n = new AssemblyName(e.Name).Name;
            foreach (var d in dirs) { string p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
            return null;
        };
        Assembly mod = Assembly.LoadFrom(modDll);
        Type plugin = mod.GetType("MainMod.Plugin");
        BindingFlags all = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        Console.WriteLine("config path: " + plugin.GetProperty("ConfigPath", all).GetValue(null));
        plugin.GetMethod("BindConfig", all).Invoke(null, null);
        Dump(plugin, all);

        string path = (string)plugin.GetProperty("ConfigPath", all).GetValue(null);
        string text = File.ReadAllText(path);
        Console.WriteLine("--- editing MaxCompression 500 -> 1234 and InstantLaunch true -> false, then ReloadConfig()");
        File.WriteAllText(path, text.Replace("MaxCompression = 500", "MaxCompression = 1234").Replace("InstantLaunch = true", "InstantLaunch = false"));
        plugin.GetMethod("ReloadConfig", all).Invoke(null, null);
        Dump(plugin, all, "MaxCompression", "InstantLaunch");
        File.WriteAllText(path, text); // restore
        return 0;
    }

    static void Dump(Type plugin, BindingFlags all, params string[] only)
    {
        foreach (var f in plugin.GetFields(all).Where(f => f.FieldType.Name.StartsWith("ConfigEntry")))
        {
            if (only.Length > 0 && !only.Contains(f.Name)) continue;
            object entry = f.GetValue(null);
            object val = entry.GetType().GetProperty("Value").GetValue(entry);
            Console.WriteLine($"  {f.Name,-30} = {val}");
        }
    }
}
