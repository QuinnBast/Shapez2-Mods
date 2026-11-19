using System;
using System.Reflection;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using Unity.Mathematics;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Lets a click anywhere on a lift's shaft select the lift.
///
/// A station is one chunk, so only the two ends of a climb are islands the game can hit; the
/// levels between are drawn, not occupied. Island picking is
/// <c>ScreenUtils.TryFindIslandAtCursor</c>: it walks the chunks under the cursor between the
/// viewed island layer and the highest one being shown, and returns the first island whose
/// collider the ray hits. A postfix there - only when that found nothing - tests the same ray
/// against the shafts in the same layer range (<see cref="LiftShaftRenderer.TryHitShaft"/>) and
/// answers with the climb's entry station, so selecting, hovering, deleting and copying the shaft
/// all act on the lift.
///
/// The range is the one vanilla computes - the viewed layer, or up to the top layer when all
/// layers are shown, or the caller's overrides - so a shaft is clickable on exactly the layers an
/// island there would be.
/// </summary>
internal sealed class LiftShaftPicking : IDisposable
{
    private delegate bool Find(
        Viewport viewport, in float2 screenCoordinate, IMapModel map, out IslandModel island,
        out GlobalChunkCoordinate chunk, out Ray ray, out float rayHitDistance, short? minLayerOverride, short? maxLayerOverride);

    private delegate bool FindHook(
        Find orig, Viewport viewport, in float2 screenCoordinate, IMapModel map, out IslandModel island,
        out GlobalChunkCoordinate chunk, out Ray ray, out float rayHitDistance, short? minLayerOverride, short? maxLayerOverride);

    private readonly ILogger Logger;
    private readonly Hook Hook;
    private bool Complained;

    public LiftShaftPicking(ILogger logger)
    {
        Logger = logger;

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            MethodInfo target = typeof(ScreenUtils).GetMethod(
                nameof(ScreenUtils.TryFindIslandAtCursor), BindingFlags.Static | BindingFlags.Public);
            if (target == null)
            {
                logger.Error?.Log("Extra Build Layers: ScreenUtils.TryFindIslandAtCursor not found; lift shafts cannot be clicked.");
                return;
            }

            Hook = new Hook(target, new FindHook(Detour));
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
        }
    }

    private bool Detour(
        Find orig, Viewport viewport, in float2 screenCoordinate, IMapModel map, out IslandModel island,
        out GlobalChunkCoordinate chunk, out Ray ray, out float rayHitDistance, short? minLayerOverride, short? maxLayerOverride)
    {
        // The overview parks on the highest layer (OverviewLayerParking), and this search starts at
        // the viewed layer - so left alone it would only find top-layer platforms there. Vanilla
        // parks on the lowest layer, where "from here up" is everything; give the search the whole
        // unlocked range explicitly instead.
        if (minLayerOverride == null && maxLayerOverride == null && viewport != null && viewport.TargetZoom >= 1500f)
        {
            OverviewRange(ref minLayerOverride, ref maxLayerOverride);
        }

        if (orig(viewport, in screenCoordinate, map, out island, out chunk, out ray, out rayHitDistance, minLayerOverride, maxLayerOverride))
        {
            return true;
        }

        try
        {
            // The same range TryFindIslandAtCursor searches.
            int min = minLayerOverride ?? viewport.IslandLayer;
            int max = maxLayerOverride ?? (viewport.ShowAllIslandLayers ? map.MaxIslandLayer : viewport.IslandLayer);

            if (LiftShaftRenderer.TryHitShaft(ray, min, max, out GlobalChunkCoordinate entry, out float distance) &&
                map.TryGetIsland(entry, out IslandModel station))
            {
                island = station;
                chunk = entry;
                rayHitDistance = distance;
                return true;
            }
        }
        catch (Exception exception)
        {
            if (!Complained)
            {
                Complained = true;
                Logger.Exception?.LogException(exception);
            }
        }

        return false;
    }

    private static void OverviewRange(ref short? min, ref short? max)
    {
#pragma warning disable CS0618 // A static click query has nothing to inject.
        IGameSessionManagers session = StaticGameCoreAccessor.HasInstance() ? StaticGameCoreAccessor.G : null;
#pragma warning restore CS0618
        if (session?.InteractionMode == null || session.LocalPlayer == null)
        {
            return;
        }

        min = session.InteractionMode.GetMinimumAllowedIslandLayer(session.LocalPlayer);
        max = session.InteractionMode.GetMaximumAllowedIslandLayer(session.LocalPlayer);
    }

    public void Dispose()
    {
        Hook?.Dispose();
    }
}
