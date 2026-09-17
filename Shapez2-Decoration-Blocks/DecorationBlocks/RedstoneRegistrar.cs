using System.Collections.Generic;
using Core.Factory;
using Core.Localization;
using Game.Core.Content.Buildings;
using Game.Core.Coordinates;
using Game.Core.Simulation;
using QuinnBast.Shapez2.ToolbarKit;
using ShapezShifter.Flow;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// A second inert simulation, distinct from the blocks'.
///
/// It has to be a different type from `InertSimulation` for one reason: simulation renderers are
/// keyed by **simulation type**, and `SimulationsDrawer` logs an error and drops the second
/// renderer that claims a type. The blocks' renderer already claims `InertSimulation`, so the
/// components need their own marker to hang `RedstoneRenderer` off.
public sealed class RedstoneSimulation : ISimulation
{
    public static readonly RedstoneSimulation Shared = new RedstoneSimulation();

    private RedstoneSimulation() { }
}

internal sealed class RedstoneSimulationFactory : IFactory<RedstoneSimulation>
{
    public RedstoneSimulation Produce()
    {
        return RedstoneSimulation.Shared;
    }
}

internal sealed class RedstoneSimulationFactoryBuilder : IBuildingSimulationFactoryBuilder<RedstoneSimulation>
{
    private static readonly RedstoneSimulationFactory Factory = new RedstoneSimulationFactory();

    public IFactory<RedstoneSimulation> BuildFactory(SimulationSystemsDependencies dependencies)
    {
        return Factory;
    }
}

/// Registers the redstone components as buildings, the same way the blocks are registered, into
/// a toolbar category of their own.
internal static class RedstoneRegistrar
{
    public const string CategoryTitleId = "decoration-blocks.redstone-toolbar.title";

    /// Computed from the atlas rather than remembered from whichever registrar ran first.
    /// ToolbarKit's InNewCategory is find-or-create and only the first call's icon is used, so
    /// two registrars adding to the same category have to agree on it without depending on
    /// which of them gets there first.
    public static Sprite CategoryIcon(BlockAtlas atlas)
    {
        return Sprite.Create(
            atlas.Texture, atlas.PixelRectFor("redstone_torch"), new Vector2(0.5f, 0.5f));
    }

    private static readonly Dictionary<BuildingDefinitionId, RedstoneDefinition> ByDefinition =
        new Dictionary<BuildingDefinitionId, RedstoneDefinition>();

    public static bool TryGetComponent(BuildingDefinitionId id, out RedstoneDefinition definition)
    {
        return ByDefinition.TryGetValue(id, out definition);
    }

    public static void RegisterAll(BlockAtlas atlas, DecorationUnlock unlock, ILogger log)
    {
        Sprite categoryIcon = CategoryIcon(atlas);

        foreach (RedstoneDefinition component in RedstoneCatalog.All)
        {
            Register(component, atlas, Icon(atlas, component), categoryIcon, unlock, log);
        }

        log?.Info?.Log("Decoration blocks: registered " + RedstoneCatalog.All.Count + " redstone components.");
    }

