using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Lets the overview stay on the player's layer, and lets the layer keys move it there.
///
/// Zoomed out past <c>TargetZoom</c> 1500 the game is in its overview, and
/// <c>IslandLayerController</c> treats itself as overridden there
/// (<c>IsCurrentlyOverriden</c>: in the overview and not placing). Three things hang off that one
/// check:
/// <list type="bullet">
/// <item><c>UpdateTick</c> parks the view - saves the layer, drops to the lowest, restores on the
/// way back in. From the lowest layer the camera tops out about 20000 units up, level with layer
/// 999, so high layers could never be seen from above.</item>
/// <item><c>IsActiveDuringState</c> returns false, so the layer keys do nothing.</item>
/// <item><c>HUDIslandLayersView</c> asks that same <c>IsActiveDuringState</c> whether to show, so
/// the layer selector disappears.</item>
/// </list>
/// Answering "not overridden" undoes all three at once: the overview stays on the current layer,
/// the layer keys (with <see cref="LayerKeyRepeat"/>'s hold-to-repeat) move it, the layer selector
/// stays on screen to say so, and zooming back in lands on whatever layer the overview is on.
///
/// Parking on the highest layer was tried first and was worse: with hundreds of layers, everything
/// far from the top is too small to see, and zooming in always returned to the top.
///
/// Also records the live layer controller and camera controller, which nothing else exposes, for
/// <see cref="LayerJumpOnSelect"/>.
/// </summary>
internal sealed class OverviewLayerParking : IDisposable
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private delegate bool Overriden(IslandLayerController self, bool isPlacingAnything);

    private delegate bool OverridenHook(Overriden orig, IslandLayerController self, bool isPlacingAnything);

    private delegate void CameraUpdate(CameraController self, InputDownstreamContext context, FrameDrawOptions options);

    private delegate void CameraUpdateHook(CameraUpdate orig, CameraController self, InputDownstreamContext context, FrameDrawOptions options);

    /// <summary>The live island layer controller, or null before it is first asked.</summary>
    public static IslandLayerController Controller { get; private set; }

    /// <summary>The live camera controller, or null before its first update.</summary>
    public static CameraController Camera { get; private set; }

    private readonly Hook OverrideDetour;
    private readonly Hook CameraDetour;

    public OverviewLayerParking(ILogger logger)
    {
        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            MethodInfo overriden = typeof(IslandLayerController).GetMethod("IsCurrentlyOverriden", Declared);
            if (overriden != null)
            {
                OverrideDetour = new Hook(overriden, new OverridenHook((orig, self, placing) =>
                {
                    Controller = self;
                    return false;
                }));
            }
            else
            {
                logger.Error?.Log("Extra Build Layers: IslandLayerController.IsCurrentlyOverriden not found; the overview parks on the lowest layer.");
            }

            MethodInfo update = typeof(CameraController).GetMethod(nameof(CameraController.OnGameUpdate), Declared);
            if (update != null)
            {
                CameraDetour = new Hook(update, new CameraUpdateHook((orig, self, context, options) =>
                {
                    Camera = self;
                    orig(self, context, options);
                }));
            }
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
        }
    }

    public void Dispose()
    {
        OverrideDetour?.Dispose();
        CameraDetour?.Dispose();
        Controller = null;
        Camera = null;
    }
}
