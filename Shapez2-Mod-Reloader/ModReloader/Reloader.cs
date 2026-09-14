using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Core.Dependency;
using Game.Core.Modding;
using ILogger = Core.Logging.ILogger;
using PrefixedLogger = Core.Logging.PrefixedLogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Swaps a mod's running code for a freshly built assembly and rebuilds the game session
/// around it, without restarting the game.
///
/// The session rebuild is what makes a reload cover new content - toolbars, panels,
/// islands, meshes - rather than only new code; see <see cref="SessionRecycler"/> for why
/// none of that can be swapped in place.
///
/// The awkward part is that an assembly loaded into Unity's Mono runtime can never be
/// unloaded, and the game loads mods with `Assembly.LoadFrom`, which binds by assembly
/// identity - name, version, culture, public key - rather than by path. A rebuilt mod
/// keeps the same identity (nothing bumps the version between builds), so `LoadFrom`
/// returns the copy already in memory and never reads the new file, whatever path it is
/// handed. So each reload instead reads the rebuilt DLL into a byte array and loads it
/// with `Assembly.Load(byte[])`, which has no such context and always produces a
/// genuinely distinct assembly from the new bytes.
///
/// The consequences are unavoidable rather than incidental, and worth knowing:
///
/// - **Every reload leaks an assembly.** Fine for a development loop, never for shipping.
/// - **Old and new types are different types.** Anything the game still holds from the old
///   assembly keeps the old code alive.
/// - **A mod is only as reloadable as its Dispose.** Whatever it registered and did not
///   undo - a HUD element, an event subscription, a lane hook - survives the reload and
///   then exists twice.
/// </summary>
public class Reloader
{
    private readonly ILogger Logger;
    private readonly ModRegistry Registry;
    private readonly SessionRewiring Rewiring;
    private readonly SessionRecycler Recycler;

    private int Generation;

    public Reloader(ILogger logger, ModRegistry registry, SessionRewiring rewiring,
        SessionRecycler recycler)
    {
        Logger = logger;
        Registry = registry;
        Rewiring = rewiring;
        Recycler = recycler;
    }

    /// <summary>A mod that can be reloaded, resolved before anything is disposed.</summary>
    private class Target
    {
        public readonly ResolvedMod Resolved;
        public readonly ExecutableMod Executable;

        public Target(ResolvedMod resolved, ExecutableMod executable)
        {
            Resolved = resolved;
            Executable = executable;
        }
    }

    /// <summary>The pair of assemblies a swap produced, for the rewiring fallback.</summary>
    private class Swapped
    {
        public readonly Assembly Previous;
        public readonly Assembly Current;

        public Swapped(Assembly previous, Assembly current)
        {
            Previous = previous;
            Current = current;
        }
    }

    /// <summary>One mod by name - the everyday case of the batch below.</summary>
    public IEnumerable<string> Reload(string name)
    {
        return Reload(new List<string> { name });
    }

    /// <summary>
    /// Swaps each named mod's code, then rebuilds the session so the new code's content
    /// takes effect.
    ///
    /// The save and the session reload straddle the swap, and have to: the old instance is
    /// the only code that can still write its own save data, and the new session has to be
    /// built by the new code. Everything is resolved before either, so a mistyped name
    /// costs nothing.
    ///
    /// A batch saves once and rebuilds the session once, however many mods it covers,
    /// because a solution-wide build stages all of them at the same moment.
    /// </summary>
    public IEnumerable<string> Reload(IList<string> names)
    {
        List<string> report = new List<string>();
        List<Target> targets = new List<Target>();

        foreach (string name in names)
        {
            if (TryResolve(name, report, out Target target))
            {
                targets.Add(target);
            }
        }

        if (targets.Count == 0)
        {
            return report;
        }

        SessionRecycler.Handle saved = Recycler.TrySave(report);
        List<Swapped> swapped = new List<Swapped>();

        foreach (Target target in targets)
        {
            if (Swap(target, report, out Swapped result))
            {
                swapped.Add(result);
            }
        }

        if (swapped.Count == 0)
        {
            report.Add("Nothing was reloaded, so the session is left as it is.");
            return report;
        }

        if (saved != null && Recycler.TryReload(saved, report))
        {
            // Nothing left for SessionRewiring to replay: the rebuilt session runs every
            // one-shot init callback again, which is the whole point of rebuilding it.
            report.Add("Reloaded. Anything an old instance did not undo in Dispose now exists twice,");
            report.Add("and the rebuilt session applies it a second time.");
            return report;
        }

        // No session was rebuilt, so the callbacks that only fire at init have been and
        // gone. Put the new code back in front of them by hand instead.
        foreach (Swapped result in swapped)
        {
            Rewiring.Replay(result.Previous, result.Current, report);
        }

        report.Add("Reloaded code only - new content (toolbars, panels, islands, meshes) appears");
        report.Add("when the session is next built.");
        return report;
    }

    /// <summary>
    /// Finds a named mod and rules out what cannot be reloaded at all - before the save,
    /// so that none of those cases can cost a session rebuild.
    /// </summary>
    private bool TryResolve(string name, List<string> report, out Target target)
    {
        target = null;

        if (!Registry.TryFind(name, out ResolvedMod resolved, out ExecutableMod executable,
                out string problem))
        {
            report.Add(problem);
            return false;
        }

        if (executable.EntryPoint.GetType().Assembly == typeof(Reloader).Assembly)
        {
            report.Add("Refusing to reload the reloader - that would dispose the code doing the reloading.");
            return false;
        }

        if ((resolved.Metadata.Assemblies ?? Array.Empty<string>()).Length == 0)
        {
            report.Add(resolved.Descriptor.ModTitle + " declares no assemblies to reload.");
            return false;
        }

        target = new Target(resolved, executable);
        return true;
    }

