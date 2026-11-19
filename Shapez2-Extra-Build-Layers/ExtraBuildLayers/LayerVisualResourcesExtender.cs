using System;
using Game.Core.Rendering.MeshGeneration;
using ShapezShifter.Hijack;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Lengthens the two authored arrays that decide what a building layer *looks* like.
///
/// Both are indexed by building layer and both were authored for three of them. Neither
/// throws when asked for more - which is why the symptom is cosmetic and confusing rather
/// than a crash:
///
/// <list type="bullet">
/// <item><c>Globals.Resources.LayerColors</c> is the per-layer colour ramp.
/// <c>LayerRenderingController.GenerateLayerTexture</c> uploads it as the global
/// <c>_G_LayerColorTexture</c> and publishes its length as <c>_G_LayerCount</c>, and
/// <c>LerpArray</c> reads it with <c>math.clamp(layer, 0, Length - 1)</c>. Past the end every
/// floor is the same colour as the top authored one.</item>
/// <item><c>VisualThemeBaseResources.PlayingfieldCurrentLayerPlaneMaterialPerLayer</c> is the
/// floor plane drawn for each storey. <c>IslandPlayingFieldLayersDrawer.Draw</c> picks
/// <c>[math.min(i - 1, Length - 1)]</c> - the same clamp, so every storey above the third
/// renders with the third one's material and the layer readout stops changing.</item>
/// </list>
///
/// Neither needs a hook. They are plain public arrays, and this runs as an
/// <see cref="IBuildingsRewirer"/> - inside <c>GameMode.From</c>, which is before
/// <c>GameSessionOrchestrator</c> constructs the <c>LayerRenderingController</c> that reads
/// <c>LayerColors</c> and builds the texture from it. Lengthen it here and the game generates
/// a correctly sized texture on its own.
///
/// The extension **cycles** the authored entries rather than inventing colours. Floor 3 then
/// looks like floor 0, floor 4 like floor 1, and so on: a repeating ladder reads as height,
/// which is the job, and it keeps the ramp and the floor planes in step because both cycle
/// with the same period.
/// </summary>
internal class LayerVisualResourcesExtender : IBuildingsRewirer
{
    private readonly ILogger Logger;
    private readonly bool Enabled;

    public LayerVisualResourcesExtender(ILogger logger, bool enabled)
    {
        Logger = logger;
        Enabled = enabled;
    }

    public GameBuildings ModifyGameBuildings(
        AuthoringBuildings metaBuildings, GameBuildings gameBuildings, IMeshCache meshCache, VisualThemeBaseResources theme)
    {
        LayerVisualsDump.Theme = theme;

        // Not behind the ramp switch: a stand hanging in the air is a geometry fault, not a
        // shading choice.
        try
        {
            TallStands.Extend(theme, Logger);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }

        if (!Enabled)
        {
            Logger.Info?.Log("Extra Build Layers: layer ramp extension disabled by config.");
            return gameBuildings;
        }

        // One slot per valid layer: layers run 0..MaxBuildingLayer inclusive, and
        // MaxBuildingLayer is BuildingLayerUnlocks.Count, so the count is that plus one.
        int wanted = Math.Min(BuildLayers.ExtraMachineLevels, BuildLayers.MaxMachineLevel) + 3 + 1;

        try
        {
            ExtendLayerColors(wanted);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log("Extra Build Layers: could not extend the layer colour ramp; upper floors will share the top colour.");
        }

        try
        {
            ExtendPlayingFieldMaterials(theme, wanted);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log("Extra Build Layers: could not extend the playing-field layer materials; upper floors will share the top one.");
        }

        try
        {
            // Independent of the layer count: the grid's last slice is redrawn to whichever
            // layer is on screen, by LiveGridNumber.
            LayerNumberArt.PrepareGridIndicator(theme, Logger);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log("Extra Build Layers: could not extend the space grid's layer numbers; layers above 2 keep showing 2.");
        }

        return gameBuildings;
    }

    private void ExtendLayerColors(int wanted)
    {
#pragma warning disable 0618   // Globals.Resources is [Obsolete("Access via dependency injection only.")] - there is no injection point for a mod here.
        GameResources resources = Globals.Resources;
#pragma warning restore 0618

        resources.LayerColors = Cycled(resources?.LayerColors, wanted, "layer colour ramp");

        // The belt arrow ramp has to grow with it, and this is not cosmetic parity - it is the
        // reason belts and pipes render black on high floors.
        //
        // GenerateLayerTexture publishes `_G_LayerCount` from LayerColors.Length, and that is the
        // **only** layer count any shader gets. GenerateBeltArrowTexture builds a second texture
        // from BeltArrowColors and publishes no count of its own, so the two arrays are implicitly
        // required to be the same length. Lengthening one and not the other leaves every sample of
        // the arrow ramp reading off the end of a texture that is now a fraction of the height the
        // shader believes it to be.
        resources.BeltArrowColors = Cycled(resources?.BeltArrowColors, wanted, "belt arrow ramp");
    }

    /// <summary>
    /// Repeats an authored ramp up to <paramref name="wanted"/> entries, or returns it unchanged
    /// when it is already long enough. Cycling rather than extrapolating keeps every entry a
    /// colour an artist chose.
    /// </summary>
    private Color[] Cycled(Color[] existing, int wanted, string label)
    {
        if (existing == null || existing.Length == 0)
        {
            Logger.Error?.Log("Extra Build Layers: no " + label + " to extend.");
            return existing;
        }

        if (existing.Length >= wanted)
        {
            Logger.Info?.Log(
                "Extra Build Layers: " + label + " already has " + existing.Length +
                " entries for " + wanted + " needed; left alone.");
            return existing;
        }

        Color[] extended = new Color[wanted];
        for (int i = 0; i < wanted; i++)
        {
            extended[i] = existing[i % existing.Length];
        }

        Logger.Info?.Log(
            "Extra Build Layers: " + label + " " + existing.Length + " -> " + wanted + " entries.");
        return extended;
    }

    private void ExtendPlayingFieldMaterials(VisualThemeBaseResources theme, int wanted)
    {

        MaterialReference[] existing = theme?.PlayingfieldCurrentLayerPlaneMaterialPerLayer;
        if (existing == null || existing.Length == 0 || existing.Length >= wanted)
        {
            return;
        }

        // A material per new floor, labelled with its own number (LayerNumberArt). Cycling the
        // authored two is the fallback if the vanilla art cannot be measured: every floor still
        // draws, it just reads "01"/"02" again.
        int painted = 0;
        MaterialReference[] extended = new MaterialReference[wanted];
        for (int i = 0; i < wanted; i++)
        {
            MaterialReference numbered = null;
            if (i >= existing.Length && existing.Length >= 2)
            {
                try
                {
                    numbered = LayerNumberArt.FloorMaterial(i, existing[0], existing[1], Logger);
                }
                catch (Exception exception)
                {
                    Logger.Exception?.LogException(exception);
                }
            }

            if (numbered != null)
            {
                painted++;
            }

            extended[i] = numbered ?? existing[i % existing.Length];
        }

        theme.PlayingfieldCurrentLayerPlaneMaterialPerLayer = extended;
        Logger.Info?.Log(
            "Extra Build Layers: playing-field layer materials " + existing.Length + " -> " + wanted + " entries, " +
            painted + " with their own number.");
    }
}
