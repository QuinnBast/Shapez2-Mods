using System.Collections.Generic;
using Game.Core.Coordinates;
using Game.Core.Rendering.Islands.SpacePaths;
using Game.Core.Rendering.Islands;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Builds the mesh modules for a lift island of any height.
///
/// This is a transcription of the game's own
/// <c>SpacePathIslandDefinitionFactory.CreateModularLiftDrawData</c>, and the reason a custom
/// stretched model is not needed: that method is **already general**. It derives
/// <c>absoluteLayerDifference</c> from the two pivots and then tiles - one connection mesh per
/// layer between the ends, one adapter per layer - so vanilla's own lift art covers a
/// twelve-layer shaft as readily as a two-layer one. It is only ever *called* with two-layer
/// pivots because those are the only lift islands the game authors.
///
/// Transcribed rather than called because the original is a private instance method on a factory
/// this mod has no instance of. The shape is kept deliberately close to the original so the two
/// can be diffed if the art or the layering ever changes.
///
/// Attaching the result replaces the copied <c>ModularIslandMeshDrawer.Data</c>: that is what a
/// space lift draws from, and a stretched island that kept the original's would draw a two-layer
/// shaft between connectors twelve layers apart.
/// </summary>
internal static class SpaceLiftMeshModules
{
    public static List<ModularIslandMeshDrawer.Module> Create(
        LocalChunkPivot inputPivot, LocalChunkPivot outputPivot, SpaceLiftDrawerMetaData drawData)
    {
        List<ModularIslandMeshDrawer.Module> modules = new List<ModularIslandMeshDrawer.Module>();
        if (drawData == null)
        {
            return modules;
        }

        LocalChunkTransform inputTransform = new LocalChunkTransform(
            inputPivot.Position, inputPivot.Direction.GlobalRotationTo().ZRotation);
        LocalChunkTransform outputTransform = new LocalChunkTransform(
            outputPivot.Position, outputPivot.Direction.GlobalRotationTo().ZRotation);

        // The shaft is built from the lower end upwards regardless of travel direction, which is
        // why this is the lower of the two rather than simply the input.
        ChunkVector lowerConnectorPosition =
            outputPivot.Position.z > inputPivot.Position.z ? inputPivot.Position : outputPivot.Position;

        int difference = outputTransform.Position.z - inputTransform.Position.z;
        int absoluteLayerDifference = System.Math.Abs(difference);
        bool isGoingUp = difference >= 0;

        foreach (LODMeshMaterialAsset mesh in drawData.EntranceMeshes)
        {
            modules.Add(new ModularIslandMeshDrawer.Module(inputTransform, mesh));
        }

        foreach (LODMeshMaterialAsset mesh in isGoingUp ? drawData.EntranceUpMeshes : drawData.EntranceDownMeshes)
        {
            modules.Add(new ModularIslandMeshDrawer.Module(inputTransform, mesh));
        }

        foreach (LODMeshMaterialAsset mesh in drawData.ExitMeshes)
        {
            modules.Add(new ModularIslandMeshDrawer.Module(outputTransform, mesh));
        }

        foreach (LODMeshMaterialAsset mesh in isGoingUp ? drawData.ExitUpMeshes : drawData.ExitDownMeshes)
        {
            modules.Add(new ModularIslandMeshDrawer.Module(outputTransform, mesh));
        }

        // The tiling that makes any height work: one connection per layer strictly between the
        // ends, so a two-layer lift gets one and a twelve-layer lift gets eleven.
        foreach (LODMeshMaterialAsset connectionMesh in drawData.ConnectionMeshes)
        {
            for (int i = 1; i < absoluteLayerDifference; i++)
            {
                LocalChunkTransform transform = new LocalChunkTransform(
                    lowerConnectorPosition + i * ChunkVector.Up, inputTransform.Rotation);
                modules.Add(new ModularIslandMeshDrawer.Module(transform, connectionMesh));
            }
        }

        LODMeshMaterialAsset[] directedAdapterMeshes = isGoingUp ? drawData.AdapterUpMeshes : drawData.AdapterDownMeshes;
        for (int layer = 0; layer < absoluteLayerDifference; layer++)
        {
            LocalChunkTransform adapterTransform = new LocalChunkTransform(
                lowerConnectorPosition + layer * ChunkVector.Up, inputTransform.Rotation);

            foreach (LODMeshMaterialAsset mesh in drawData.AdapterMeshes)
            {
                modules.Add(new ModularIslandMeshDrawer.Module(adapterTransform, mesh));
            }

            foreach (LODMeshMaterialAsset mesh in directedAdapterMeshes)
            {
                modules.Add(new ModularIslandMeshDrawer.Module(adapterTransform, mesh));
            }
        }

        return modules;
    }
}
