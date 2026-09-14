using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Core.Modding;
using Game.Orchestration;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Reaches the game's mod loader and describes what it has loaded.
///
/// `GameModdingFramework` is bound in the game session's dependency container, and it holds
/// the `ModLoader`. Both the field and the loader's lists are private, which the publicizer
/// makes reachable.
/// </summary>
public class ModRegistry
{
    private readonly ILogger Logger;

    private GameSessionOrchestrator Orchestrator;
    private ModLoader Loader;

    public ModRegistry(ILogger logger)
    {
        Logger = logger;
    }

    public void Capture(GameSessionOrchestrator orchestrator)
    {
        if (!ReferenceEquals(Orchestrator, orchestrator))
        {
            Orchestrator = orchestrator;
            Loader = null;
        }
    }

    /// <summary>
    /// The mod loader - from the session when there is one, and from the game-level
    /// container when there is not.
    ///
    /// That second route is not a fallback for tidiness. A mod that throws while the
    /// session is loading never reaches <c>OnSessionReady</c>, so the session route has
    /// captured nothing at precisely the moment a reload is worth most - which is the case
    /// <see cref="CrashScreenReload"/> exists for. <c>ModLoadingBlindStep</c> binds the
    /// framework into <c>GameOrchestrator.InitializationDependencyContainer</c>, which
    /// lives for the process rather than the session, and holds the same loader.
    /// </summary>
    public bool TryGetLoader(out ModLoader loader)
    {
        if (Loader == null && Orchestrator != null)
        {
            try
            {
                Loader = Orchestrator.DependencyContainer
                    .Resolve<GameModdingFramework>()
                    .ModLoader;
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
            }
        }

        if (Loader == null)
        {
            try
            {
                GameOrchestrator game = GameBootstrapper.GameOrchestrator;

                if (game != null)
                {
                    Loader = game.InitializationDependencyContainer
                        .Resolve<GameModdingFramework>()
                        .ModLoader;
                }
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
            }
        }

        loader = Loader;
        return loader != null;
    }

    /// <summary>
    /// The session's debug console, from the same container Shifter resolves it out of.
    /// Not cached: the console is built per savegame, so a stale one would silently
    /// register commands nobody can reach.
    /// </summary>
    public bool TryGetConsole(out IDebugConsole console)
    {
        console = null;

        if (Orchestrator == null)
        {
            return false;
        }

        try
        {
            console = Orchestrator.DependencyContainer.Resolve<IDebugConsole>();
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }

        return console != null;
    }

    /// <summary>
    /// The live session, which <see cref="SessionRecycler"/> needs in order to save it and
    /// re-enter it. Held rather than resolved, because the orchestrator is what hands out
    /// everything else - including the navigator that replaces it.
    /// </summary>
    public bool TryGetSession(out GameSessionOrchestrator orchestrator)
    {
        orchestrator = Orchestrator;
        return orchestrator != null;
    }

