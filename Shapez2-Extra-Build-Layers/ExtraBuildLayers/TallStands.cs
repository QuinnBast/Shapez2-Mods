using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ILogger = Core.Logging.ILogger;
using Object = UnityEngine.Object;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Makes the stands under belts, pipes and wires reach the floor from any height.
///
/// A stand is not part of the building's mesh. <c>StaticBuildingMeshBuilder</c> adds one per
/// connector from a theme array indexed by height:
/// <code>
/// // BuildBeltEndCaps
/// builder.AddTranslateRotate(ThemeResources.BeltCapStandsNormal.GetSafe(standIndex - 1), …);
/// // BuildPipeEndCaps / BuildWireEndCaps
/// ThemeResources.PipeStandsBetweenPipes.GetSafe(standIndex)
/// ThemeResources.PipeStandsAndEndCap.GetSafe(layer)
/// </code>
/// where <c>standIndex</c> is the building layer, plus one when the tile is over the void. The
/// arrays are authored for vanilla's three layers and <c>GetSafe</c> clamps, so a belt on floor
/// ten gets the two-floor stand and it hangs in the air.
///
/// Each authored stand is the one before it with a longer column, so the missing ones are made
/// the same way: the tallest authored stand, with every vertex below the middle of its bottom
/// layer moved down by the extra height. The column's side faces span that cut and stretch; the
/// foot below it moves down intact. The building shader colours by a palette lookup on UV0, not
/// by a texture, so stretched faces keep their colour.
///
/// Which arrays are stands is decided by their geometry rather than a list of names: an array
/// qualifies when each entry's lowest point is one step below the previous one's. That also
/// measures the step, rather than assuming a layer is one unit tall.
/// </summary>
internal static class TallStands
{
    /// <summary>Layer 19, plus one for a stand over the void, plus one for the belt's index offset.</summary>
    private static readonly int Target = BuildLayers.MaxMachineLevel + 3;

    public static void Extend(VisualThemeBaseResources theme, ILogger logger)
    {
        if (theme == null)
        {
            return;
        }

        List<string> extended = new List<string>();
        Dictionary<(Mesh, int), Mesh> made = new Dictionary<(Mesh, int), Mesh>();

        foreach (FieldInfo field in typeof(VisualThemeBaseResources).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType != typeof(LODMeshAsset[]) || field.GetValue(theme) is not LODMeshAsset[] { Length: >= 2 } authored ||
                authored.Length >= Target)
            {
                continue;
            }

            try
            {
                if (!TryMeasureStep(authored, out float step))
                {
                    continue;
                }

                LODMeshAsset tallest = authored[authored.Length - 1];
                LODMeshAsset[] longer = new LODMeshAsset[Target];
                Array.Copy(authored, longer, authored.Length);
                for (int i = authored.Length; i < Target; i++)
                {
                    longer[i] = Stretched(tallest, step, (i - (authored.Length - 1)) * step, made);
                }

                field.SetValue(theme, longer);
                extended.Add(field.Name + " " + authored.Length + "->" + Target);
            }
            catch (Exception exception)
            {
                logger.Exception?.LogException(exception);
                logger.Error?.Log("Extra Build Layers: could not extend " + field.Name + "; those stands stay two floors tall.");
            }
        }

        if (extended.Count > 0)
        {
            logger.Info?.Log("Extra Build Layers: stands extended to every floor: " + string.Join(", ", extended) + ".");
        }
    }

    /// <summary>
    /// True when the entries reach lower and lower - the signature of a stand ladder - with the
    /// step per entry in <paramref name="step"/>. Caps and connectors of fixed height fail this and
    /// are left alone.
    ///
    /// The step is the average over the whole ladder, rounded to a quarter floor - not the drop
    /// between the last two. The authored art is not evenly spaced: the belt stands bottom out at
    /// 1.007, 2.250 and 3.007, so the last two are 0.757 apart and a ladder continued from that
    /// fell a quarter floor short per floor, leaving stands about two floors short of the ground by
    /// floor 10. Averaged and rounded, every stand ladder the game ships comes out at exactly 1.
    /// </summary>
    private static bool TryMeasureStep(LODMeshAsset[] authored, out float step)
    {
        step = 0f;
        float first = 0f;
        float previous = float.MaxValue;
        for (int i = 0; i < authored.Length; i++)
        {
            Mesh mesh = MeshAt(authored[i], 0);
            if (mesh == null)
            {
                return false;
            }

            float bottom = mesh.bounds.min.y;
            if (bottom > previous + 0.01f)
            {
                return false;
            }

            if (i == 0)
            {
                first = bottom;
            }

            previous = bottom;
        }

        float average = (first - previous) / (authored.Length - 1);
        step = Mathf.Round(average * 4f) / 4f;
        return step >= 0.25f;
    }

    private static LODMeshAsset Stretched(LODMeshAsset tallest, float step, float extra, Dictionary<(Mesh, int), Mesh> made)
    {
        // A copy through Unity's own serialization: the LOD wrapper and its mesh references come
        // out as fresh objects, so pointing them at new meshes leaves the authored asset alone.
        LODMeshAsset copy = Object.Instantiate(tallest);
        copy.name = tallest.name + "+" + Mathf.RoundToInt(extra / step);

        LOD6Mesh lods = copy.Meshes;
        foreach (UnityMeshReference reference in new[]
                 {
                     lods.LODClose, lods.LODNormal, lods.LODFar, lods.LODReduced, lods.LODMinimal, lods.LODOverview,
                 })
        {
            Mesh source = reference?._Mesh;
            if (source == null)
            {
                continue;
            }

            int key = Mathf.RoundToInt(extra * 1000f);
            if (!made.TryGetValue((source, key), out Mesh mesh))
            {
                mesh = StretchDown(source, step, extra);
                made[(source, key)] = mesh;
            }

            reference._Mesh = mesh;
        }

        copy.ClearCache();
        return copy;
    }

    private static Mesh StretchDown(Mesh source, float step, float extra)
    {
        if (!source.isReadable)
        {
            // Nothing to stretch; the stand keeps its authored height rather than vanishing.
            return source;
        }

        Mesh mesh = Object.Instantiate(source);
        mesh.name = source.name + "_ExtraBuildLayers";

        float cut = source.bounds.min.y + step * 0.5f;
        Vector3[] vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            if (vertices[i].y < cut)
            {
                vertices[i].y -= extra;
            }
        }

        mesh.vertices = vertices;
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Mesh MeshAt(LODMeshAsset asset, int lod)
    {
        return asset != null && asset.TryGet(lod, out IMeshReference reference) && reference is UnityMeshReference unity
            ? unity._Mesh
            : null;
    }
}
