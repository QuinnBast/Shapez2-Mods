using System;
using System.Collections.Generic;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Rendering.Culling;
using Game.Core.Rendering.Islands.SpacePaths;
using Game.Core.Simulation;
using UnityEngine;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Draws the shaft of every lift: vanilla's lift art between the two stations, and the items
/// riding it.
///
/// Not a <c>StatelessIslandSimulationRenderer</c>, which is what vanilla island renderers use:
/// that one draws an island only when the island's own chunk is in the frame's cull result, and a
/// shaft is hundreds of chunks tall - looking at the top of a climb, the entry station at its foot
/// is off screen and the whole shaft would vanish. So this keeps its own list of shafts, fed by
/// <see cref="RegisterSimulation"/> for every entry station's simulation, and tests each level of
/// each shaft against the camera itself. Only the levels in view are drawn, so a 900-level climb
/// costs what the few levels on screen cost.
///
/// Picked up by type like every island renderer
/// (<c>GameSessionOrchestrator.CreateSimulationRenderers</c> instantiates every non-abstract
/// <c>IIslandSimulationRenderer</c> in every loaded assembly), and bound to entry stations by
/// simulation type.
///
/// Where an item is drawn: an entry's shaft is one belt segment per level, so an item's progress up
/// the climb is its segment index plus its progress along that segment. The first half segment
/// carries it from the input edge to the middle of the entry, and the rest lifts it from the middle
/// of the entry to the middle of the exit, where <see cref="LiftExitRenderer"/> takes over.
/// </summary>
public sealed class LiftShaftRenderer : IslandSimulationRenderer<LiftEntrySimulation>
{
    private const float ChunkSize = 20f;

    private sealed class Shaft
    {
        public LiftEntrySimulation Simulation;
        public ConnectableIslandSimulation Connectable;
        public LiftPath Path;
        public GlobalChunkCoordinate Entry;
        public int Rise;

        /// <summary>Where items come from: the entry's input pivot points at its source.</summary>
        public ChunkDirection InputFacing;
    }

    /// <summary>
    /// Every live shaft, for drawing and for <see cref="TryHitShaft"/>. Static because clicking is
    /// answered by a static game method with no route to this renderer; one renderer exists per
    /// session, and each clears the list when it is made, so a shaft never outlives its session.
    /// Touched only on the main thread: registration, drawing and clicking all happen there.
    /// </summary>
    private static readonly List<Shaft> Shafts = new List<Shaft>();

    private FrameDrawOptions CachedDrawOptions;

    public LiftShaftRenderer(IMapModel map)
    {
        Shafts.Clear();
    }

    /// <summary>
    /// The entry station of the nearest shaft the ray passes through, testing only levels from
    /// <paramref name="minLevel"/> to <paramref name="maxLevel"/> - the range the game's own island
    /// picking searches, so a shaft is clickable exactly where an island there would be.
    ///
    /// Each level is tested as a whole chunk. Nothing else can stand in a shaft's chunks - a climb
    /// through occupied levels is refused when it is dragged - so a generous box cannot take a
    /// click meant for another island.
    /// </summary>
    internal static bool TryHitShaft(Ray ray, int minLevel, int maxLevel, out GlobalChunkCoordinate entry, out float distance)
    {
        entry = default;
        distance = float.MaxValue;
        bool hit = false;

        foreach (Shaft shaft in Shafts)
        {
            int low = Math.Min(shaft.Entry.z, shaft.Entry.z + shaft.Rise);
            int high = Math.Max(shaft.Entry.z, shaft.Entry.z + shaft.Rise);
            int from = Math.Max(low, minLevel);
            int to = Math.Min(high, maxLevel);
            for (int z = from; z <= to; z++)
            {
                GlobalChunkCoordinate level = new GlobalChunkCoordinate(shaft.Entry.x, shaft.Entry.y, (short)z);
                Bounds bounds = new Bounds((Vector3)level.ToCenter_W(), new Vector3(ChunkSize, ChunkSize, ChunkSize));
                if (bounds.IntersectRay(ray, out float d) && d < distance)
                {
                    distance = d;
                    entry = shaft.Entry;
                    hit = true;
                }
            }
        }

        return hit;
    }

