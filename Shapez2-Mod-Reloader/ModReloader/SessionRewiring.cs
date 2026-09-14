using System;
using System.Collections.Generic;
using System.Reflection;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Replays the one-shot session callbacks a reloaded mod has already missed.
///
/// Shifter delivers most things through rewirers that are consulted every time they are
/// needed - a tick rewirer is enumerated each frame, so a reloaded mod's tick works
/// immediately. But three of them are invoked exactly once, during session init:
/// console commands, island side-panel modules and building side-panel modules. A mod
/// reloaded mid-session adds its rewirer to a registry nothing will read again, so its
/// commands and panels quietly stay as they were - still bound to the disposed instance.
/// The console case is the worst of the three, because the stale command still answers,
/// reading from the old mod's now-empty state, which looks exactly like a broken mod.
///
/// No mod can fix this for itself: at construction time there is no route from a mod to
/// the live console or to the module lookups, and by the time one exists the callback has
/// been and gone. The reloader can, because it holds the session orchestrator and hooks
/// the same two injection points Shifter does, so it has all three in hand.
/// </summary>
public class SessionRewiring
{
    private readonly ILogger Logger;
    private readonly ModRegistry Registry;

    private IslandsModulesLookup Islands;
    private BuildingsModulesLookup Buildings;

    public SessionRewiring(ILogger logger, ModRegistry registry)
    {
        Logger = logger;
        Registry = registry;
    }

    public void Capture(IslandsModulesLookup lookup)
    {
        Islands = lookup;
    }

    public void Capture(BuildingsModulesLookup lookup)
    {
        Buildings = lookup;
    }

    /// <summary>
    /// Puts the new assembly's rewirers back in front of the callbacks that already ran.
    /// Reported line by line into the reload output, because a silent no-op here is what
    /// made the problem hard to see in the first place.
    /// </summary>
    public void Replay(Assembly previous, Assembly current, List<string> report)
    {
        RewireConsole(previous, current, report);
        RewireIslandModules(current, report);
        RewireBuildingModules(current, report);
    }

    /// <summary>
    /// Drops the old assembly's commands and re-registers the new one's.
    ///
    /// Dropping first is not optional: the console registers into a dictionary with
    /// <c>Add</c>, so re-registering an id that is still present throws, and a mod that
    /// guards each registration - as it should - would swallow that and leave every one of
    /// its commands pointing at the disposed instance.
    /// </summary>
    private void RewireConsole(Assembly previous, Assembly current, List<string> report)
    {
        if (!Registry.TryGetConsole(out IDebugConsole resolved))
        {
            report.Add("  console: not reachable, so its commands are still the old ones");
            return;
        }

        if (!(resolved is DebugConsole console) || console.Commands == null)
        {
            report.Add("  console: unrecognised implementation, leaving its commands alone");
            return;
        }

        int dropped = 0;

        try
        {
            List<string> ids = new List<string>(console.Commands.Keys);

            foreach (string id in ids)
            {
                if (console.Commands.TryGetValue(id, out DebugConsole.Command command)
                    && BelongsTo(command?.Handler, previous))
                {
                    console.Commands.Remove(id);
                    dropped++;
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.Add("  console: could not drop the old commands - see the log");
            return;
        }

        int registered = 0;

        foreach (IConsoleRewirer rewirer in RewirersFrom<IConsoleRewirer>(current))
        {
            try
            {
                rewirer.RegisterCommands(console);
                registered++;
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
                report.Add("  console: " + rewirer.GetType().Name + " threw while registering - see the log");
            }
        }

        report.Add("  console: dropped " + dropped + " stale command(s), re-registered "
            + registered + " rewirer(s)");
    }

    private void RewireIslandModules(Assembly current, List<string> report)
    {
        if (Islands == null)
        {
            return;
        }

        int replayed = 0;

        foreach (IIslandModulesRewirer rewirer in RewirersFrom<IIslandModulesRewirer>(current))
        {
            try
            {
                rewirer.AddModules(Islands);
                replayed++;
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
                report.Add("  island panels: " + rewirer.GetType().Name + " threw - see the log");
            }
        }

        if (replayed > 0)
        {
            report.Add("  island panels: replayed " + replayed + " rewirer(s)");
        }
    }

    private void RewireBuildingModules(Assembly current, List<string> report)
    {
        if (Buildings == null)
        {
            return;
        }

        int replayed = 0;

        foreach (IBuildingModulesRewirer rewirer in RewirersFrom<IBuildingModulesRewirer>(current))
        {
            try
            {
                rewirer.AddModules(Buildings);
                replayed++;
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
                report.Add("  building panels: " + rewirer.GetType().Name + " threw - see the log");
            }
        }

        if (replayed > 0)
        {
            report.Add("  building panels: replayed " + replayed + " rewirer(s)");
        }
    }

    /// <summary>
    /// The rewirers of one kind that the freshly loaded assembly registered. Everything
    /// else in the registry belongs to another mod, or to the generation just disposed.
    /// </summary>
    private IEnumerable<TRewirer> RewirersFrom<TRewirer>(Assembly assembly) where TRewirer : IRewirer
    {
        List<TRewirer> found = new List<TRewirer>();

        try
        {
            foreach (IRewirer rewirer in GameRewirers.Rewirers)
            {
                if (rewirer is TRewirer typed && rewirer.GetType().Assembly == assembly)
                {
                    found.Add(typed);
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }

        return found;
    }

    /// <summary>
    /// Whether a handler is code from the given assembly - by the instance it closes over
    /// for the usual lambda, and by its declaring type for a static one.
    /// </summary>
    private static bool BelongsTo(Delegate handler, Assembly assembly)
    {
        if (handler == null || assembly == null)
        {
            return false;
        }

        if (handler.Target != null)
        {
            return handler.Target.GetType().Assembly == assembly;
        }

        return handler.Method?.DeclaringType?.Assembly == assembly;
    }
}