    /// <summary>
    /// Whether a mod built around the named assembly is already running, and from where.
    /// </summary>
    public bool TryGetRunningAssembly(string assemblyName, out string directory)
    {
        directory = null;

        if (!TryGetLoader(out ModLoader loader) || string.IsNullOrEmpty(assemblyName))
        {
            return false;
        }

        foreach (ExecutableMod mod in loader.ExecutableMods)
        {
            Type entry = mod.EntryPoint.GetType();

            if (!string.Equals(entry.Assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            directory = TryResolveFor(mod, out ResolvedMod resolved)
                ? resolved.Descriptor.DirectoryPath
                : entry.Assembly.GetName().Name;

            return true;
        }

        return false;
    }

    /// <summary>
    /// One line per loaded mod, showing every name that mrl.reload will accept - so the
    /// listing doubles as the answer to "what do I type".
    /// </summary>
    public IEnumerable<string> Describe()
    {
        if (!TryGetLoader(out ModLoader loader))
        {
            return new[] { "The mod loader is not reachable yet - load a save first." };
        }

        List<string> lines = new List<string>
        {
            loader.ExecutableMods.Count + " mods loaded, "
                + loader.ResolvedMods.Count + " resolved"
        };

        foreach (ExecutableMod mod in loader.ExecutableMods)
        {
            Type entry = mod.EntryPoint.GetType();
            lines.Add("  " + entry.Name + "  (v" + mod.Metadata.Version + ")");

            foreach (string name in NamesFor(mod))
            {
                lines.Add("      matches: " + name);
            }
        }

        return lines;
    }

    /// <summary>Every string that should identify this mod on the command line.</summary>
    private IEnumerable<string> NamesFor(ExecutableMod mod)
    {
        List<string> names = new List<string>();

        Type entry = mod.EntryPoint.GetType();
        names.Add(entry.Name);
        names.Add(entry.Assembly.GetName().Name);

        if (TryResolveFor(mod, out ResolvedMod resolved))
        {
            if (resolved.Descriptor.ModId != null)
            {
                names.Add(resolved.Descriptor.ModId.ToString());
            }

            if (!string.IsNullOrEmpty(resolved.Descriptor.ModTitle))
            {
                names.Add(resolved.Descriptor.ModTitle);
            }

            string folder = FolderName(resolved.Descriptor.DirectoryPath);
            if (!string.IsNullOrEmpty(folder))
            {
                names.Add(folder);
            }
        }

        List<string> unique = new List<string>();
        foreach (string name in names)
        {
            if (!string.IsNullOrWhiteSpace(name) && !unique.Contains(name))
            {
                unique.Add(name);
            }
        }

        return unique;
    }

    /// <summary>
    /// Correlates a running mod with its resolved definition, which is the only place the
    /// directory path lives. Matching on the manifest instance is the direct route; the
    /// assembly name is the fallback in case the loader rebuilt the manifest.
    /// </summary>
    private bool TryResolveFor(ExecutableMod mod, out ResolvedMod resolved)
    {
        resolved = default(ResolvedMod);

        if (!TryGetLoader(out ModLoader loader))
        {
            return false;
        }

        foreach (ResolvedMod candidate in loader.ResolvedMods)
        {
            if (ReferenceEquals(candidate.Metadata, mod.Metadata))
            {
                resolved = candidate;
                return true;
            }
        }

        string assembly = mod.EntryPoint.GetType().Assembly.GetName().Name + ".dll";

        foreach (ResolvedMod candidate in loader.ResolvedMods)
        {
            string[] assemblies = candidate.Metadata.Assemblies ?? Array.Empty<string>();

            foreach (string declared in assemblies)
            {
                if (string.Equals(declared, assembly, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static string FolderName(string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            return null;
        }

        // A trailing separator makes GetFileName return an empty string.
        return Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    /// <summary>
    /// Finds a running mod by any of the names <see cref="Describe"/> lists, matching case
    /// insensitively. Ambiguity is refused rather than guessed, so a partial name cannot
    /// reload something unintended.
    /// </summary>
    public bool TryFind(string name, out ResolvedMod resolved, out ExecutableMod executable, out string problem)
    {
        resolved = default(ResolvedMod);
        executable = default(ExecutableMod);
        problem = null;

        if (!TryGetLoader(out ModLoader loader))
        {
            problem = "The mod loader is not reachable yet - load a save first.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            problem = "Name a mod. Try mrl.list.";
            return false;
        }

        // Search the running mods, not the resolved list: a resolved mod without a running
        // entry point cannot be reloaded anyway, and this keeps both halves in step.
        List<ExecutableMod> matches = new List<ExecutableMod>();
        List<string> available = new List<string>();

        foreach (ExecutableMod candidate in loader.ExecutableMods)
        {
            bool matched = false;

            foreach (string candidateName in NamesFor(candidate))
            {
                available.Add(candidateName);

                if (candidateName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matched = true;
                }
            }

            if (matched)
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count == 0)
        {
            problem = "No running mod matches \"" + name + "\". Known names: "
                + string.Join(", ", available.ToArray());
            return false;
        }

        if (matches.Count > 1)
        {
            problem = "\"" + name + "\" matches " + matches.Count + " mods - be more specific.";
            return false;
        }

        executable = matches[0];

        if (!TryResolveFor(executable, out resolved))
        {
            problem = "Found the running mod but not its folder on disk, so there is nothing to reload from.";
            return false;
        }

        return true;
    }

    /// <summary>Replaces a mod's running entry point in the loader's own list.</summary>
    public void Replace(ExecutableMod old, IMod entryPoint)
    {
        if (!TryGetLoader(out ModLoader loader))
        {
            return;
        }

        List<ExecutableMod> mods = loader._ExecutableMods;

        for (int i = 0; i < mods.Count; i++)
        {
            if (ReferenceEquals(mods[i].EntryPoint, old.EntryPoint))
            {
                mods[i] = new ExecutableMod(old.Metadata, entryPoint);
                return;
            }
        }
    }
}
