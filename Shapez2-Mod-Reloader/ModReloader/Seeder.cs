using System;
using System.Collections.Generic;
using System.IO;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Installs staged builds that have never been installed.
///
/// A mod built with <c>-p:Dev=true</c> lands in "mods-dev", which mod discovery does not
/// scan - deliberately, so a staged build is never loaded twice. The catch is the first
/// build of a brand new mod: there is nothing in "mods" to reload, so the staged build is
/// invisible to the game and to <c>mrl.reload</c>. This copies it across.
///
/// It cannot take effect in the session that does the copying. Discovery has already run
/// by the time any mod's code executes - including this one's - so the seeded mod is picked
/// up on the next launch. That is the point of doing it this way rather than loading the
/// assembly here: the game then loads it as an ordinary installed mod, and so applies the
/// usual manifest, game-version and dependency checks and reports a failure in the mod list
/// the same way it would for anything else. Loading it in-session would mean skipping all
/// of that, and a mod whose manifest is wrong would appear to work until someone else
/// installed it.
/// </summary>
public class Seeder
{
    private readonly ILogger Logger;
    private readonly ModRegistry Registry;

    public Seeder(ILogger logger, ModRegistry registry)
    {
        Logger = logger;
        Registry = registry;
    }

    public IEnumerable<string> Seed()
    {
        List<string> report = new List<string>();

        string dev = Path.Combine(GameEnvironment.DataPath, "mods-dev");
        string mods = Path.Combine(GameEnvironment.DataPath, "mods");

        if (!Directory.Exists(dev))
        {
            report.Add("No mods-dev folder yet: " + dev);
            report.Add("Build with -p:Dev=true to stage a build there.");
            return report;
        }

        string[] staged;

        try
        {
            staged = Directory.GetDirectories(dev);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.Add("Could not read " + dev + " - see the log.");
            return report;
        }

        int seeded = 0;

        foreach (string folder in staged)
        {
            string name = Path.GetFileName(folder);
            string target = Path.Combine(mods, name);

            if (Directory.Exists(target))
            {
                // Already installed, so the game found it and mrl.reload will prefer the
                // staged build anyway. Overwriting here would fail regardless: the running
                // game has the installed assembly memory-mapped.
                continue;
            }

            if (!File.Exists(Path.Combine(folder, "manifest.json")))
            {
                report.Add(name + ": no manifest.json in the staged build - skipped.");
                continue;
            }

            // The mod may already be running from somewhere else - a workshop
            // subscription, most likely. Installing a second copy would give the loader
            // two mods with the same id, and it would then refuse one of them. Somebody
            // who deleted their local copy to test the published one should not have it
            // quietly put back.
            if (AlreadyRunning(folder, out string where))
            {
                report.Add(name + ": already running from " + where + " - not installing a second copy.");
                continue;
            }

            try
            {
                Copy(folder, target);
                seeded++;
                report.Add("Seeded " + name + " into mods.");
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
                report.Add(name + ": could not copy into mods - see the log.");
            }
        }

        if (seeded > 0)
        {
            report.Add("Restart the game to load " + (seeded == 1 ? "it" : "them")
                + " - discovery runs before any mod does, so this session cannot pick "
                + (seeded == 1 ? "it" : "them") + " up. If the manifest is wrong the game "
                + "will say so in the mod list on that launch. After that, mrl.reload works.");
        }
        else if (report.Count == 0)
        {
            report.Add("Nothing to seed - every staged build is already installed.");
        }

        return report;
    }

    /// <summary>
    /// Whether one of the assemblies in a staged build is already loaded, whatever folder
    /// it came from. Matching on the assembly rather than the folder name is what catches
    /// the workshop copy, whose folder is a numeric id.
    /// </summary>
    private bool AlreadyRunning(string folder, out string where)
    {
        where = null;

        try
        {
            foreach (string file in Directory.GetFiles(folder, "*.dll"))
            {
                string assembly = Path.GetFileNameWithoutExtension(file);

                if (Registry.TryGetRunningAssembly(assembly, out where))
                {
                    return true;
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }

        return false;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (string file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.GetDirectories(from))
        {
            Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }
}
