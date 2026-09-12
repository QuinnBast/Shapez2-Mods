using System;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

/// <summary>
/// Makes the panel modal: while it is open the world beneath it stops responding, and Escape
/// closes it.
///
/// **Why an IMGUI window needs help with this at all.** IMGUI has its own event queue and the
/// game does not read it. The game reads <c>InputDownstreamContext</c>, which
/// <c>GameSessionOrchestrator.Update</c> fills once a frame and then walks through in a fixed
/// order: <c>DialogStack</c>, <c>HUD.OnGameUpdate</c>, <c>PlayerInteractionOrchestrator</c>
/// (which drives <c>CameraController</c>), <c>SystemButtons</c>. Drawing over the top of that
/// changes none of it, so the wheel still zoomed the camera and a click still placed a
/// building.
///
/// **How the game's own screens do it.** They consume. <c>HUDStatistics.OnGameUpdate</c> ends
/// with <c>context.ConsumeToken("HUDPart$confine_cursor")</c> and <c>context.ConsumeAll()</c>,
/// and that is the whole mechanism - <c>ConsumeAll</c> clears the active bindings, the mouse
/// delta and the wheel delta, so everything downstream sees an idle frame. The token is read
/// by <c>GameInputManager</c> as "a fullscreen overlay is open", which is what frees the cursor
/// and stops border panning.
///
/// **Why two hooks rather than one.** The consume has to land *after* the HUD parts have run,
/// or the debug console would stop working the moment this panel opened - it is a HUD part and
/// reads the same context. Escape is the opposite case: the pause menu is also a HUD part, so
/// a cancel that is still unconsumed when the parts run opens the pause menu behind us. So
/// cancel is taken in a prefix, before any part sees it, and everything else is cleared in a
/// postfix, after every part has had its turn.
/// </summary>
public class PanelInput : IDisposable
{
    private readonly ILogger Logger;
    private readonly DebugPanel Panel;
    private readonly Hook CancelHook;
    private readonly Hook BlockHook;

    private bool Complained;

    public PanelInput(ILogger logger, DebugPanel panel)
    {
        Logger = logger;
        Panel = panel;

        CancelHook = DetourHelper.CreatePrefixHook<HUD, InputDownstreamContext, FrameDrawOptions>(
            (hud, context, options) => hud.OnGameUpdate(context, options),
            (hud, context, options) =>
            {
                Cancel(context);
                return (context, options);
            });

        BlockHook = DetourHelper.CreatePostfixHook<HUD, InputDownstreamContext, FrameDrawOptions>(
            (hud, context, options) => hud.OnGameUpdate(context, options),
            (hud, context, options) => Block(context));
    }

    public void Dispose()
    {
        CancelHook?.Dispose();
        BlockHook?.Dispose();
    }

    private void Cancel(InputDownstreamContext context)
    {
        if (context == null || Panel == null || !Panel.Open)
        {
            return;
        }

        try
        {
            // Indexed by id through a dictionary the game owns, so an id this build does not
            // have is a KeyNotFoundException rather than a false.
            if (context.ConsumeWasActivated("global.cancel"))
            {
                Panel.Close();
            }
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private void Block(InputDownstreamContext context)
    {
        if (context == null || Panel == null || !Panel.Open)
        {
            return;
        }

        try
        {
            context.ConsumeToken("HUDPart$confine_cursor");
            context.ConsumeAll();
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    /// <summary>
    /// Once, not once a frame. This runs inside the frame loop, and a hook that throws every
    /// frame writes a log faster than anything else in it.
    /// </summary>
    private void Report(Exception exception)
    {
        if (Complained)
        {
            return;
        }

        Complained = true;
        Logger.Exception?.LogException(exception);
    }
}
