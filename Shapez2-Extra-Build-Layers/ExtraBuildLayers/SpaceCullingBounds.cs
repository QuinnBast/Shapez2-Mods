using System;
using System.Reflection;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Stops high space layers from being culled whenever the camera is not looking straight down.
///
/// Culling starts with whole super chunks (<c>MapCuller.CullSuperChunksAndIslands</c>): a super
/// chunk's box is tested against the camera, and every island in one that fails is dropped
/// without being looked at. The box comes from
/// <c>SpaceThemeBoundsProvider.GetSuperChunkBounds</c>, which spans the provider's island layer
/// range - and the provider is built with a **hardcoded** range:
/// <code>
/// // GameSessionOrchestrator
/// Theme = new SpaceTheme(GraphicsSettings, -1, 1, ThemeResourceLifetime);
/// </code>
/// So every box ends around space level 2, whatever the scenario allows. Vanilla never notices.
/// At level 999 the camera is ~20000 units above the top of every box: looking straight down,
/// the frustum still reaches that far below and catches them; tilted, it swings off them, and the
/// cull keeps no islands at all - measured with <c>extrabuildlayers.dump-view</c>: 745 islands
/// looking down, 0 at a 63 degree pitch.
///
/// A postfix on <c>GetSuperChunkBounds</c> raises each box's top to the highest space level that
/// exists (<see cref="LayerRegistration.TopSpaceLevel"/>). The boxes are computed once per super
/// chunk and cached (<c>MapCuller.GetRegisterSuperChunk</c>), so this costs nothing per frame. A
/// taller box only means a super chunk is looked inside more often; the islands in it are still
/// culled one by one.
/// </summary>
internal sealed class SpaceCullingBounds : IDisposable
{
    private delegate Bounds GetBounds(SpaceThemeBoundsProvider self, SuperChunkCoordinate origin, ChunkVector expansion);

    private delegate Bounds GetBoundsHook(GetBounds orig, SpaceThemeBoundsProvider self, SuperChunkCoordinate origin, ChunkVector expansion);

    private readonly ILogger Logger;
    private readonly Hook Hook;
    private bool Complained;

    public SpaceCullingBounds(ILogger logger)
    {
        Logger = logger;

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            MethodInfo target = typeof(SpaceThemeBoundsProvider).GetMethod(
                nameof(SpaceThemeBoundsProvider.GetSuperChunkBounds), BindingFlags.Instance | BindingFlags.Public);
            if (target == null)
            {
                logger.Error?.Log("Extra Build Layers: GetSuperChunkBounds not found; high space layers will vanish unless viewed from above.");
                return;
            }

            Hook = new Hook(target, new GetBoundsHook(Detour));
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
        }
    }

    private Bounds Detour(GetBounds orig, SpaceThemeBoundsProvider self, SuperChunkCoordinate origin, ChunkVector expansion)
    {
        Bounds bounds = orig(self, origin, expansion);

        try
        {
            int top = BuildLayers.Current?.TopSpaceLevel ?? 0;
            if (top <= 1)
            {
                return bounds;
            }

            // The top of the highest layer's chunk, plus the chunk above it for anything standing
            // on that layer - the same one-chunk margin vanilla's expansion gives.
            GlobalChunkCoordinate highest = new GlobalChunkCoordinate(0, 0, (short)(top + 1));
            float topY = GlobalChunkBounds.From(highest, highest).ToWorldBounds().max.y;
            if (topY > bounds.max.y)
            {
                Vector3 max = bounds.max;
                max.y = topY;
                bounds.SetMinMax(bounds.min, max);
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

        return bounds;
    }

    public void Dispose()
    {
        Hook?.Dispose();
    }
}
