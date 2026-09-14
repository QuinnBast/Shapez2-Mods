using System;
using JetBrains.Annotations;
using MonoMod.RuntimeDetour;
using ShapezShifter.Flow;
using ShapezShifter.Hijack;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Mod Reloader — a development tool.
///
/// Rebuild a mod, run `mrl.reload &lt;name&gt;` in the console, and its new code runs without
/// restarting the game. On a large save that turns a two-minute reload into a second.
///
/// This is deliberately not something to ship alongside a mod. See <see cref="Reloader"/>
/// for why: every reload leaks an assembly, and a mod is only as reloadable as its
/// `Dispose` is thorough.
/// </summary>
[UsedImplicitly]
public class ModReloaderMod : IMod
{
    private readonly ILogger Logger;
    private readonly ModRegistry Registry;
    private readonly SessionRewiring Rewiring;
    private readonly Hook SessionHook;
    private readonly Hook BuildingModulesHook;
    private readonly RewirerHandle CommandsHandle;
    private readonly Seeder Seed;
    private readonly ConsoleTap Tap;
    private readonly StagedBuildWatcher Watcher;
    private readonly CrashScreenReload CrashScreen;
    private readonly RegistrationDeduplication Duplicates;
    private readonly PauseMenuReload PauseMenu;
    private readonly RewirerHandle WatchHandle;

    private bool Seeded;

    public ModReloaderMod(ILogger logger)
    {
        Logger = logger;
        Registry = new ModRegistry(logger);
        Rewiring = new SessionRewiring(logger, Registry);

        // Runs once per session and hands over the orchestrator, whose dependency container
        // holds the modding framework - and the side-panel lookup, which is only ever
        // offered here, so a reloaded mod's panels can only be restored if we keep it.
        SessionHook = DetourHelper.CreatePostfixHook<GameSessionOrchestrator, IslandsModulesLookup>(
            (orchestrator, lookup) => orchestrator.InjectIslandsModuleProviders(lookup),
            OnSessionReady);

        BuildingModulesHook = DetourHelper.CreatePostfixHook<GameSessionOrchestrator, BuildingsModulesLookup>(
            (orchestrator, lookup) => orchestrator.InjectBuildingsModuleProviders(lookup),
            OnBuildingModulesReady);

        Seed = new Seeder(logger, Registry);

        // Hooked here rather than when the commands are registered: the console is built
        // once per session, and the tap has to be in place before the first command runs.
        Tap = new ConsoleTap(logger);

        // Installed whether or not anything is ever reloaded: the stale registrations a
        // reload leaves behind outlive it, so the session build that trips over them may be
        // an ordinary load from the main menu much later.
        Duplicates = new RegistrationDeduplication(logger);

        Reloader reloader = new Reloader(logger, Registry, Rewiring, new SessionRecycler(logger, Registry));
        Watcher = new StagedBuildWatcher(logger, reloader, Tap);

        // The crash screen is the one place a reload is worth more than the console, and
        // the one place the console cannot be opened: by the time it is up the session that
        // owns the console has already been unloaded.
        CrashScreen = new CrashScreenReload(logger, reloader, Tap);

        // The same reload as mrl.reload, on the screen you are already looking at when you
        // tab back in from a rebuild - and the only one that pauses the simulation first.
        PauseMenu = new PauseMenuReload(logger, reloader, Tap);

        // The watcher's file events arrive on a thread pool thread, where nothing may
        // touch Unity. This is the main thread it hands the work over to; it ticks only
        // while a session is running, which is also the only time there is one to rebuild.
        WatchHandle = this.OnTick(Watcher.Pump);

        CommandsHandle = GameRewirers.AddRewirer(
            new ReloaderCommands(logger, Registry, reloader, Seed, Tap, Watcher));

        Logger.Info?.Log("Mod Reloader ready - mrl.list, then mrl.reload <name>, or mrl.watch (F1).");
    }

    private void OnSessionReady(GameSessionOrchestrator orchestrator, IslandsModulesLookup lookup)
    {
        try
        {
            Registry.Capture(orchestrator);
            Rewiring.Capture(lookup);

            // A staged build with nothing installed beside it is invisible to the game, so
            // the first build of a new mod would otherwise have nothing to reload.
            // Deliberately here rather than in the constructor: the loader is only
            // reachable now, and without it we could not tell that a mod is already
            // running from the workshop and must not be installed a second time.
            if (!Seeded)
            {
                Seeded = true;

                foreach (string line in Seed.Seed())
                {
                    Logger.Info?.Log(line);
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    private void OnBuildingModulesReady(GameSessionOrchestrator orchestrator, BuildingsModulesLookup lookup)
    {
        try
        {
            Registry.Capture(orchestrator);
            Rewiring.Capture(lookup);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    public void Dispose()
    {
        GameRewirers.RemoveRewirer(CommandsHandle);
        GameRewirers.RemoveRewirer(WatchHandle);
        Duplicates?.Dispose();
        PauseMenu?.Dispose();
        CrashScreen?.Dispose();
        Watcher?.Dispose();
        Tap?.Dispose();
        BuildingModulesHook?.Dispose();
        SessionHook?.Dispose();
    }
}
