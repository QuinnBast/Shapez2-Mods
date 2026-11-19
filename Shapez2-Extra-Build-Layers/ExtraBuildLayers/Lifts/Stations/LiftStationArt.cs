using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Content.Islands;
using Game.Core.Coordinates;
using Game.Core.Rendering.Islands;
using Game.Core.Rendering.Islands.SpacePaths;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Dresses the lift stations in vanilla's own lift art: the entry gets the lift's entrance, the
/// exit its exit.
///
/// The art lives in the authoring metadata (<c>SpaceLiftDrawerMetaData</c> on the space belt and
/// space pipe node metadata), which an islands rewirer is handed and nothing else is. The modules
/// are attached to the definition objects <see cref="LiftStationIslands"/> kept from its builders,
/// so this works whichever order the rewirers run in.
///
/// Only the ends are drawn here. The shaft between them depends on each climb's height, so it is
/// drawn per instance, not per definition.
/// </summary>
internal sealed class LiftStationArt : IIslandsRewirer
{
    /// <summary>
    /// The lift art per path kind, for <see cref="LiftShaftRenderer"/>: the shaft pieces and the
    /// up/down-specific end pieces depend on each climb, so they are drawn per instance and need
    /// the art at draw time. Authored assets, so holding them across sessions is harmless.
    /// </summary>
    public static readonly Dictionary<LiftPath, SpaceLiftDrawerMetaData> Art = new Dictionary<LiftPath, SpaceLiftDrawerMetaData>();

    /// <summary>
    /// Every island definition of the last scenario built, for <see cref="ViewPlatformModules"/>,
    /// which needs to give each one a side panel provider and is handed nothing but the lookup.
    /// Recorded here because an islands rewirer is handed the built islands.
    /// </summary>
    public static IReadOnlyList<IIslandDefinition> AllDefinitions = Array.Empty<IIslandDefinition>();

    private readonly ILogger Logger;

    public LiftStationArt(ILogger logger)
    {
        Logger = logger;
    }

    public GameIslands ModifyGameIslands(IslandDefinitionFactory factory, AuthoringIslands metaIslands, GameIslands gameIslands)
    {
        AllDefinitions = gameIslands?.AllDefinitions?.ToArray() ?? Array.Empty<IIslandDefinition>();

        try
        {
            Dress(LiftPath.Belt, metaIslands?.SpaceBeltIsland?.SpaceLiftDrawerData);
            Dress(LiftPath.Pipe, metaIslands?.SpacePipeIsland?.SpaceLiftDrawerData);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log("Extra Build Layers: could not dress the lift stations; they will draw as bare chunks.");
        }

        return gameIslands;
    }

    private void Dress(LiftPath path, SpaceLiftDrawerMetaData art)
    {
        if (art == null)
        {
            Logger.Warning?.Log("Extra Build Layers: no " + path + " lift art in this scenario's metadata.");
            return;
        }

        Art[path] = art;
        Attach(path, LiftEnd.Entry, art.EntranceMeshes, ChunkDirection.West);
        Attach(path, LiftEnd.Exit, art.ExitMeshes, ChunkDirection.East);
    }

    private static void Attach(LiftPath path, LiftEnd end, LODMeshMaterialAsset[] meshes, ChunkDirection facing)
    {
        if (!LiftStationIslands.Definitions.TryGetValue((path, end), out IslandDefinition definition) || meshes == null)
        {
            return;
        }

        // The same transform SpaceLiftMeshModules gives a lift's end: at the connector, turned to
        // face the way the connector faces.
        LocalChunkTransform transform = new LocalChunkTransform(ChunkVector.Zero, facing.GlobalRotationTo().ZRotation);
        List<ModularIslandMeshDrawer.Module> modules = new List<ModularIslandMeshDrawer.Module>();
        foreach (LODMeshMaterialAsset mesh in meshes)
        {
            modules.Add(new ModularIslandMeshDrawer.Module(transform, mesh));
        }

        definition.CustomData.AttachOrReplace(new ModularIslandMeshDrawer.Data(modules));

        // The far zoom draws islands from a different source: IslandOverviewDrawer combines each
        // island's IslandOverviewDrawer.Data per super chunk, and an island without it is simply
        // absent there. Built the way CommonIslandDefinitionFactory.AttachOverviewMeshes builds
        // vanilla's - the coarsest LOD of each module.
        List<IslandOverviewDrawer.Module> overview = new List<IslandOverviewDrawer.Module>();
        foreach (ModularIslandMeshDrawer.Module module in modules)
        {
            if (module.MeshMaterial.TryGet(5, 5, out IMeshReference mesh, out IMaterialReference _) && mesh != null && !mesh.IsEmpty)
            {
                overview.Add(new IslandOverviewDrawer.Module(module.Transform, mesh));
            }
        }

        if (overview.Count > 0)
        {
            definition.CustomData.AttachOrReplace(new IslandOverviewDrawer.Data(overview.ToArray()));
        }
    }
}
