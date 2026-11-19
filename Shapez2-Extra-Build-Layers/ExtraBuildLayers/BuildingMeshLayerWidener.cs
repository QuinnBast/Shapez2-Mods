using System;
using System.Collections.Generic;
using Game.Core.Rendering.MeshGeneration;
using Game.Core.Simulation;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Makes machine levels above the third possible at all.
///
/// A building's world mesh is picked per layer:
/// <code>
/// // StaticBuildingMeshBuilder
/// ILODMesh mesh = building.Definition.CustomData.Get&lt;IBuildingDrawData&gt;()
///                     .MainMeshPerLayer[tile_G.BuildingLayer()];
/// </code>
/// and that array is built to a hardcoded length:
/// <code>
/// // BuildingDrawDataFactory.FromMeta
/// int num = 3;
/// ILODMesh[] array = new ILODMesh[num];
/// </code>
/// So a building on layer 3 indexes past the end. The throw happens inside the chunk's mesh
/// build, which means it does not just make one machine invisible - it aborts the build for
/// the whole chunk and everything there disappears. That single hardcoded 3, not the layer
/// logic, is the reason extra machine levels have been considered impossible.
///
/// It is also the only place the array is indexed by layer, so widening it is the entire fix.
/// The new slots repeat the topmost authored mesh rather than staying null: for a building
/// without <c>IndividualMainMeshPerLayer</c> - which is most of them - every slot already
/// holds the same mesh, so repeating is exactly what the factory would have produced had its
/// loop run longer. For the ones that do author a mesh per layer, repeating the top layer's
/// is a deliberate approximation: the alternative is <c>LODEmptyMesh</c>, and an invisible
/// building the player can still place is worse than one whose supports look wrong.
///
/// This runs as an <see cref="IBuildingsRewirer"/>, which Shifter fires straight after
/// <c>GameModeBuildingsFactory.FromMetadata</c> - the moment every definition exists with its
/// draw data attached and nothing has drawn anything yet.
///
/// It also runs a **second time**, from <see cref="LateBuildingMeshLayerWidener"/>, because
/// rewirers run in registration order and registration follows mod load order. A mod that
/// depends on this one loads after it, so its own buildings are added by rewirers that run after
/// this one - and would keep a three-slot array and take their chunk down on floor 3. The second
/// pass comes after every buildings rewirer. Hence sizing to an absolute target and skipping
/// anything already that long: running twice must not widen twice.
/// </summary>
internal class BuildingMeshLayerWidener : IBuildingsRewirer
{
    /// <summary><c>BuildingDrawDataFactory.FromMeta</c> builds the array to exactly this length.</summary>
    private const int VanillaSlots = 3;

    private readonly ILogger Logger;

    public BuildingMeshLayerWidener(ILogger logger)
    {
        Logger = logger;
    }

    public GameBuildings ModifyGameBuildings(
        AuthoringBuildings metaBuildings, GameBuildings gameBuildings, IMeshCache meshCache, VisualThemeBaseResources theme)
    {
        WidenAll(gameBuildings, Logger, "");
        return gameBuildings;
    }