    /// <summary>Replaces one mod's running code with its rebuilt assembly.</summary>
    private bool Swap(Target target, List<string> report, out Swapped swapped)
    {
        swapped = null;

        ResolvedMod resolved = target.Resolved;
        ExecutableMod executable = target.Executable;
        Assembly previous = executable.EntryPoint.GetType().Assembly;

        string[] assemblies = resolved.Metadata.Assemblies ?? Array.Empty<string>();
        string directory = ResolveSource(resolved, report);

        Generation++;
        report.Add("Reloading " + resolved.Descriptor.ModTitle + " (generation " + Generation + ")");

        // 1. Let the old instance undo whatever it did. Everything it fails to undo will
        //    now exist twice, which is the usual cause of a confusing reload.
        try
        {
            executable.EntryPoint.Dispose();
            report.Add("  disposed the old entry point");
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.Add("  the old entry point threw while disposing - see the log; continuing");
        }

        // 2. Load and find the single IMod.
        //
        //    NOT with Assembly.LoadFrom, which the game uses and which is exactly what makes
        //    a second load impossible: LoadFrom binds by assembly identity - name, version,
        //    culture, public key - not by path. A rebuilt mod keeps the same identity
        //    (nothing bumps the assembly version between builds), so LoadFrom finds the
        //    original already in memory and hands it straight back, reading nothing. The
        //    reload would then reconstruct the *old* type and re-register the old code,
        //    which looks exactly like "the new version did not load".
        //
        //    Assembly.Load(byte[]) has no such context: it takes the raw image and always
        //    produces a genuinely distinct assembly, even when the identity is unchanged.
        //    Reading the bytes ourselves also sidesteps the file lock - the installed copy
        //    is memory-mapped and cannot be overwritten, but it can still be read, and a
        //    staged mods-dev build is not locked at all.
        Type bootstrap;
        try
        {
            List<Type> types = new List<Type>();

            foreach (string assemblyName in assemblies)
            {
                byte[] image = File.ReadAllBytes(Path.Combine(directory, assemblyName));
                Assembly loaded = Assembly.Load(image);
                types.AddRange(LoadableTypes(loaded));
            }

            List<Type> entries = types
                .Where(type => typeof(IMod).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                .ToList();

            if (entries.Count != 1)
            {
                report.Add("  expected exactly one IMod implementation, found " + entries.Count);
                return false;
            }

            bootstrap = entries[0];
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.Add("  could not load the rebuilt assembly - see the log.");
            return false;
        }

        // 3. Construct it exactly as ModLoader does: a container with ILogger bound.
        try
        {
            string prefix = resolved.Descriptor.ModId + "[" + resolved.Metadata.Version + "]";

            using (DependencyContainer container = new DependencyContainer())
            {
                container.Bind<Core.Logging.ILogger>().To(new PrefixedLogger(Logger, prefix));
                IMod instance = (IMod)container.Create(bootstrap);

                Registry.Replace(executable, instance);
                report.Add("  constructed " + bootstrap.Name + " from the new assembly");
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.Add("  the new entry point threw while constructing - see the log. That mod is now not running.");
            return false;
        }

        swapped = new Swapped(previous, bootstrap.Assembly);
        return true;
    }

    /// <summary>
    /// Where to reload from.
    ///
    /// The copy in the mods folder is memory-mapped the moment the game loads it, so a
    /// build can never overwrite it while the game runs - which would leave the reloader
    /// with nothing new to read. The way out is to build somewhere else: a "mods-dev"
    /// folder beside the mods folder, which mod discovery does not scan (it only
    /// enumerates immediate subdirectories of "mods"), so a staged build is never loaded
    /// as a second mod.
    /// </summary>
    private string ResolveSource(ResolvedMod resolved, List<string> report)
    {
        string installed = resolved.Descriptor.DirectoryPath;
        string folder = Path.GetFileName(
            installed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        string staged = Path.Combine(StagedBuildWatcher.Root, folder);

        if (Directory.Exists(staged))
        {
            report.Add("  source: " + staged + " (staged " + BuiltWhen(staged, resolved) + ")");
            return staged;
        }

        report.Add("  source: the installed folder - which the game has locked, so this will");
        report.Add("          reload the same bytes. Build with -p:Dev=true to stage instead.");
        return installed;
    }

    /// <summary>
    /// When the staged build was produced, so a stale one is obvious rather than
    /// mystifying - reloading old bytes looks exactly like a reload that did nothing.
    /// </summary>
    private static string BuiltWhen(string staged, ResolvedMod resolved)
    {
        try
        {
            string[] assemblies = resolved.Metadata.Assemblies ?? Array.Empty<string>();
            if (assemblies.Length == 0)
            {
                return "unknown";
            }

            string file = Path.Combine(staged, assemblies[0]);
            if (!File.Exists(file))
            {
                return "no assembly present";
            }

            TimeSpan age = DateTime.Now - File.GetLastWriteTime(file);
            return age.TotalMinutes < 1
                ? "seconds ago"
                : (int)age.TotalMinutes + " minutes ago";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null);
        }
    }
}
