using System;
using System.Collections.Generic;
using System.IO;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Watches the staged build folder and reloads whatever was rebuilt, so the loop becomes
/// "build" rather than "build, alt-tab, type".
///
/// Two things make this more than a file event and a call to <see cref="Reloader"/>:
///
/// - **A build is not one write.** MSBuild writes the assembly, its symbols, the manifest
///   and whatever else the project copies, over some tens of milliseconds. Reloading on
///   the first of them would read a half-written assembly, so a change only counts once
///   the folder has been quiet for <see cref="Settle"/>.
/// - **File events do not arrive on the main thread.** <see cref="FileSystemWatcher"/>
///   raises them on a thread pool thread, where nothing may touch Unity - and a reload
///   loads assemblies, rebuilds a session and unloads asset bundles. So the event only
///   records what changed, and <see cref="Pump"/> does the work from the session tick.
/// </summary>
public class StagedBuildWatcher : IDisposable
{
    /// <summary>Where <c>dotnet build -p:Dev=true</c> stages a mod.</summary>
    public static string Root => Path.Combine(GameEnvironment.DataPath, "mods-dev");

    /// <summary>
    /// Every folder staged for reload.
    ///
    /// Shared by the two places that reload without being given a name - the pause menu and
    /// the crash screen - because neither has anywhere to type one. Reload resolves each
    /// name and reports the ones that are not running mods, so a stale folder costs a line
    /// of report rather than a throw.
    /// </summary>
    public static List<string> StagedFolders()
    {
        List<string> folders = new List<string>();

        if (!Directory.Exists(Root))
        {
            return folders;
        }

        foreach (string path in Directory.GetDirectories(Root))
        {
            folders.Add(Path.GetFileName(path));
        }

        return folders;
    }

    /// How long the folder has to stay quiet before a build counts as finished.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(750);

    private readonly ILogger Logger;
    private readonly Reloader Reloader;
    private readonly ConsoleTap Tap;

    /// Folders touched since the last reload, and when the last touch was. Both are
    /// written from the watcher thread and read from the tick, so both are held under this
    /// lock rather than the other way round.
    private readonly HashSet<string> Changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private DateTime LastChange;

    private FileSystemWatcher Watcher;

    public StagedBuildWatcher(ILogger logger, Reloader reloader, ConsoleTap tap)
    {
        Logger = logger;
        Reloader = reloader;
        Tap = tap;
    }

    public bool Watching => Watcher != null;

    public IEnumerable<string> Toggle()
    {
        return Watching ? Stop() : Start();
    }

    private IEnumerable<string> Start()
    {
        List<string> report = new List<string>();

        if (!Directory.Exists(Root))
        {
            report.Add("Nothing staged to watch: " + Root + " does not exist.");
            report.Add("Build a mod with -p:Dev=true and it will appear.");
            return report;
        }

        try
        {
            Watcher = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            Watcher.Changed += OnChanged;
            Watcher.Created += OnChanged;
            Watcher.Renamed += OnChanged;
            Watcher.Error += OnError;
            Watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Stop();

            report.Add("Could not watch " + Root + " - see the log.");
            return report;
        }

        report.Add("Watching " + Root);
        report.Add("Rebuild a mod and it reloads itself, session and all. mrl.watch again to stop.");
        report.Add("Each reload prints its report to Player.log - and mrl.copy will still have it,");
        report.Add("which the rebuilt session would otherwise have wiped off the screen.");
        return report;
    }

    private IEnumerable<string> Stop()
    {
        try
        {
            if (Watcher != null)
            {
                Watcher.EnableRaisingEvents = false;
                Watcher.Changed -= OnChanged;
                Watcher.Created -= OnChanged;
                Watcher.Renamed -= OnChanged;
                Watcher.Error -= OnError;
                Watcher.Dispose();
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }

        Watcher = null;

        lock (Changed)
        {
            Changed.Clear();
        }

        return new[] { "No longer watching for staged builds." };
    }

    /// <summary>
    /// Reloads what has been rebuilt, once the writing has stopped. Called every frame
    /// from the session tick, so it must stay cheap when there is nothing to do - and must
    /// never throw, or it takes the game's tick down with it.
    /// </summary>
    public void Pump(float deltaTime)
    {
        List<string> folders = null;

        try
        {
            lock (Changed)
            {
                if (Changed.Count > 0 && DateTime.UtcNow - LastChange >= Settle)
                {
                    folders = new List<string>(Changed);
                    Changed.Clear();
                }
            }

            if (folders == null)
            {
                return;
            }

            List<string> report = new List<string> { "Staged build detected, reloading." };
            report.AddRange(Reloader.Reload(folders));

            foreach (string line in report)
            {
                Logger.Info?.Log(line);
            }

            // The rebuilt session takes the console's history with it, so put the report
            // where mrl.copy can still reach it.
            Tap.Capture(report);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        string folder = FolderFor(args.FullPath);

        if (folder == null)
        {
            return;
        }

        lock (Changed)
        {
            Changed.Add(folder);
            LastChange = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// The watcher gives up if its buffer overflows, which leaves it raising nothing and
    /// looking like a build that did not register.
    /// </summary>
    private void OnError(object sender, ErrorEventArgs args)
    {
        Logger.Exception?.LogException(args.GetException());
        Logger.Error?.Log("Staged build watching stopped - run mrl.watch twice to restart it.");
    }

    /// <summary>
    /// The mod folder a changed file belongs to. Staged builds are laid out as
    /// <c>mods-dev/&lt;folder&gt;/...</c>, and that folder name is one of the names
    /// <c>mrl.reload</c> accepts - so it can be handed straight to the reloader.
    /// </summary>
    private static string FolderFor(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= Root.Length)
        {
            return null;
        }

        string relative = path
            .Substring(Root.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        int separator = relative.IndexOfAny(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });

        // A file directly in mods-dev belongs to no mod, so there is nothing to reload.
        return separator <= 0 ? null : relative.Substring(0, separator);
    }

    public void Dispose()
    {
        if (Watching)
        {
            Stop();
        }
    }
}