    public override void RegisterSimulation(ILocalizedSimulation localizedSimulation)
    {
        if (localizedSimulation is not ConnectableIslandSimulation connectable ||
            connectable.Simulation is not LiftEntrySimulation simulation ||
            !LiftStationIds.TryParse(connectable.Island.Definition.Id, out LiftPath path, out _))
        {
            return;
        }

        int rise = LiftStationConfiguration.RiseOf(connectable.Island.Configuration);
        if (rise == 0 || connectable.NumConnectors == 0 || connectable.GetConnector(0) is not IChunkSimulationConnector input)
        {
            return;
        }

        Shafts.Add(new Shaft
        {
            Simulation = simulation,
            Connectable = connectable,
            Path = path,
            Entry = connectable.Island.Transform.Position,
            Rise = rise,
            InputFacing = input.Pivot.Direction,
        });
    }

    public override void UnregisterSimulation(ILocalizedSimulation localizedSimulation)
    {
        Shafts.RemoveAll(shaft => ReferenceEquals(shaft.Connectable, localizedSimulation));
    }

    /// <summary>
    /// A summary of the last frame's cull result, for <c>extrabuildlayers.dump-view</c>. This
    /// renderer is handed the frame's <c>MapCullResult</c> and nothing else in the mod is, so it
    /// is the cheapest place to look at what culling kept.
    /// </summary>
    internal static string LastCullSummary = "no frame drawn yet";

    private static void Summarise(FrameDrawOptionsNoLOD options, MapCullResult cullResult)
    {
        int layer = options.Viewport.IslandLayer;
        int onLayer = 0;
        int minLod = int.MaxValue;
        int maxLod = int.MinValue;
        int rendering = 0;
        foreach (ChunkCullResult chunk in cullResult.Chunks)
        {
            if (chunk.Chunk_G.z != layer)
            {
                continue;
            }

            onLayer++;
            minLod = Math.Min(minLod, chunk.LOD.BuildingLOD);
            maxLod = Math.Max(maxLod, chunk.LOD.BuildingLOD);
            if (chunk.LOD.ShouldRenderBuildings)
            {
                rendering++;
            }
        }

        LastCullSummary =
            "islands " + cullResult.Islands.Count + ", chunks " + cullResult.Chunks.Count +
            "; on layer " + layer + ": " + onLayer + " chunk(s), building LOD " +
            (onLayer > 0 ? minLod + ".." + maxLod : "-") + ", " + rendering + " rendering buildings" +
            "; camera " + options.CameraPosition_W + ", max shown layer " + options.MaxBuildingIslandLayer;
    }

    /// <summary>
    /// The far zoom's level of detail: the coarsest island art, the same LOD 5 vanilla builds its
    /// overview meshes from, and no items (BuildingLOD above 3).
    /// </summary>
    private static readonly LODRenderConfig OverviewLOD = new LODRenderConfig
    {
        IslandLOD = 5,
        IslandMaterialLOD = 5,
        BuildingLOD = 6,
        ShapeLOD = 5,
    };

    /// <summary>
    /// Drawn in the far zoom too. <c>IslandSimulationRenderer</c> opts out of it, which is right
    /// for item renderers, but there islands are drawn from coarse overview meshes and a shaft
    /// would otherwise vanish, leaving its two stations unconnected.
    /// </summary>
    public override bool ShouldDraw(FrameDrawOptionsNoLOD options)
    {
        return true;
    }

    public override void Draw(FrameDrawOptionsNoLOD options, MapCullResult cullResult)
    {
        Summarise(options, cullResult);

        if (Shafts.Count == 0)
        {
            return;
        }

        LODRenderConfig lod;
        if (options.InOverviewMode)
        {
            lod = OverviewLOD;
        }
        else if (!TryNearestLOD(cullResult, out lod))
        {
            return;
        }

        options.CloneIntoLOD(ref CachedDrawOptions);
        CachedDrawOptions.LOD = lod;

        foreach (Shaft shaft in Shafts)
        {
            LiftStationArt.Art.TryGetValue(shaft.Path, out SpaceLiftDrawerMetaData art);
            DrawShaft(CachedDrawOptions, shaft, art);
        }
    }

