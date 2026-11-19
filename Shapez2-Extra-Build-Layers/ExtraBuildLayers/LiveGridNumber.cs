using System;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Keeps the space grid's number in step with the layer on screen - the other half of
/// <see cref="LayerNumberArt.PrepareGridIndicator"/>, which leaves one slice to be redrawn.
///
/// Hooked on <c>HUDLayersView.UpdateLayersState</c> because <c>HUDIslandLayersView</c> registers
/// it on exactly the events that matter - <c>Viewport.OnIslandLayerChanged</c> and
/// <c>OnNewViewportLoaded</c>, the second covering a save that opens on a high layer - and it
/// runs on the main thread, which uploading a texture requires. The building-layer view shares
/// the method and is filtered out by type.
/// </summary>
internal sealed class LiveGridNumber : IDisposable
{
    private readonly ILogger Logger;
    private readonly Hook UpdateHook;
    private bool Complained;

    public LiveGridNumber(ILogger logger)
    {
        Logger = logger;

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            UpdateHook = DetourHelper.CreatePostfixHook(
                (HUDLayersView view) => view.UpdateLayersState(), OnLayersState);
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            logger.Error?.Log("Extra Build Layers: could not follow the space layer; the grid will show the wrong number above layer 2.");
        }
    }

    private void OnLayersState(HUDLayersView view)
    {
        if (view is not HUDIslandLayersView)
        {
            return;
        }

        try
        {
            LayerNumberArt.ShowGridLayer(view.CurrentLayer);
        }
        catch (Exception exception)
        {
            // Once: this runs on every layer change, and the cause will not have changed.
            if (!Complained)
            {
                Complained = true;
                Logger.Exception?.LogException(exception);
            }
        }
    }

    public void Dispose()
    {
        UpdateHook?.Dispose();
    }
}
