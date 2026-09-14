using System;
using JetBrains.Annotations;
using MonoMod.RuntimeDetour;
using ShapezShifter.Hijack;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// Platform Blackbox.
///
/// Groundwork for collapsing a selection of platforms into a single stand-in platform. What
/// works today is the part everything else depends on: reading a selection, working out
/// which of its ports cross the boundary, and capturing it as a blueprint that can be
/// placed again.
///
/// The boundary analysis is the interesting half. A selection is only replaceable by a
/// small platform if the number of ports leaving it is small - everything wired internally
/// is detail that a stand-in would hide.
/// </summary>
[UsedImplicitly]
public class PlatformBlackboxMod : IMod
{
    private readonly ILogger Logger;
    private readonly SessionServices Session;
    private readonly BlackboxCommands Commands;
    private readonly Hook SessionHook;
    private readonly RewirerHandle CommandsHandle;
    private readonly RewirerHandle ToolbarHandle;
    private readonly BlackboxPlacement Placement;

    public PlatformBlackboxMod(ILogger logger)
    {
        Logger = logger;
        QuinnBast.Shapez2.ToolbarKit.ToolbarKit.Log = logger;
        Session = new SessionServices(logger);

        ToolbarMap toolbar = new ToolbarMap(logger);
        Placement = new BlackboxPlacement(logger, Session);
        Commands = new BlackboxCommands(logger, Session, toolbar, Placement);

        // The session is taken off the tick rather than off a hook on session setup. A hot
        // reload happens mid-session, so any one-shot init hook has already fired and will
        // not fire again - a freshly loaded instance would sit there with no orchestrator.
        // Capture is a reference comparison, so paying it per frame costs nothing.
        SessionHook = DetourHelper.CreatePostfixHook<GameSessionOrchestrator, float>(
            (orchestrator, time) => orchestrator.Tick(time),
            OnTick);

        CommandsHandle = GameRewirers.AddRewirer(Commands);

        // Content has to be registered before any session exists, so this happens here
        // rather than off the tick like the session-scoped work above.
        BlackboxIsland.Register(logger);

        // Registered last, and therefore applied last, so the tree it captures includes
        // this mod's own toolbar entry. Registered first, it reported the vanilla toolbar
        // and was useless for finding out where the entry had gone.
        ToolbarHandle = GameRewirers.AddRewirer(toolbar);

        Logger.Info?.Log("Platform Blackbox ready - select platforms and run pbx.analyze (F1).");
    }

    private void OnTick(GameSessionOrchestrator orchestrator, float time)
    {
        try
        {
            if (Session.Capture(orchestrator))
            {
                OnSessionChanged();
                Placement.Watch(orchestrator.Simulator);
            }

            // Polled rather than bound, because a real keybinding has to be registered
            // before a session exists and this is still finding its shape.
            Placement.Update(orchestrator);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    /// <summary>
    /// Claims the console commands for this instance.
    ///
    /// Shifter registers console commands from a hook on session setup, which fires once. A
    /// reloaded mod is added as a rewirer long after that, so it never gets asked - and the
    /// console would keep dispatching into the assembly this one replaced. Re-registering is
    /// harmless at ordinary session start, where it replaces identical entries.
    /// </summary>
    private void OnSessionChanged()
    {
        if (Session.TryGetConsole(out IDebugConsole console))
        {
            Commands.RegisterCommands(console);
        }
    }

    public void Dispose()
    {
        // Leaving dead handlers behind would keep this assembly reachable from the console.
        if (Session.TryGetConsole(out IDebugConsole console))
        {
            Commands.UnregisterCommands(console);
        }

        GameRewirers.RemoveRewirer(CommandsHandle);
        GameRewirers.RemoveRewirer(ToolbarHandle);
        SessionHook?.Dispose();
    }
}
