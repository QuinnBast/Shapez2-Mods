using System;
using System.Collections.Generic;
using System.Reflection;

namespace QuinnBast.Shapez2.ModProfiler;

/// <summary>
/// Which loaded assemblies are mods.
///
/// The test is "declares a concrete <c>IMod</c>", which is what Shapez Shifter itself loads
/// by - more reliable than a name or a folder, and it excludes the game and Unity without a
/// blocklist that would need keeping current.
///
/// Cached, because asking it means walking every type in every loaded assembly and the game's
/// own assembly alone is tens of thousands of types. The cache is rebuilt when the AppDomain
/// gains an assembly, which is what a hot reload looks like from in here.
/// </summary>
public static class ModAssemblies
{
    private static readonly HashSet<Assembly> Mods = new HashSet<Assembly>();
    private static readonly List<string> Names = new List<string>();

    private static int SeenAssemblies;

    /// <summary>Mod assembly names, sorted, for a picker.</summary>
    public static IReadOnlyList<string> All
    {
        get
        {
            Refresh();
            return Names;
        }
    }

    public static bool IsMod(Assembly assembly)
    {
        Refresh();

        return assembly != null && Mods.Contains(assembly);
    }

    public static Assembly Find(string name)
    {
        Refresh();

        foreach (Assembly candidate in Mods)
        {
            if (string.Equals(candidate.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void Refresh()
    {
        Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();

        if (loaded.Length == SeenAssemblies)
        {
            return;
        }

        SeenAssemblies = loaded.Length;
        Mods.Clear();
        Names.Clear();

        foreach (Assembly assembly in loaded)
        {
            try
            {
                foreach (Type type in assembly.GetTypes())
                {
                    if (typeof(IMod).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                    {
                        Mods.Add(assembly);
                        Names.Add(assembly.GetName().Name);
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // An assembly whose types will not all load cannot be identified either way.
            }
        }

        Names.Sort(StringComparer.OrdinalIgnoreCase);
    }
}