    private static void DrawShaft(FrameDrawOptions options, Shaft shaft, SpaceLiftDrawerMetaData art)
    {
        int step = Math.Sign(shaft.Rise);
        int levels = Math.Abs(shaft.Rise);
        bool up = step > 0;
        GridRotation rotation = shaft.InputFacing.GlobalRotationTo().ZRotation;
        GlobalChunkCoordinate exit = Level(shaft.Entry, shaft.Rise);
        GlobalChunkCoordinate lower = up ? shaft.Entry : exit;

        if (art != null)
        {
            // The direction-specific end pieces: these are what make the entry read as "goes up"
            // or "goes down", and depend on the climb, not on the station.
            // The exit's are drawn by LiftExitRenderer, which knows which way the exit faces.
            if (Visible(options, shaft.Entry))
            {
                DrawPieces(options, up ? art.EntranceUpMeshes : art.EntranceDownMeshes, shaft.Entry, rotation);
            }

            // The tiling vanilla's lift generator uses (SpaceLiftMeshModules): an adapter on every
            // level from the lower end, and a connection on every level strictly between the ends.
            LODMeshMaterialAsset[] directedAdapters = up ? art.AdapterUpMeshes : art.AdapterDownMeshes;
            for (int k = 0; k < levels; k++)
            {
                GlobalChunkCoordinate at = Level(lower, k);
                if (!Visible(options, at))
                {
                    continue;
                }

                DrawPieces(options, art.AdapterMeshes, at, rotation);
                DrawPieces(options, directedAdapters, at, rotation);
                if (k > 0)
                {
                    DrawPieces(options, art.ConnectionMeshes, at, rotation);
                }
            }
        }

        if (options.LOD.BuildingLOD > 3)
        {
            return;
        }

        if (shaft.Path == LiftPath.Belt)
        {
            DrawItems<ShapeSpacePathBeltItemRendererAdapter, ShapeItem>(options, shaft, exit, default(ShapeSpacePathBeltItemRendererAdapter), options.Theme.BaseResources.SpaceBelts.ItemRenderingConfig);
        }
        else
        {
            DrawItems<FluidSpacePathBeltItemRendererAdapter, FluidPackageItem>(options, shaft, exit, default(FluidSpacePathBeltItemRendererAdapter), options.Theme.BaseResources.SpacePipes.ItemRenderingConfig);
        }
    }

