using ShapezShifter.Flow.Toolbar;
using Game.Core.Content.Buildings;
using System.Collections.Generic;
using Core.Localization;
using QuinnBast.Shapez2.ToolbarKit;
using ShapezShifter.Flow;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// Turns the catalog into registered buildings.
///
/// One group per block, which is not a choice. BuildingGroupBuilder.BuildAndRegister ends in
/// `gameBuildings._VariantsById.Add(group.Id, group)` - a Dictionary.Add, not an indexer - so
/// running the chain twice against one group id crashes the session on a duplicate key. 21
/// blocks are therefore 21 groups and 21 toolbar entries, which is also what the game
/// does with its own decorations: BuildingDefinitionGroupFactory builds one group per
/// MetaDecorationBuildingDefinition, each with a single internal variant.
///
/// The toolbar clutter that would otherwise cause is handled by giving them a category of their
/// own rather than by sharing a group.
internal static class DecorationRegistrar
{
    private const string CategoryTitleId = "decoration-blocks.toolbar.title";

    /// Every definition this mod registers, so the stacking rule can tell "the cursor is over
    /// one of ours" from "the cursor is over a machine". Filled during registration, which
    /// happens once at mod load, so it is read-only for the life of the process.
    private static readonly HashSet<BuildingDefinitionId> BlockDefinitions =
        new HashSet<BuildingDefinitionId>();

    public static bool IsDecorationBlock(BuildingDefinitionId id)
    {
        return BlockDefinitions.Contains(id);
    }

    /// The block of redstone, which is already in the decoration catalog and which the redstone
    /// world treats as a permanently powered source. Matched on the definition rather than
    /// hardcoded elsewhere so the catalog stays the single place a block is named.
    private static BuildingDefinitionId RedstoneBlockId;

    public static bool IsRedstoneBlock(BuildingDefinitionId id)
    {
        return id.Equals(RedstoneBlockId);
    }

    public static void RegisterAll(BlockAtlas atlas, DecorationUnlock unlock, ILogger log)
    {
        // The first block's texture stands for the whole category. AddEntry runs once per
        // registered entry and ToolbarKit's find-or-create returns the same category every
        // time, so only the first call's icon is ever used - passing each block's own would
        // make the category icon depend on registration order.
        Sprite categoryIcon = CutIcon(atlas, BlockCatalog.All[0]);

        foreach (BlockDefinition block in BlockCatalog.All)
        {
            Register(block, atlas, CutIcon(atlas, block), categoryIcon, unlock, log);
        }

        log?.Info?.Log("Decoration blocks: registered " + BlockCatalog.All.Count + " blocks.");
    }

    private static void Register(
        BlockDefinition block, BlockAtlas atlas, Sprite icon, Sprite categoryIcon,
        DecorationUnlock unlock, ILogger log)
    {
        BuildingDefinitionGroupId groupId = new BuildingDefinitionGroupId(block.Id + "BlockVariant");
        BuildingDefinitionId definitionId = new BuildingDefinitionId(block.Id + "BlockInternalVariant");
        BlockDefinitions.Add(definitionId);

        if (block.Id == "SparkstoneBlock")
        {
            RedstoneBlockId = definitionId;
        }

        Mesh cube = CubeMesh.Build(block, atlas);
        TemporaryMeshReference drawnMesh = new TemporaryMeshReference(cube);

        // Never disposed, deliberately. TemporaryMeshReference.Dispose destroys the underlying
        // Mesh, and these live for the process - one per block type, 24 vertices each. The class
        // warns about undisposed instances in principle; WarnAboutNonDisposedInstances is an
        // empty method in the shipped build.

        // The blueprint slot gets a **wireframe**, not the cube. That slot is what the game's
        // placement ghost draws, in flat blue, and a solid blue one by one by one box hides the
        // block it is previewing - with twenty-one blocks that all ghost identically, there is no
        // telling which is on the cursor. PlacementPreview draws the real textured mesh inside it.
        Mesh ghost = BoxMesh.EdgesOf(cube, block.SideTexture, atlas, "DecorationGhost_" + block.Id);
        LOD6Mesh cubeLod = MeshLod.Create().AddLod0Mesh(ghost).BuildLod6Mesh();

        PlacementPreview.Register(definitionId, drawnMesh);

        // One instance, handed to both the DynamicallyRendering stage and the BuildingDrawData.
        // CustomDataHolder.Attach is a list append and Get<T> returns the first assignable
        // entry, so attaching two BlockDrawData objects would leave which one the renderer sees
        // up to insertion order.
        BlockDrawData drawData = new BlockDrawData(drawnMesh, block.Translucent);

        IBuildingGroupBuilder group = BuildingGroup.Create(groupId)
           .WithTitle(new RawText(block.DisplayName))
           .WithDescription(new RawText("A decorative block. It does nothing."))
           .WithIcon(icon)
           .AsNonTransportableBuilding()

           // Area, so a wall is dragged out as a rectangle rather than clicked in one tile at a
           // time. Decoration is the one thing in this game where the player wants to fill a
           // region, and the mode already exists for foundations.
           .WithPreferredPlacement(DefaultPreferredPlacementMode.Area)

           // Everything below copies BuildingDefinitionGroupFactory's decoration path rather
           // than being chosen. A block has no connectors, so auto-connect has nothing to
           // attract and the connector indicators have nothing to draw; and a decoration that
           // cannot be built over without holding the force key is a decoration a player comes
           // to resent.
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

        // No inputs and no outputs of any kind: Build() with nothing added yields a single-tile
        // connector set with an empty connector list, which is exactly what a decoration is.
        IBuildingConnectorData connectors = BuildingConnectors.SingleTile().Build();

        IBuildingBuilder building = Building.Create(definitionId)
           .WithConnectorData(connectors)

           // The renderer type argument is never read - BuildingBuilder.DynamicallyRendering
           // attaches the draw data and ignores TRenderer entirely - but it is the only stage
           // of the chain that attaches an IBuildingCustomDrawData, and
           // BuildingDrawDataFactory throws without one. So the constraint has to be satisfied
           // even though nothing looks the type up here. BlockRenderer is found by reflection,
           // separately, keyed on InertSimulation.
           .DynamicallyRendering<BlockRenderer, InertSimulation, BlockDrawData>(drawData)
           .WithStaticDrawData(CreateDrawData(cubeLod, drawnMesh, drawData))
           .WithoutSound()
           .WithoutSimulationConfiguration()

           // Not WithEfficiencyData. A block processes nothing, so it has no throughput to
           // report - and the setter is broken anyway: BuildingBuilder.WithEfficiencyData
           // discards its argument and attaches `new BuildingEfficiencyData(2f, 1)` regardless
           // of what was passed.
           .WithoutEfficiencyData();

        AtomicBuildingExtender extender = (AtomicBuildingExtender)AtomicBuildings.Extend()
           .AllScenarios()
           .WithBuilding(building, group)

           // Existing, not new. UnlockedWithNewSideUpgrade runs its builder once per building
           // group, and SideUpgradeBuilder.Build appends to the shop every call, so 21
           // blocks would be 21 identical research nodes. See DecorationUnlock.
           .UnlockedWithExistingSideUpgrade(unlock)
           .WithDefaultPlacement()
           .InToolbar(Slot(block, categoryIcon, atlas));

        // The cast is what reaches the stateless WithSimulation overload. It is public on
        // AtomicBuildingExtender but no interface in the chain returns it, so the fluent path
        // can only reach the stateful one - which would drag in a serialised state class and a
        // BuffablesExtender for a cube that has neither state nor speed. Omitting the call
        // altogether is not an option: Build dereferences the simulation branch unconditionally.
        extender
           .WithSimulation(new InertSimulationFactoryBuilder(), log)
           .WithCustomModules(new NoBuildingModules())
           .WithoutPrediction()
           .Build();
    }