    /// <summary>
    /// Widens every definition shorter than the target. Safe to call any number of times; the
    /// return value is how many it actually changed.
    /// </summary>
    internal static int WidenAll(GameBuildings gameBuildings, ILogger logger, string phase)
    {
        // Read from BuildLayers, not from the config. The config only seeds it, and a dependent
        // mod that raised the count would otherwise get arrays sized for the config's number and
        // a chunk-killing IndexOutOfRange on its own top floor. Over-allocating is harmless;
        // under-allocating is the exact bug this class exists to prevent.
        int extra = Math.Min(BuildLayers.ExtraMachineLevels, BuildLayers.MaxMachineLevel);
        if (extra <= 0 || gameBuildings == null)
        {
            return 0;
        }

        int target = VanillaSlots + extra;

        int widened = 0;
        int skipped = 0;

        // Decorations live in their own list and are not in _DefinitionsById, so both have to
        // be walked or a decoration on the new layer takes its chunk down exactly as before.
        foreach (IBuildingDefinitionGroup group in Concat(gameBuildings.All, gameBuildings.AllDecorations))
        {
            foreach (IBuildingDefinition definition in group.Definitions)
            {
                if (TryWiden(definition, target))
                {
                    widened++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        // The late pass normally finds nothing; only say so when it did something.
        if (phase.Length == 0 || widened > 0)
        {
            logger.Info?.Log(
                "Extra Build Layers: " + phase + "widened MainMeshPerLayer on " + widened + " building definition(s) to " +
                target + " slots; " + skipped + " already wide enough or not widenable.");
        }

        return widened;
    }

    private static bool TryWiden(IBuildingDefinition definition, int target)
    {
        // The interface only exposes CustomData as a reader. The concrete BuildingDefinition
        // holds the writable CustomDataHolder, and every definition the factory produces is
        // one - but a mod could have registered something else, so this is a soft cast.
        if (definition is not BuildingDefinition concrete)
        {
            return false;
        }

        if (!concrete.CustomData.TryGet(out IBuildingDrawData existing) || existing.MainMeshPerLayer == null)
        {
            return false;
        }

        ILODMesh[] source = existing.MainMeshPerLayer;
        if (source.Length == 0 || source.Length >= target)
        {
            return false;
        }

        ILODMesh[] widened = new ILODMesh[target];
        for (int i = 0; i < source.Length; i++)
        {
            widened[i] = source[i];
        }

        // The last *non-empty* slot, not simply the last one. A building whose authored
        // MainMeshPerLayerLOD is shorter than three - a two-layer lift can only start on the
        // bottom floor, so it authors one - gets LODEmptyMesh in the trailing slots from
        // ComputeMainMesh. Repeating that propagates "invisible" to every floor above, which
        // looks exactly like the mesh array was never widened at all.
        ILODMesh fill = null;
        for (int i = source.Length - 1; i >= 0; i--)
        {
            if (source[i] != null && source[i].Count > 0)
            {
                fill = source[i];
                break;
            }
        }

        // Every slot empty means the building genuinely has no main mesh - a decoration drawn
        // by its simulation renderer, say. Leave the trailing slots as the game made them.
        fill ??= source[source.Length - 1];

        for (int i = source.Length; i < widened.Length; i++)
        {
            widened[i] = fill;
        }

        // Rebuilt rather than mutated: MainMeshPerLayer is a get-only property, and every
        // other field is carried across by reference - including CustomDrawData, which the
        // old instance's Dispose owns. The old instance is dropped, never disposed, or that
        // shared draw data would be torn down under the new one.
        BuildingDrawData replacement = new BuildingDrawData(
            existing.RenderVoidBelow,
            widened,
            existing.IsolatedBlueprintMesh,
            existing.CombinedBlueprintMesh,
            existing.PreviewMesh,
            existing.GlassMesh,
            existing.Colliders,
            existing.CustomDrawData,
            existing.HasCustomOverviewMesh,
            existing.CustomOverviewMesh,
            existing.SimulationRendererDrawsMainMesh);

        concrete.CustomData.AttachOrReplace<IBuildingDrawData>(replacement);
        return true;
    }

    private static IEnumerable<IBuildingDefinitionGroup> Concat(
        IReadOnlyList<IBuildingDefinitionGroup> first, IReadOnlyList<IBuildingDefinitionGroup> second)
    {
        if (first != null)
        {
            foreach (IBuildingDefinitionGroup group in first)
            {
                yield return group;
            }
        }

        if (second != null)
        {
            foreach (IBuildingDefinitionGroup group in second)
            {
                yield return group;
            }
        }
    }
}

/// <summary>
/// The second pass of <see cref="BuildingMeshLayerWidener"/>, for buildings added by mods that
/// load after this one.
///
/// A simulation-systems rewirer because that is the first Shifter phase guaranteed to come after
/// every buildings rewirer: it is a postfix on <c>BuiltinSimulationSystems.CreateSimulationSystems</c>,
/// which session setup reaches with <c>GameMode</c> already built, and before any chunk mesh is.
/// It touches no simulation systems.
/// </summary>
internal sealed class LateBuildingMeshLayerWidener : ISimulationSystemsRewirer
{
    private readonly ILogger Logger;

    public LateBuildingMeshLayerWidener(ILogger logger)
    {
        Logger = logger;
    }

    public void ModifySimulationSystems(
        ICollection<ISimulationSystem> simulationSystems,
        SimulationSystemsDependencies dependencies)
    {
        try
        {
            BuildingMeshLayerWidener.WidenAll(dependencies?.Mode?.Buildings, Logger, "late pass: ");
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }
}
