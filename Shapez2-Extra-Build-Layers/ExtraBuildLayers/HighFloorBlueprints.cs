using System;
using System.Reflection;
using Game.Core.Coordinates;
using MonoMod.RuntimeDetour;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Stops placement previews going black on building layer 10 and up.
///
/// The cause is a constant compiled into the preview shader, not anything a mod can set.
/// <c>Shader Graphs/UXBuildingBlueprintShader</c> - the material
/// <c>SmartBuildingBlueprintRenderer</c> draws every building preview with - computes, from the
/// pixel's world height, disassembled:
///
/// <code>
/// m = (worldY - 10) mod 20        // positive modulo; 20 = one island layer
/// t = saturate(m - 9)             // 1 below height 10, 0 from 10 to 19
/// emission *= t;  alpha term *= t
/// </code>
///
/// so a preview's glow is switched off between heights 10 and 19 of every island layer - the
/// gap vanilla leaves above its three building layers. With more layers that "gap" is floors 10
/// to 19, and a preview there renders unlit, i.e. black. Placed buildings use the uber building
/// shader, which has no such band, which is why the same belt is fine once built. The 10 and 9
/// are literals in every compiled variant (68 programs, read with <c>D3DDisassemble</c> out of
/// <c>resources.assets</c>), so no material property moves them.
///
/// <c>UXIslandBlueprintShader</c> - the island placement preview - has no band, and takes the
/// same allowability colour block (<c>IslandsBlueprintDrawUtils.GetColorForAllowability</c> is
/// the same four <c>Theme*</c> colours). So a preview that reaches the band is drawn with the
/// island blueprint material instead. Floors below the band keep the vanilla material exactly.
///
/// The hook is the innermost <c>DrawBlueprintPart</c> overload, which every building preview
/// part - body and belt/pipe end caps, single placement and mass selection - ends in. Passing a
/// material override also skips vanilla's depth pre-pass, as any override already does.
/// </summary>
internal sealed class HighFloorBlueprints : IDisposable
{
    /// <summary>
    /// The first building layer whose mesh reaches height 10. A layer-9 building's top face sits
    /// at exactly 10.0, where <c>t</c> is already 0, so 9 is included.
    /// </summary>
    private const int FirstBandedLayer = 9;

    private delegate void DrawPart(
        FrameDrawOptions options, IMeshReference mesh, MaterialReference materialOverride, Matrix4x4 trs,
        in SmartBuildingBlueprintRenderer.DrawData entry, float cameraDistSq);

    private delegate void DrawPartHook(
        DrawPart orig, FrameDrawOptions options, IMeshReference mesh, MaterialReference materialOverride, Matrix4x4 trs,
        in SmartBuildingBlueprintRenderer.DrawData entry, float cameraDistSq);

    private delegate void HoverDraw(HUDBuildingMassSelection self, FrameDrawOptions options, BuildingModel building, float alpha);

    private delegate void HoverDrawHook(HoverDraw orig, HUDBuildingMassSelection self, FrameDrawOptions options, BuildingModel building, float alpha);

    /// <summary>
    /// A soft grey, for the hover - the blueprint material adds this as light, so a value that looks
    /// mid-grey on paper already reads as a pale sheen and 0.8 was glaring white. The game's own blueprint colours - will be placed, warning,
    /// impossible - are the bright blue, yellow and red a selection or a placement shows; a hover
    /// is meant to be a light sheen over the model, so it gets its own. Made on first use, on the
    /// main thread (a MaterialPropertyBlock cannot be built off it).
    /// </summary>
    private static readonly Color HoverTint = new Color(0.32f, 0.33f, 0.35f, 1f);

    private static IShaderColorProvider HoverColour;

    private readonly Hook Hook;
    private readonly Hook HoverHook;
    private readonly ILogger Logger;
    private bool Failed;