    /// The vanilla slots a block still wants, and the one it must not have.
    ///
    /// MainMeshPerLayer is empty on purpose. It is the only slot drawn with the shared
    /// BuildingMaterial, whose colour is an atlas lookup the mod cannot write to, so leaving a
    /// cube there would draw a second, wrongly coloured copy inside the one BlockRenderer
    /// draws. Everything else is the real cube: the blueprint meshes so copy-paste, mass
    /// selection and the placement animation all show it, and the collider so it can be
    /// clicked and removed.
    private static BuildingDrawData CreateDrawData(
        LOD6Mesh cubeLod, IMeshReference preview, BlockDrawData drawData)
    {
        LODEmptyMesh nothing = new LODEmptyMesh();

        return new BuildingDrawData(
            renderVoidBelow: false,
            new ILODMesh[] { nothing, nothing, nothing },
            cubeLod,
            cubeLod,
            preview,
            new LODEmptyMesh(),

            // The default SerializedCollisionBox is already a unit cube sitting on the deck -
            // Center_L (0, 0, 0.5), Dimensions_L (1, 1, 1) - which is exactly this block.
            //
            // BoundingBoxHelper.CreateBasicCollider would be the obvious call and it is wrong
            // here: it copies mesh.bounds.center straight into a LocalVector, and those are
            // different spaces. Unity's is Y-up, LocalVector is Z-up (LocalVector.Up is
            // (0, 0, 1)), so a cube centred at Unity (0, 0.5, 0) becomes a collider offset half
            // a tile sideways and sitting at deck level.
            new[] { new CollisionBox(new SerializedCollisionBox()) },
            drawData,

            // No custom overview mesh, so the game falls back to
            // Theme.BaseResources.OverviewModeFallbackPlaneMesh per occupied tile, the same as
            // every other building. A block shows as a flat plane when zoomed out, which is
            // what the rest of the platform does too.
            hasCustomOverviewMesh: false,
            null,

            // True, so StaticBuildingMeshBuilder.BuildMainMesh skips the main mesh at close
            // LOD. With an empty mesh in that slot it changes nothing observable, but it is the
            // honest value: the simulation renderer really is what draws this building.
            simulationRendererDrawsMainMesh: true);
    }

    /// Which tab the block lands in. Everything goes to Decorations except the block of
    /// redstone, which the catalog marks as belonging with the components that use it.
    private static IToolbarEntryInsertLocation Slot(
        BlockDefinition block, Sprite categoryIcon, BlockAtlas atlas)
    {
        return block.Tab == BlockTab.Redstone
            ? ToolbarSlot.InNewCategory(
                RedstoneRegistrar.CategoryTitleId, RedstoneRegistrar.CategoryIcon(atlas))
            : ToolbarSlot.InNewCategory(CategoryTitleId, categoryIcon);
    }

    /// Cut straight out of the atlas rather than drawn. See BlockAtlas.PixelRectFor.
    private static Sprite CutIcon(BlockAtlas atlas, BlockDefinition block)
    {
        return Sprite.Create(
            atlas.Texture,
            atlas.PixelRectFor(block.IconTexture),
            new Vector2(0.5f, 0.5f));
    }
}
