using System.Collections.Generic;
using Game.Core.Coordinates;
using Game.Core.Rendering;
using Unity.Mathematics;
using UnityEngine;

#pragma warning disable CS0618 // IIslandPlatformDrawer is obsolete, but it is how space paths draw.

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Draws a crossing as two space path segments at right angles.
    ///
    /// The first attempt at this borrowed the segment's meshes off its island definition and fed
    /// them to ModularIslandMeshDrawer. That was wrong twice over: a space path definition carries
    /// no IslandMeshDrawer.Data at all - its meshes live on the visual theme, in SpaceBeltResources
    /// and SpacePipeResources, keyed by PathNodeClassification - and even with the right meshes the
    /// modular drawer would have put them at the wrong height and in the wrong render pass.
    /// SpacePathPlatformDrawer drops a segment by 2.07314 world units and submits it to
    /// Renderers.SpacePaths; ModularIslandMeshDrawer does neither.
    ///
    /// So this is SpacePathPlatformDrawer with one addition: it draws its mesh list twice, the
    /// second time turned a quarter turn. A rotation maps local East to
    /// rotation.ToChunkDirection() and RotateCW maps East to South, so that quarter turn is the
    /// same one the connectors, the appearance and the placement processor all use, and the
    /// mirrored variant turns the other way so its arrows point where its items travel.
    ///
    /// IIslandPlatformDrawer is marked obsolete in favour of ModularIslandMeshDrawer, which is the
    /// direction the game is moving - but until space paths move with it, this is how their track
    /// gets drawn, blueprint ghosts and overview map included.
    internal sealed class CrossoverPlatformDrawer : IIslandPlatformDrawer
    {
        /// SpacePathPlatformDrawer's own constant. Space path meshes are authored expecting to sit
        /// this far below the chunk centre; drawing them at the island's height floats the track.
        private const float HeightOffset = -2.07314f;

        /// Every crossing is two straight segments, so both runs always ask for Forward.
        private const PathNodeClassification Run = PathNodeClassification.Forward;

        private readonly ISpacePathResources PathA;
        private readonly ISpacePathResources PathB;

        /// The quarter turn from path A to path B: clockwise normally, anticlockwise mirrored.
        private readonly GridRotation TurnToB;

        public CrossoverPlatformDrawer(
            ISpacePathResources pathA, ISpacePathResources pathB, bool mirrored)
        {
            PathA = pathA;
            PathB = pathB;
            TurnToB = mirrored ? GridRotation.RotateCCW : GridRotation.RotateCW;
        }

        public void Draw(
            FrameDrawOptions drawOptions, in GlobalChunkTransform transform,
            IIslandConfiguration islandConfiguration, LODMaterialAsset materialProvider, float3 scale)
        {
            float3 centre = transform.Position.ToCenter_W(HeightOffset);
            DrawRun(drawOptions, PathA, transform.Rotation, centre, scale);
            DrawRun(drawOptions, PathB, transform.Rotation + TurnToB, centre, scale);
        }

        private static void DrawRun(
            FrameDrawOptions drawOptions, ISpacePathResources resources, GridRotation rotation,
            float3 centre, float3 scale)
        {
            IReadOnlyList<LODMeshMaterialAsset> meshMaterials = resources.GetMeshMaterials(Run);
            for (int i = 0; i < meshMaterials.Count; i++)
            {
                if (((ILODMeshMaterial)meshMaterials[i]).TryGet(
                        drawOptions.LOD.IslandLOD, drawOptions.LOD.IslandMaterialLOD,
                        out IMeshReference mesh, out IMaterialReference material))
                {
                    Matrix4x4 trs = Matrix4x4.TRS(
                        (Vector3)centre, FastMatrix.RotateY(rotation), (Vector3)scale);
                    drawOptions.Renderers.SpacePaths.Add(mesh, material, trs);
                }
            }
        }

        public void DrawBlueprint(
            FrameDrawOptions drawOptions, in GlobalChunkTransform transform,
            IIslandConfiguration islandConfiguration, MaterialPropertyBlock propertyBlock,
            PropertyBlockHash propertyBlockHash, float3 scale)
        {
            float3 centre = transform.Position.ToCenter_W(HeightOffset);
            float3 offset = IslandsBlueprintDrawUtils.GetCameraPositionOffset(drawOptions, centre);
            BlueprintRun(drawOptions, PathA, transform.Rotation, centre + offset, scale,
                propertyBlock, propertyBlockHash);
            BlueprintRun(drawOptions, PathB, transform.Rotation + TurnToB, centre + offset, scale,
                propertyBlock, propertyBlockHash);
        }

        private static void BlueprintRun(
            FrameDrawOptions drawOptions, ISpacePathResources resources, GridRotation rotation,
            float3 centre, float3 scale, MaterialPropertyBlock propertyBlock,
            PropertyBlockHash propertyBlockHash)
        {
            IReadOnlyList<LODMeshMaterialAsset> meshMaterials = resources.GetMeshMaterials(Run);
            for (int i = 0; i < meshMaterials.Count; i++)
            {
                if (!meshMaterials[i].TryGet(
                        drawOptions.LOD.IslandLOD, drawOptions.LOD.IslandMaterialLOD,
                        out IMeshReference mesh, out IMaterialReference material))
                {
                    continue;
                }

                Matrix4x4 trs = Matrix4x4.TRS(
                    (Vector3)centre, FastMatrix.RotateY(rotation), (Vector3)scale);

                // The deck plane gets the blueprint material; everything else keeps its own. Same
                // split SpacePathPlatformDrawer makes, and skipping it makes a ghost look solid.
                if (material.InstanceId == resources.StandardPlaneMaterial.InstanceId)
                {
                    IslandsBlueprintDrawUtils.DrawBlueprintMesh(
                        drawOptions, mesh, trs, propertyBlock, propertyBlockHash,
                        resources.BlueprintPlaneMaterial);
                }
                else
                {
                    IslandsBlueprintDrawUtils.DrawBlueprintMesh(
                        drawOptions, mesh, trs, propertyBlock, propertyBlockHash);
                }
            }
        }

        public void DrawBlueprintNonInstanced(
            FrameDrawOptions drawOptions, in GlobalChunkTransform transform,
            IIslandConfiguration islandConfiguration, MaterialPropertyBlock propertyBlock,
            float3 scale)
        {
            float3 centre = transform.Position.ToCenter_W(HeightOffset);
            float3 offset = IslandsBlueprintDrawUtils.GetCameraPositionOffset(drawOptions, centre);
            NonInstancedRun(drawOptions, PathA, transform.Rotation, centre + offset, scale, propertyBlock);
            NonInstancedRun(drawOptions, PathB, transform.Rotation + TurnToB, centre + offset, scale, propertyBlock);
        }

        private static void NonInstancedRun(
            FrameDrawOptions drawOptions, ISpacePathResources resources, GridRotation rotation,
            float3 centre, float3 scale, MaterialPropertyBlock propertyBlock)
        {
            IReadOnlyList<LODMeshMaterialAsset> meshMaterials = resources.GetMeshMaterials(Run);
            for (int i = 0; i < meshMaterials.Count; i++)
            {
                if (!meshMaterials[i].TryGet(
                        drawOptions.LOD.IslandLOD, drawOptions.LOD.IslandMaterialLOD,
                        out IMeshReference mesh, out IMaterialReference material))
                {
                    continue;
                }

                Matrix4x4 trs = Matrix4x4.TRS(
                    (Vector3)centre, FastMatrix.RotateY(rotation), (Vector3)scale);

                if (material.InstanceId == resources.StandardPlaneMaterial.InstanceId)
                {
                    IslandsBlueprintDrawUtils.DrawBlueprintMeshNonInstanced(
                        drawOptions, mesh, trs, propertyBlock, resources.BlueprintPlaneMaterial);
                }
                else
                {
                    IslandsBlueprintDrawUtils.DrawBlueprintMeshNonInstanced(
                        drawOptions, mesh, trs, propertyBlock);
                }
            }
        }

        /// The zoomed-out map builds one static mesh, so a crossing that drew nothing here would
        /// disappear in the overview while every belt around it stayed.
        public void DrawOverview(MeshBuilder builder, in GlobalChunkTransform transform)
        {
            WorldCoordinate position = transform.Position.ToCenter_W(HeightOffset);
            builder.AddTranslateRotate(PathA.GetReducedMesh(Run), position, transform.Rotation);
            builder.AddTranslateRotate(
                PathB.GetReducedMesh(Run), position, transform.Rotation + TurnToB);
        }
    }
}