    private static void DrawItems<TRenderer, TItem>(
        FrameDrawOptions options, Shaft shaft, GlobalChunkCoordinate exit, TRenderer renderer, SpacePathItemRenderingConfig config)
        where TRenderer : ISpacePathBeltItemRendererAdapter<TItem>
        where TItem : IBeltItem
    {
        List<ItemLaneBundle<FastBeltPathLane>> segments = shaft.Simulation.Shaft;
        int count = segments.Count;
        if (count == 0)
        {
            return;
        }

        WorldCoordinate entryCentre = shaft.Entry.ToCenter_W();
        WorldCoordinate exitCentre = exit.ToCenter_W();
        WorldCoordinate inputEdge = WorldCoordinate.Lerp(entryCentre, (shaft.Entry + shaft.InputFacing).ToCenter_W(), 0.5f);
        GridRotation laneRotation = shaft.InputFacing.Opposite.GlobalRotationTo().ZRotation;

        // The climb from the middle of the entry to the middle of the exit spans this much of the
        // total segment length; the first half segment is the run in from the input edge.
        float climb = Math.Max(0.5f, count - 0.5f);

        for (int s = 0; s < count; s++)
        {
            // A segment holds the items between its level and the next. If neither end is drawable,
            // nothing in it is - which is what keeps a tall shaft cheap.
            GlobalChunkCoordinate from = Level(shaft.Entry, s * Math.Sign(shaft.Rise));
            GlobalChunkCoordinate to = Level(shaft.Entry, (s + 1) * Math.Sign(shaft.Rise));
            if (!Visible(options, from) && !Visible(options, to))
            {
                continue;
            }

            ItemLaneBundle<FastBeltPathLane> segment = segments[s];
            for (short laneIndex = 0; laneIndex < 4; laneIndex++)
            {
                for (short layerIndex = 0; layerIndex < 3; layerIndex++)
                {
                    WorldVector offset = new WorldVector(
                        0f,
                        (layerIndex - 1f) * config.TracksSpacing + (laneIndex - 1.5f) * config.TrackItemsSpacing,
                        config.LayerOffset * layerIndex + config.Height).Rotate(laneRotation);

                    FastBeltPathLane lane = segment.GetLane(laneIndex, layerIndex);
                    Steps distance = lane.FirstItemDistance_S;
                    IReadOnlyList<FastBeltPathLaneState.ItemOnBelt> items = lane.Items;
                    for (int i = 0; i < items.Count; i++)
                    {
                        FastBeltPathLaneState.ItemOnBelt item = items[i];
                        float t = s + Steps.Ratio(distance, lane.Length_S);
                        distance += item.NextItemDistance_S;

                        WorldCoordinate at = t < 0.5f
                            ? WorldCoordinate.Lerp(inputEdge, entryCentre, t * 2f)
                            : WorldCoordinate.Lerp(entryCentre, exitCentre, (t - 0.5f) / climb);
                        at += offset;
                        renderer.RenderItem(options, (TItem)item.Item, FastMatrix.Translate(in at));
                    }
                }
            }
        }
    }

    internal static void DrawPieces(FrameDrawOptions options, LODMeshMaterialAsset[] pieces, GlobalChunkCoordinate at, GridRotation rotation)
    {
        if (pieces == null)
        {
            return;
        }

        GlobalChunkTransform transform = new GlobalChunkTransform(at, rotation);
        foreach (LODMeshMaterialAsset piece in pieces)
        {
            if (piece != null && piece.TryGet(options.LOD.IslandLOD, options.LOD.IslandMaterialLOD, out IMeshReference mesh, out IMaterialReference material))
            {
                options.Renderers.Islands.Add(mesh, material, FastMatrix.ByTransform(in transform));
            }
        }
    }

    /// <summary>
    /// In front of the camera, and on a layer the player is showing - the same layer rule the
    /// game's own platform contents follow, so a shaft above the viewed layer hides with
    /// everything else there.
    /// </summary>
    private static bool Visible(FrameDrawOptions options, GlobalChunkCoordinate chunk)
    {
        // The far zoom shows every layer at once - its island meshes ignore the layer filter - so
        // the shaft does too.
        if (!options.InOverviewMode && !options.ShouldRenderPlatformContentsAtLayer(chunk.z))
        {
            return false;
        }

        Bounds bounds = new Bounds((Vector3)chunk.ToCenter_W(), new Vector3(ChunkSize, ChunkSize, ChunkSize));
        return GeometryUtility.TestPlanesAABB(options.CameraPlanes, bounds);
    }

    private static GlobalChunkCoordinate Level(GlobalChunkCoordinate from, int levels)
    {
        return new GlobalChunkCoordinate(from.x, from.y, (short)(from.z + levels));
    }

    /// <summary>
    /// The level of detail of the nearest island in view. A shaft is not one of the frame's culled
    /// islands, so it borrows one; the nearest is the finest, which is right for the levels the
    /// player is looking at.
    /// </summary>
    private static bool TryNearestLOD(MapCullResult cullResult, out LODRenderConfig lod)
    {
        lod = default;
        float nearest = float.MaxValue;
        foreach (IslandCullResult island in cullResult.Islands)
        {
            if (island.CameraDistanceSquare < nearest)
            {
                nearest = island.CameraDistanceSquare;
                lod = island.LOD;
            }
        }

        return nearest < float.MaxValue;
    }
}