    private static void Register(
        RedstoneDefinition component, BlockAtlas atlas, Sprite icon, Sprite categoryIcon,
        DecorationUnlock unlock, ILogger log)
    {
        BuildingDefinitionGroupId groupId = new BuildingDefinitionGroupId(component.Id + "RedstoneVariant");
        BuildingDefinitionId definitionId = new BuildingDefinitionId(component.Id + "RedstoneInternalVariant");
        ByDefinition[definitionId] = component;

        Mesh mesh = RedstoneMeshes.Build(component, atlas, active: false, variant: 1);
        TemporaryMeshReference drawn = new TemporaryMeshReference(mesh);
        // Wireframe ghost, real mesh underneath. See DecorationRegistrar for why.
        //
        // A torch frames its whole tile rather than its mesh. `EdgesOf` takes the bounds of the
        // shape it is given, and a standing torch is a thin column in the middle of its tile - so
        // the outline drew a narrow cage in the centre while the textured mesh under it moved to
        // whichever wall the rotation mounted against, and the two visibly disagreed. The
        // blueprint mesh is baked into the building's static draw data, once per definition, so
        // it cannot follow the rotation the way the textured mesh does. Framing the tile instead
        // gives an outline that is true at every rotation: both the upright torch and all four
        // mounted positions sit inside it, and the outline answers "which tile" while the mesh
        // inside answers "which wall".
        Mesh ghost = component.Kind == RedstoneKind.Torch
            ? BoxMesh.Build(
                new List<BoxMesh.Box>(BoxMesh.Edges(
                    new Vector3(-0.5f, 0.0f, -0.5f), new Vector3(0.5f, 0.7f, 0.5f),
                    component.Texture)),
                atlas, "RedstoneGhost_" + component.Id)
            : BoxMesh.EdgesOf(mesh, component.Texture, atlas, "RedstoneGhost_" + component.Id);
        LOD6Mesh lod = MeshLod.Create().AddLod0Mesh(ghost).BuildLod6Mesh();

        if (component.Kind == RedstoneKind.Torch)
        {
            // Five states, as they read on screen: upright in open air, and shoved against
            // whichever of the four walls the current rotation points its back at. There is no
            // separate "floor" rotation - a torch mounts when there is a block behind it and
            // stands when there is not - so the ghost does not choose a state, it predicts the
            // one the simulation is about to pick, using the same tile arithmetic.
            TemporaryMeshReference mounted = new TemporaryMeshReference(
                RedstoneMeshes.Build(component, atlas, active: false, variant: 2));

            PlacementPreview.Register(definitionId, transform =>
            {
                RedstoneWorld world = RedstoneWorld.Instance;

                return world != null
                    && world.HasBlockAt(RedstoneWorld.BehindOf(transform.Position, transform.Rotation))
                    ? mounted
                    : (IMeshReference)drawn;
            });
        }
        else
        {
            PlacementPreview.Register(definitionId, drawn);
        }

        RedstoneDrawData drawData = new RedstoneDrawData(component, atlas);

        IBuildingGroupBuilder group = BuildingGroup.Create(groupId)
           .WithTitle(new RawText(component.DisplayName))
           .WithDescription(new RawText(Describe(component)))
           .WithIcon(icon)
           .AsNonTransportableBuilding()

           // Single, not Area. A wall of dust is not a thing anybody wants by accident, and
           // rotation matters for three of the five - which is also why these stay auto-rotated
           // off but placeable in all four directions by hand.
           .WithPreferredPlacement(DefaultPreferredPlacementMode.Single)
           .NotAutoConnected()
           .NotAutoRotated()
           .AllowedToBeReplacedWithoutForce()
           .SkippingReplacementConnectorChecks()
           .NotRenderingConnectorIndicator()
           .NotRenderingConnectorConflictIndicator()
           .NotShowingNotchIndicators()
           .NotShowingInSpeedOverview()
           .NotShowingBeltProcessingTimeStat()
           .NotShowingBuildingsPerFullBeltStat()
           .Removable()
           .Selectable()
           .Buildable();

        // The converter is the only component with connectors: a wire in at the back and a wire
        // out at the front, so it can bridge both ways at once without a network feeding itself.
        IBuildingConnectorData connectors = component.Kind == RedstoneKind.Converter
            ? BuildingConnectors.SingleTile()
               .AddWireInput(WireConnectorConfig.DefaultInput())
               .AddWireOutput(WireConnectorConfig.DefaultOutput())
               .Build()
            : BuildingConnectors.SingleTile().Build();

        // `DynamicallyRendering` only attaches the draw data - Shifter ignores its TRenderer
        // argument entirely - so this call is not what binds the renderer. The binding is by
        // simulation type, done by the game's own scan; see RedstoneRendererBase. The type
        // argument is still written truthfully, because a reader will assume it means something.
        IBuildingBuilder building = component.Kind == RedstoneKind.Converter
            ? Building.Create(definitionId)
               .WithConnectorData(connectors)
               .DynamicallyRendering<RedstoneConverterRenderer, RedstoneConverterSimulation, RedstoneDrawData>(drawData)
               .WithStaticDrawData(CreateDrawData(lod, drawn, drawData))
               .WithoutSound()
               .WithoutSimulationConfiguration()
               .WithoutEfficiencyData()
            : Building.Create(definitionId)
               .WithConnectorData(connectors)
               .DynamicallyRendering<RedstoneRenderer, RedstoneSimulation, RedstoneDrawData>(drawData)
               .WithStaticDrawData(CreateDrawData(lod, drawn, drawData))
               .WithoutSound()
               .WithoutSimulationConfiguration()
               .WithoutEfficiencyData();

        // Only the converter has connectors, so only the converter can draw a conflict cross.
        if (component.Kind == RedstoneKind.Converter)
        {
            building = new SkipConflictMarkers(building);
        }

        AtomicBuildingExtender extender = (AtomicBuildingExtender)AtomicBuildings.Extend()
           .AllScenarios()
           .WithBuilding(building, group)
           .UnlockedWithExistingSideUpgrade(unlock)
           .WithDefaultPlacement()
           .InToolbar(ToolbarSlot.InNewCategory(CategoryTitleId, categoryIcon));

        // The converter gets a real simulation - it is the one component that talks to the
        // game's own signal layer - and everything else the inert marker the renderer keys on.
        if (component.Kind == RedstoneKind.Converter)
        {
            extender
               .WithSimulation(new RedstoneConverterFactoryBuilder(), log)
               .WithCustomModules(new RedstoneModules())
               .WithoutPrediction()
               .Build();

            return;
        }

        extender
           .WithSimulation(new RedstoneSimulationFactoryBuilder(), log)
           .WithCustomModules(new RedstoneModules())
           .WithoutPrediction()
           .Build();
    }

