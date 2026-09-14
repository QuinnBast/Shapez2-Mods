using System.Collections.Generic;
using UnityEngine;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// Builds the runtime <see cref="MetaShapeSubPart"/> for a profile, once per process **per
    /// part count**.
    ///
    /// A shape part mesh is authored for exactly `360 / PartCount` degrees and the renderer never
    /// squashes one angularly, so one profile needs one `MetaShapeSubPart` per distinct
    /// `PartCount` - a 90 degree instance for the two quad configurations and a 60 degree one for
    /// the hexagonal configuration. They share a code, which sounds like a collision and is not:
    /// vanilla does the same thing, with `PinQuad` and `PinHex` both coded `P` and `CrystalQuad`
    /// and `CrystalHex` both coded `c`. `GameSessionOrchestrator.Init_7_Rendering` seeds its
    /// `char -> IShapeSubPart` dictionary from the *current mode's* configuration and fills in the
    /// others only `if (!dictionary.ContainsKey(part.Code))`, so the instance matching the
    /// configuration being played always wins.
    ///
    /// It has to be a real `MetaShapeSubPart` and not some other <c>IShapeSubPart</c>: both
    /// <c>ShapeItemRenderer.GetCachedSubPartMesh</c> and <c>HUDShapeViewer.GenerateShapePartMeshes</c>
    /// hard cast the interface back to the ScriptableObject to reach its meshes, so a custom
    /// implementation registers fine and then throws an InvalidCastException the first time a shape
    /// carrying it is drawn.
    public static class ShapePartFactory
    {
        private static readonly Dictionary<(char Code, int PartCount), MetaShapeSubPart> Built =
            new Dictionary<(char, int), MetaShapeSubPart>();

        public static MetaShapeSubPart GetOrCreate(ShapePartProfile profile, int partCount)
        {
            if (Built.TryGetValue((profile.Code, partCount), out MetaShapeSubPart existing))
            {
                return existing;
            }

            Mesh mesh = ShapePartMeshBuilder.Build(profile, ShapeGeometry.Sector(partCount));
            mesh.hideFlags = HideFlags.HideAndDontSave;

            MetaShapeSubPart part = ScriptableObject.CreateInstance<MetaShapeSubPart>();

            // Nothing else holds these, and a scene change runs Resources.UnloadUnusedAssets, which
            // collects an unreferenced ScriptableObject and leaves the registered part pointing at a
            // destroyed Unity object.
            part.hideFlags = HideFlags.HideAndDontSave;
            // The part count is in the name because two instances share a code, and `esp.dump`,
            // the Unity object list and any log line that prints a name would otherwise show two
            // identical entries with different meshes.
            part.name = $"ShapePart_{profile.Name}_{partCount}";

            part._Code = profile.Code;
            part._AllowColor = true;
            part._AllowChangingColor = true;

            // Only the crystal sets this; a part that destroys itself when unsupported is a
            // behaviour, not a look, and these are meant to be ordinary shapes.
            part._DestroyOnFallDown = false;

            // Leaving this off is what makes the renderer read the per-vertex colours instead of
            // painting the whole part one material - which is where the outline comes from.
            part.OverrideMaterial = false;

            LODMeshAsset lodAsset = ScriptableObject.CreateInstance<LODMeshAsset>();
            lodAsset.hideFlags = HideFlags.HideAndDontSave;
            lodAsset.name = $"ShapePartLod_{profile.Name}_{partCount}";

            // One mesh in every slot. A part is a few hundred triangles before the renderer
            // merges it into the item mesh, so there is nothing for a lower LOD to save.
            lodAsset.Meshes = ShapezShifter.Kit.MeshLod.Create().AddLod0Mesh(mesh).BuildLod6Mesh();
            part.Mesh = lodAsset;

            // The shape viewer dialog reads HighDetailMesh directly and does not null check it.
            part.HighDetailMesh = new UnityMeshReference { _Mesh = mesh };

            Built.Add((profile.Code, partCount), part);
            return part;
        }
    }
}