    public HighFloorBlueprints(ILogger logger)
    {
        Logger = logger;

        MethodInfo target = typeof(SmartBuildingBlueprintRenderer).GetMethod(
            "DrawBlueprintPart",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            null,
            new[]
            {
                typeof(FrameDrawOptions), typeof(IMeshReference), typeof(MaterialReference), typeof(Matrix4x4),
                typeof(SmartBuildingBlueprintRenderer.DrawData).MakeByRefType(), typeof(float),
            },
            null);

        if (target == null)
        {
            logger.Error?.Log("Extra Build Layers: SmartBuildingBlueprintRenderer.DrawBlueprintPart not found; previews on floor 10+ stay black.");
            return;
        }

        // Contained: this runs in the mod constructor, and a throw there is not caught by
        // ModLoader - it takes the game's whole mod loading step down with it.
        try
        {
            Hook = new Hook(target, new DrawPartHook(Detour));

            // The hover glow has the same problem: UXBuildingHoverShader carries the identical
            // band (disassembled: add v.y, -10; mod 20; add_sat -9), so a tall lift's hover stops
            // at floor 9. Declared on the concrete class, which is not generic.
            MethodInfo hover = typeof(HUDBuildingMassSelection).GetMethod(
                "Draw_HoverState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (hover != null)
            {
                HoverHook = new Hook(hover, new HoverDrawHook(HoverDetour));
            }
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            logger.Error?.Log("Extra Build Layers: could not hook DrawBlueprintPart; previews on floor 10+ stay black.");
        }
    }

    private void Detour(
        DrawPart orig, FrameDrawOptions options, IMeshReference mesh, MaterialReference materialOverride, Matrix4x4 trs,
        in SmartBuildingBlueprintRenderer.DrawData entry, float cameraDistSq)
    {
        if (materialOverride == null && !Failed)
        {
            try
            {
                if (ReachesBand(in entry))
                {
                    materialOverride = IslandBlueprintMaterial(options);
                }
            }
            catch (Exception exception)
            {
                // Once. This runs per preview part per frame; a throw here would otherwise flood
                // the log, and vanilla's black preview is the worst that not substituting costs.
                Failed = true;
                Logger.Exception?.LogException(exception);
            }
        }

        orig(options, mesh, materialOverride, trs, in entry, cameraDistSq);
    }

    /// <summary>
    /// The hover glow, for a building reaching the band, drawn with the island blueprint material
    /// instead of <c>UXBuildingHoverIndicatorMaterial</c>, the way a preview part is drawn, tinted a
    /// soft white (<see cref="HoverTint"/>) so it reads as a hover rather than a selection.
    /// </summary>
    private void HoverDetour(HoverDraw orig, HUDBuildingMassSelection self, FrameDrawOptions options, BuildingModel building, float alpha)
    {
        if (!Failed)
        {
            try
            {
                int layer = building.Tile_G.BuildingLayer();
                int top = building.Definition.CustomData.TryGet(out IBuildingConnectorData connectors)
                    ? layer + connectors.TileBounds.Max.z
                    : layer;
                MaterialReference material = top >= FirstBandedLayer ? IslandBlueprintMaterial(options) : null;
                if (material != null &&
                    building.Definition.CustomData.Get<IBuildingDrawData>().CombinedBlueprintMesh.TryGet(options.LOD.BuildingLOD, out IMeshReference handle))
                {
                    // Drawn exactly as SmartBuildingBlueprintRenderer.DrawBlueprintPart draws a
                    // preview part with this material - the buildings renderer, with the
                    // colour block a preview part gets - here a white one. The first version drew it through the hover's
                    // own call (non-instanced, AnalogUI, an alpha block only): the material then had
                    // no colours set, never showed from outside, and filled the screen yellow when
                    // the camera zoomed into the mesh.
                    WorldCoordinate translation = building.Transform.Position.ToCenter_W();
                    IShaderColorProvider colours = HoverColour ??= new FixedShaderColorProvider(HoverTint);
                    options.Renderers.Buildings.AddWithProperties(
                        handle, material, FastMatrix.TranslateRotate(in translation, building.Rotation_G),
                        colours.PropertyBlock, colours.PropertyBlockKey, options.LOD.Shadows, options.LOD.Shadows);
                    return;
                }
            }
            catch (Exception exception)
            {
                Failed = true;
                Logger.Exception?.LogException(exception);
            }
        }

        orig(self, options, building, alpha);
    }

    /// <summary>
    /// Whether any floor the building occupies is in the band. Decided per building rather than
    /// per part, so a tall lift from floor 2 to 12 is one material top to bottom instead of
    /// changing look partway up.
    /// </summary>
    private static bool ReachesBand(in SmartBuildingBlueprintRenderer.DrawData entry)
    {
        int layer = entry.Transform.Position.BuildingLayer();
        int top = layer;
        if (entry.Definition != null && entry.Definition.CustomData.TryGet(out IBuildingConnectorData connectors))
        {
            top = layer + connectors.TileBounds.Max.z;
        }

        return top >= FirstBandedLayer;
    }

    private static MaterialReference IslandBlueprintMaterial(FrameDrawOptions options)
    {
#pragma warning disable 0618   // FrameDrawOptionsNoLOD.Theme is [Obsolete] in favour of injection; a static hook has none.
        LODMaterialAsset asset = options.Theme?.BaseResources?.UXIslandBlueprintMaterial;
#pragma warning restore 0618
        return asset == null ? null : asset[options.LOD.IslandMaterialLOD] as MaterialReference;
    }

    public void Dispose()
    {
        Hook?.Dispose();
        HoverHook?.Dispose();
    }
}