    private static string Describe(RedstoneDefinition component)
    {
        switch (component.Kind)
        {
            case RedstoneKind.Dust:
                return "Carries power, losing one strength per tile. Runs up and down a layer.";
            case RedstoneKind.Torch:
                return "A power source standing free; an inverter when mounted on a block behind it.";
            case RedstoneKind.Repeater:
                return "A one-way delay of 1 to 4 ticks. Click to change the delay. Locks from the side.";
            case RedstoneKind.Lever:
                return "Click to latch power on and off.";
            case RedstoneKind.Button:
                return "Click for a one second pulse.";
            case RedstoneKind.Lamp:
                return "Lights while powered. Carries nothing onward.";
            case RedstoneKind.Comparator:
                return "Compares or subtracts its side inputs from its back input. Click to switch mode.";
            case RedstoneKind.Observer:
                return "Pulses out of its back whenever the tile it faces changes.";
            case RedstoneKind.Converter:
                return "Both ways at once: a wire signal that is not Off powers sparkstone, and "
                       + "sparkstone power puts true on the wire. Wire in at the back, wire out at the front.";
            default:
                return "A sparkstone component.";
        }
    }

    /// Same slots as a decoration block: empty main mesh so nothing draws through the shared
    /// building material, everything else real so the component can be clicked, blueprinted and
    /// seen in overview.
    private static BuildingDrawData CreateDrawData(
        LOD6Mesh lod, IMeshReference preview, RedstoneDrawData drawData)
    {
        LODEmptyMesh nothing = new LODEmptyMesh();

        return new BuildingDrawData(
            renderVoidBelow: false,
            new ILODMesh[] { nothing, nothing, nothing },
            lod,
            lod,
            preview,
            new LODEmptyMesh(),
            new[] { new CollisionBox(new SerializedCollisionBox()) },
            drawData,
            hasCustomOverviewMesh: false,
            null,
            simulationRendererDrawsMainMesh: true);
    }

    private static Sprite Icon(BlockAtlas atlas, RedstoneDefinition component)
    {
        return Sprite.Create(
            atlas.Texture, atlas.PixelRectFor(component.IconTexture), new Vector2(0.5f, 0.5f));
    }
}

/// Keeps RedstoneWorld in step with the map, and drives the tick.
///
/// Binding lazily rather than at mod load: the map does not exist until a session does, and a
/// session can be left and re-entered. Noticing that the map changed and rebuilding from
/// `map.Buildings` is both the initial fill and the reset, so there is one path rather than two.
internal sealed class RedstoneTicker : ITickRewirer
{
    private readonly ILogger Log;

    private IMapModel Bound;

    public RedstoneTicker(ILogger log)
    {
        Log = log;
    }

    public void Tick(float deltaTime)
    {
        IMapModel map = GameHelper.Core?.LocalPlayer?.CurrentMap;

        if (!ReferenceEquals(map, Bound))
        {
            Rebind(map);
        }

        if (map != null)
        {
            RedstoneWorld.Instance.Advance(deltaTime);
        }
    }

    private void Rebind(IMapModel map)
    {
        if (Bound != null)
        {
            Bound.OnBuildingAdded.Unregister(Added);
            Bound.OnBeforeBuildingRemoved.Unregister(Removed);
        }

        Bound = map;
        RedstoneWorld.Instance.Clear();

        if (map == null)
        {
            return;
        }

        foreach (BuildingModel building in map.Buildings)
        {
            Added(building);
        }

        map.OnBuildingAdded.Register(Added);
        map.OnBeforeBuildingRemoved.Register(Removed);

        Log?.Info?.Log(
            "Decoration blocks: redstone bound to a new map, "
            + RedstoneWorld.Instance.ComponentCount + " components.");
    }

    private void Added(BuildingModel building)
    {
        BuildingDefinitionId id = building.Definition.Id;

        if (RedstoneRegistrar.TryGetComponent(id, out RedstoneDefinition component))
        {
            RedstoneWorld.Instance.Register(building.Tile_G, component, building.Rotation_G);
        }
        else if (DecorationRegistrar.IsDecorationBlock(id))
        {
            RedstoneWorld.Instance.RegisterBlock(
                building.Tile_G, DecorationRegistrar.IsRedstoneBlock(id));
        }
    }

    private void Removed(BuildingModel building)
    {
        BuildingDefinitionId id = building.Definition.Id;

        if (RedstoneRegistrar.TryGetComponent(id, out RedstoneDefinition _))
        {
            RedstoneWorld.Instance.Unregister(building.Tile_G);
        }
        else if (DecorationRegistrar.IsDecorationBlock(id))
        {
            RedstoneWorld.Instance.UnregisterBlock(building.Tile_G);
        }
    }
}
