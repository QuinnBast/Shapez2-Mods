using System;
using System.Collections.Generic;
using System.IO;
using Core.Collections;
using Core.Localization;
using Game.Content.Features.SpacePaths.IslandIO;
using Game.Core.Content.Islands;
using Game.Core.Coordinates;
using Game.Core.Simulation;
using JetBrains.Annotations;
using ShapezShifter.Flow;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ShapezShifter.Flow.Toolbar;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ShapezShifter.Textures;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace TrainCargoTools
{
    /// Train cargo outside a train station.
    ///
    /// Three pieces. A packager turns loose shapes into cargo packages, a cargo belt carries them,
    /// an unpackager turns them back. The point is buffering: a station holds only a few
    /// containers, so a late train stalls everything feeding it, and a cargo belt is buffer you
    /// can build more of - one belt slot holds a whole package instead of one shape.
    ///
    /// A stock train station would refuse a package - its filling container gates on
    /// `item is ShapeItem` - so PackagedCargoStations detours that gate and every shape station
    /// accepts packages directly. A packed line therefore runs straight into a station with no
    /// unpackager in front of it, which is the whole point: the cargo belt behind it is the
    /// buffer. See DESIGN.md.
    [UsedImplicitly]
    public class TrainCargoToolsMod : IMod
    {
        private readonly PackagedCargoStations Stations;

        /// Meshes for the machines, and the hook that puts them and the belt track on screen.
        private readonly CargoAppearance Appearance;

        /// Static because the resource lookup is, and it wants somewhere to warn.
        private static ILogger Log;

        /// Registration for the drag placer, so Dispose can take it back out again.
        private readonly RewirerHandle PathPlacement;

        /// cargotools.* - recolouring and art dumps. See CargoArtCommands.
        private readonly RewirerHandle AtlasDump;

        /// One folder under Rail.
        ///
        /// It used to be two, shapes and fluids, because there were two belt families and a
        /// player laying one line had no use for the other's six pieces. Now that a single cargo
        /// belt carries both, the split bought little: seven entries in one folder, of which only
        /// the three fluid machines are line-specific.
        ///
        /// Appended to Rail, which puts it after the train dock groups - the closest the
        /// name-based API gets to "next to the wagon loaders" without an index path.
        private IToolbarEntryInsertLocation ToolbarSlot()
        {
            return Shapez2.ToolbarKit.ToolbarSlot.InNewGroup(
                Shapez2.ToolbarKit.ToolbarCategory.Rail,
                "cargo-tools.toolbar.cargo.title",
                LoadIcon("CargoBelt.png"),
                "cargo-tools.toolbar.cargo.description");
        }

        /// The mod's Resources folder. See ModResources for why it is not just the locator.
        private static ModFolderLocator Resources()
        {
            return ModResources.Locate(Log);
        }

        private static UnityEngine.Sprite LoadIcon(string file)
        {
            return FileTextureLoader.LoadTextureAsSprite(Resources().SubPath(file), out _);
        }

        public TrainCargoToolsMod(ILogger logger)
        {
            Log = logger;
            Shapez2.ToolbarKit.ToolbarKit.Log = logger;

            // One belt family for both lines, hidden behind a single drag entry.
            //
            // It used to be two families - belt-tagged for shapes, pipe-tagged for fluids -
            // because a fluid train station's input is a SpacePipeInputConnector and a
            // belt-tagged output will not snap to it. `dualConnectors` puts *both* a belt and a
            // pipe connector at each pivot instead, which IslandConnectorData allows: it keys
            // connectors by pivot in a MultiValueDictionary and only rejects two of the same
            // type at one pivot.
            AddIsland("CargoBelt", "cargo-belt", "CargoBelt.png",
                new CargoBeltSimulationFactory(), hidden: true, pathTrack: true,
                dualConnectors: true);

            // Dragging picks a corner when the run turns, so a player never reaches for one by
            // hand. They still have to exist as definitions for the placer's definition finder
            // to choose from.
            AddIsland("CargoBelt_LeftTurn", "cargo-belt-left", "CargoBeltLeft.png",
                new CargoBeltSimulationFactory(), outputDirection: ChunkDirection.North,
                hidden: true, pathTrack: true, dualConnectors: true);

            AddIsland("CargoBelt_RightTurn", "cargo-belt-right", "CargoBeltRight.png",
                new CargoBeltSimulationFactory(), outputDirection: ChunkDirection.South,
                hidden: true, pathTrack: true, dualConnectors: true);

            // Legacy ids, kept only so saves made before the two belt families merged still
            // load. A placed island is stored by definition id, and IslandLayoutSerializer
            // throws `Island definition was not migrated correctly. Perhaps a migrator is
            // missing?` the moment it meets an id the session does not have - which is what
            // deleting these did to a real save.
            //
            // The game does have a migrator interface for exactly this,
            // IIslandAdditionalDataMigrator, which can rewrite a MigratableIsland's
            // DefinitionId. But its list is built inside the session with no Shifter rewirer
            // over it, so registering one means another detour; three hidden definitions cost
            // less and cannot fail.
            //
            // They are the unified belt in every respect - same factory, same dual connectors -
            // so an old fluid cargo belt keeps working and simply cannot be built any more. They
            // borrow the unified slugs too, so they need no translation keys of their own.
            AddIsland("FluidCargoBelt", "cargo-belt", "CargoBelt.png",
                new CargoBeltSimulationFactory(), hidden: true, pathTrack: true,
                dualConnectors: true);

            AddIsland("FluidCargoBelt_LeftTurn", "cargo-belt-left", "CargoBeltLeft.png",
                new CargoBeltSimulationFactory(), outputDirection: ChunkDirection.North,
                hidden: true, pathTrack: true, dualConnectors: true);

            AddIsland("FluidCargoBelt_RightTurn", "cargo-belt-right", "CargoBeltRight.png",
                new CargoBeltSimulationFactory(), outputDirection: ChunkDirection.South,
                hidden: true, pathTrack: true, dualConnectors: true);

            // The packager is the one machine whose work is invisible from outside, so it gets
            // a fill gauge per layer. See CargoPackagerModules.
            AddIsland("CargoPackager", "cargo-packager", "CargoPackager.png",
                new ShapeCargoPackagerFactory(),
                modules: new CargoPackagerModules(LoadIcon("CargoPackager.png")));

            AddIsland("CargoUnpackager", "cargo-unpackager", "CargoUnpackager.png",
                new ShapeCargoUnpackagerFactory());

            // Both fluid ends sit on the pipe-tagged line, so both sides are pipes.
            AddIsland("FluidCargoPackager", "fluid-cargo-packager", "FluidCargoPackager.png",
                new FluidCargoPackagerFactory(), inputIsPipe: true, outputIsPipe: true,
                fluidLine: true,
                modules: new CargoPackagerModules(LoadIcon("FluidCargoPackager.png")));

            AddIsland("FluidCargoUnpackager", "fluid-cargo-unpackager", "FluidCargoUnpackager.png",
                new FluidCargoUnpackagerFactory(), inputIsPipe: true, outputIsPipe: true, fluidLine: true);

            // One buffer for either kind of cargo. Placed singly rather than dragged - a store
            // is a thing you put somewhere, not a run you lay out.
            AddIsland("CargoStoreAny", "cargo-store", "CargoStore.png",
                new AnyCargoStoreFactory(), dualConnectors: true,
                modules: new CargoStoreModules(LoadIcon("CargoStore.png")));

            // The two per-kind stores it replaced, kept registered and hidden so saves holding
            // one still load - the same reason the old fluid belt ids are still here. They keep
            // their own simulations and states untouched, so an existing store carries on
            // working and simply cannot be built any more.
            AddIsland("CargoStore", "cargo-store", "CargoStore.png",
                new ShapeCargoStoreFactory(), hidden: true,
                modules: new CargoStoreModules(LoadIcon("CargoStore.png")));

            AddIsland("FluidCargoStore", "cargo-store", "FluidCargoStore.png",
                new FluidCargoStoreFactory(), inputIsPipe: true, outputIsPipe: true,
                hidden: true, modules: new CargoStoreModules(LoadIcon("FluidCargoStore.png")));

            // After the definitions, because it addresses them by id, and once only - the
            // meshes are loaded here rather than per session.
            Appearance = new CargoAppearance(
                new CargoMeshes(Resources(), logger, CargoAppearance.MeshIds), logger);
            AtlasDump = GameRewirers.AddRewirer(new CargoArtCommands(Appearance, logger));

            Stations = new PackagedCargoStations(logger);

            // Drag placement. One placer now, over the one belt family.
            //
            // Typed on the belt connectors rather than the pipe ones because a drag has to pick
            // a pair, and the belt side is what a shape line starts from. The definitions carry
            // both, so a run laid this way still joins a fluid station at either end.
            PathPlacement = GameRewirers.AddRewirer(
                new CargoPathPlacement<SpaceBeltInputConnector, SpaceBeltOutputConnector>(
                    logger, "CargoBeltPlacementInitiator",
                    "CargoBelt", "CargoBelt_LeftTurn", "CargoBelt_RightTurn",
                    "cargo-tools.group.cargo-belt.title", "cargo-tools.group.cargo-belt.description",
                    () => LoadIcon("CargoBelt.png"),
                    () => ToolbarSlot()));
        }

        public void Dispose()
        {
            GameRewirers.RemoveRewirer(PathPlacement);
            GameRewirers.RemoveRewirer(AtlasDump);
            Appearance.Dispose();
            Stations.Dispose();
        }

        /// Everything the pieces have in common: one chunk of unbuildable space path, an input
        /// West and an output East, a toolbar entry, and a stateful simulation.
        ///
        /// Only the fluid pieces differ, and only in their connector tags - the tag is what
        /// decides what an island will join to, and a fluid train station only joins to a pipe.
        private void AddIsland<TSimulation, TState, TConfig>(
            string id, string slug, string iconFile,
            IIslandSimulationFactoryBuilder<TSimulation, TState, TConfig> simulation,
            ChunkDirection? outputDirection = null,
            bool inputIsPipe = false, bool outputIsPipe = false, bool hidden = false,
            bool fluidLine = false, bool pathTrack = false, bool dualConnectors = false,
            IIslandModuleDataProvider modules = null)
            where TSimulation : Simulation<TState>
            where TState : class, ISimulationState, new()
        {
            IslandDefinitionGroupId groupId = new($"{id}Group");
            IslandDefinitionId definitionId = new(id);

            string titleId = $"cargo-tools.group.{slug}.title";
            string descriptionId = $"cargo-tools.group.{slug}.description";

            IIslandGroupBuilder groupBuilder = IslandGroup.Create(groupId)
               .WithTitle(titleId.T())
               .WithDescription(descriptionId.T())
               .WithIcon(LoadIcon(iconFile))
               .AsNonTransportableIsland()
               .WithPreferredPlacement(DefaultPreferredPlacementMode.Area);

            ChunkLayoutLookup<ChunkVector, IslandChunkData> layout = SingleChunkLayout();

            IIslandBuilder islandBuilder = Island.Create(definitionId)
               .WithLayout(layout)
               // Per-chunk, never bounding. WithBoundingCollider takes the min and max
               // chunk position and uses (max - min) as the box size, so a one-chunk
               // island gets min == max and a box of size zero - invisible to the
               // raycast, so the platform cannot be clicked. It is wrong for multi-chunk
               // islands too: the z extent is always 0 and x/y come out one chunk short.
               .WithPerChunkColliders()
               .WithConnectorData(Connectors(
                    layout, outputDirection ?? ChunkDirection.East,
                    inputIsPipe, outputIsPipe, dualConnectors))
               .WithInteraction(flippable: false, canHoldBuildings: false)
               .WithDefaultChunkCost()
               // Two different things are being drawn here, so two different contexts.
               //
               // A machine is a platform with something standing on it: it gets the full frame
               // and a playing field, like any island, and CargoAppearance hangs a mesh on top.
               //
               // A belt is not a platform at all. Its whole appearance is the space path track
               // CargoAppearance points it at, which SpacePathPlatformDrawer draws 2.07314 units
               // below the chunk - so leaving the frame on would float a full platform deck over
               // every segment of a dragged run, which no vanilla space belt has.
               .WithRenderingOptions(
                    new HomogeneousChunkDrawing(pathTrack
                        ? ChunkPlatformDrawingContext.DrawNothing()
                        : ChunkPlatformDrawingContext.DrawAll()),
                    drawPlayingField: !pathTrack);

            IAtomicIslandExtender extender = AtomicIslands.Extend()
               .AllScenarios()
               // Dual-connector islands only. Two connectors share each pivot, so joining a
               // shape line connects one and "conflicts" the other, and the placement preview
               // paints a red cross over a belt that works perfectly. See SkipConflictMarkers.
               .WithIsland(
                    dualConnectors ? new SkipConflictMarkers(islandBuilder) : islandBuilder,
                    groupBuilder)
               .UnlockedAtMilestone(new ByIndexMilestoneSelector(0))
               .WithDefaultPlacement()
               .InToolbar(hidden
                    ? Shapez2.ToolbarKit.ToolbarSlot.Hidden()
                    : ToolbarSlot())
               .WithSimulation(simulation);

            // Prediction, which is a wholly separate simulation from the one above and the only
            // thing the shape and fluid readouts are built from. Without it an island registers
            // no receiver bundle, the belt in front of it never manages to link its provider, and
            // `IslandPredictionRenderer` keeps drawing that belt's end-of-line bubble on top of
            // the island's entrance. See CargoPrediction.
            //
            // The cast is the only way in: `WithPrediction` lives on
            // IDefinedAccessibleSimulatablePlaceableIslandExtender, which nothing in this chain
            // returns. It is safe because every one of these interfaces is implemented by the
            // same AtomicIslandExtender instance, and the extender only records the builder - the
            // order the two branches are declared in does not matter to Build().
            ((IDefinedAccessibleSimulatablePlaceableIslandExtender)extender)
               .WithPrediction(new CargoPredictionFactory(dualConnectors ? 2 : 1), Log);

            // Two separate builder methods rather than one taking null, because
            // that is the shape Shifter offers.
            IIslandExtender complete = modules == null
                ? extender.WithoutModules()
                : extender.WithCustomModules(modules);

            complete.Build();
        }

        /// One chunk, no buildable tiles - the shape of a space path segment.
        private ChunkLayoutLookup<ChunkVector, IslandChunkData> SingleChunkLayout()
        {
            return new ChunkLayoutLookup<ChunkVector, IslandChunkData>(ChunkData());
        }

        private IEnumerable<KeyValuePair<ChunkVector, IslandChunkData>> ChunkData()
        {
            ChunkVector origin = new(0, 0, 0);

            IslandChunkData chunkData = IslandLayoutFactory.CreateIslandChunkData(
                chunkTile: origin,
                notchDirections: Array.Empty<ChunkDirection>(),
                neighborChunks: origin.AsEnumerable(),
                isBuildable: true,
                flipped: false,
                out _);

            for (int i = 0; i < chunkData.TileVoidFlags_L.Length; i++)
            {
                chunkData.TileVoidFlags_L[i] = true;
            }

            yield return new KeyValuePair<ChunkVector, IslandChunkData>(origin, chunkData);
        }

        /// West in, East out.
        ///
        /// Every piece carries cargo on a *belt* connector, fluid ends included. Reusing
        /// SpaceBeltInput/OutputConnector for cargo rather than inventing a cargo connector is
        /// not a shortcut, it is the only option: ConnectableIslandSimulation switches on the
        /// connector class to decide the chunk connector's item type - SpaceBeltInputConnector
        /// maps to ShapeItem, SpacePipeInputConnector to FluidPackageItem - and throws
        /// NotImplementedException for anything else.
        ///
        /// The item type on those chunk connectors is only a compatibility tag, though.
        /// ItemInputChunkConnector&lt;TItem&gt;.CanConnect tests
        /// `other is IItemOutputChunkConnector&lt;TItem&gt;`, which decides which paths may join;
        /// what actually flows is whatever the lanes accept. That is what lets a packager emit
        /// packages through a shape-tagged connector, and it has a visible cost: a cargo belt
        /// will connect to an ordinary space belt and then silently refuse its shapes.
        ///
        /// The pipe connectors are the exception and behave properly, because a pipe genuinely
        /// does carry FluidPackageItems: a fluid packager's input will only join a pipe, and a
        /// fluid unpackager's output will only join a pipe.
        private IIslandConnectorData Connectors(
            ChunkLayoutLookup<ChunkVector, IslandChunkData> layout, ChunkDirection outputDirection,
            bool inputIsPipe, bool outputIsPipe, bool dualConnectors)
        {
            List<EntityIO<LocalChunkPivot, IIslandConnector>> connectors = new();

            if (dualConnectors)
            {
                // Both tags at both pivots, so one island joins a belt-tagged neighbour or a
                // pipe-tagged one. Legal because IslandConnectorData keys by pivot in a
                // MultiValueDictionary and only rejects two connectors of the *same* type there.
                // The matching half of the trick is CargoBeltSimulation claiming two bundles;
                // without that, ConnectableIslandSimulation's loop ignores the second connector.
                connectors.Add(Connector(ChunkDirection.West, new SpaceBeltInputConnector()));
                connectors.Add(Connector(ChunkDirection.West, new SpacePipeInputConnector()));
                connectors.Add(Connector(outputDirection, new SpaceBeltOutputConnector()));
                connectors.Add(Connector(outputDirection, new SpacePipeOutputConnector()));
            }
            else
            {
                IIslandConnector input = inputIsPipe
                    ? new SpacePipeInputConnector()
                    : (IIslandConnector)new SpaceBeltInputConnector();

                IIslandConnector output = outputIsPipe
                    ? new SpacePipeOutputConnector()
                    : (IIslandConnector)new SpaceBeltOutputConnector();

                connectors.Add(Connector(ChunkDirection.West, input));
                connectors.Add(Connector(outputDirection, output));
            }

            return new IslandConnectorData(connectors, layout.ChunkPositions);

            EntityIO<LocalChunkPivot, IIslandConnector> Connector(ChunkDirection dir, IIslandConnector connector)
            {
                LocalChunkPivot pivot = new(ChunkVector.Zero, dir);
                return new EntityIO<LocalChunkPivot, IIslandConnector>(pivot, connector);
            }
        }
    }
}