/// <summary>
/// Draws items leaving an exit station: from the middle of the station, where the shaft delivers
/// them, out to its output side. An ordinary island renderer, since an exit is one chunk.
/// </summary>
public sealed class LiftExitRenderer : StatelessIslandSimulationRenderer<LiftExitSimulation, IIslandCustomDrawData>
{
    public LiftExitRenderer(IMapModel map)
        : base(map)
    {
    }

    // Not limited to close range like the items: the exit's direction piece is part of how the
    // lift looks from any distance the island itself is drawn at.
    public override bool ShouldDraw(LODRenderConfig lod)
    {
        return true;
    }

    public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
    {
        if (!options.ShouldRenderPlatformContentsAtLayer(entity.Transform.Position.z) ||
            entity.LocalizedSimulation.NumConnectors == 0 ||
            entity.LocalizedSimulation.GetConnector(0) is not IChunkSimulationConnector output ||
            !LiftStationIds.TryParse(entity.Definition.Id, out LiftPath path, out _))
        {
            return;
        }

        int rise = LiftStationConfiguration.RiseOf(entity.Island.Configuration);
        if (rise != 0 && LiftStationArt.Art.TryGetValue(path, out SpaceLiftDrawerMetaData art))
        {
            LiftShaftRenderer.DrawPieces(
                options, rise > 0 ? art.ExitUpMeshes : art.ExitDownMeshes, output.Pivot.Position,
                output.Pivot.Direction.GlobalRotationTo().ZRotation);
        }

        if (options.LOD.BuildingLOD > 3)
        {
            return;
        }

        if (path == LiftPath.Belt)
        {
            DrawItems<ShapeSpacePathBeltItemRendererAdapter, ShapeItem>(options, entity.Simulation, output.Pivot, default(ShapeSpacePathBeltItemRendererAdapter), options.Theme.BaseResources.SpaceBelts.ItemRenderingConfig);
        }
        else
        {
            DrawItems<FluidSpacePathBeltItemRendererAdapter, FluidPackageItem>(options, entity.Simulation, output.Pivot, default(FluidSpacePathBeltItemRendererAdapter), options.Theme.BaseResources.SpacePipes.ItemRenderingConfig);
        }
    }

    private static void DrawItems<TRenderer, TItem>(
        FrameDrawOptions options, LiftExitSimulation simulation, GlobalChunkPivot output, TRenderer renderer, SpacePathItemRenderingConfig config)
        where TRenderer : ISpacePathBeltItemRendererAdapter<TItem>
        where TItem : IBeltItem
    {
        WorldCoordinate centre = output.Position.ToCenter_W();
        WorldCoordinate edge = WorldCoordinate.Lerp(centre, (output.Position + output.Direction).ToCenter_W(), 0.5f);
        GridRotation laneRotation = output.Direction.GlobalRotationTo().ZRotation;

        for (short laneIndex = 0; laneIndex < 4; laneIndex++)
        {
            for (short layerIndex = 0; layerIndex < 3; layerIndex++)
            {
                WorldVector offset = new WorldVector(
                    0f,
                    (layerIndex - 1f) * config.TracksSpacing + (laneIndex - 1.5f) * config.TrackItemsSpacing,
                    config.LayerOffset * layerIndex + config.Height).Rotate(laneRotation);

                FastBeltPathLane lane = simulation.Segment.GetLane(laneIndex, layerIndex);
                Steps distance = lane.FirstItemDistance_S;
                IReadOnlyList<FastBeltPathLaneState.ItemOnBelt> items = lane.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    FastBeltPathLaneState.ItemOnBelt item = items[i];
                    float t = Steps.Ratio(distance, lane.Length_S);
                    distance += item.NextItemDistance_S;
                    WorldCoordinate at = WorldCoordinate.Lerp(centre, edge, t) + offset;
                    renderer.RenderItem(options, (TItem)item.Item, FastMatrix.Translate(in at));
                }
            }
        }
    }
}
