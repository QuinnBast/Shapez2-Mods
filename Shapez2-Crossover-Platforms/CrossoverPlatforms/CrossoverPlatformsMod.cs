using System;
using System.Collections.Generic;
using Core.Collections;
using Core.Localization;
using Game.Content.Features.SpacePaths.IslandIO;
using Game.Core.Content.Islands;
using Game.Core.Coordinates;
using JetBrains.Annotations;
using ShapezShifter.Flow;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ShapezShifter.Flow.Toolbar;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ShapezShifter.Textures;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Adds three space platform pieces where two paths cross straight through each other.
    ///
    /// Vanilla has no crossing at all: SpacePathIslandDefinitionFactory generates forward,
    /// left/right turns, four splitters, four mergers and the lifts, and every one of them has a
    /// single path through it. Routing one line past another therefore costs detour platforms.
    [UsedImplicitly]
    public class CrossoverPlatformsMod : IMod
    {
        private readonly ILogger Logger;

        /// Registration for the placer rewirer, so <see cref="Dispose"/> can take it back out.
        private readonly RewirerHandle PlacementHandle;

        /// The same, for the side panel throughput readouts.
        private readonly RewirerHandle ModulesHandle;

        /// Everything registered outside the atomic extender's own chain - every variant's
        /// prediction, and the mirrors' simulation - which re-arms on every scenario load and so
        /// has to be stopped explicitly rather than just unregistered once.
        private readonly List<IDisposable> ReArmedRegistrations = new();

        /// Resolved once, and not through the assembly location alone - see <see cref="ModResources"/>.
        private readonly ModFolderLocator Resources;

        /// The three crossings in one folder of their own, under Regular Platform.
        ///
        /// Was `Root().ChildAt(5).ChildAt(4).ChildAt(^1).InsertAfter()`, taken from the
        /// SandboxIslands sample because it was known to resolve rather than because it was
        /// right - so the three variants landed in an arbitrary group, separately. Naming the
        /// parent means a game update that reorders the toolbar moves them with it instead of
        /// scattering them somewhere new.
        /// Takes the locator rather than resolving one, because after a hot reload the assembly
        /// has no location to resolve from. See <see cref="ModResources"/>.
        private static IToolbarEntryInsertLocation ToolbarSlot(ModFolderLocator resources)
        {
            return QuinnBast.Shapez2.ToolbarKit.ToolbarSlot.InNewGroup(
                QuinnBast.Shapez2.ToolbarKit.ToolbarCategory.RegularPlatform,
                "crossover.toolbar.title",
                FileTextureLoader.LoadTextureAsSprite(resources.SubPath("Crossover_BeltBelt.png"), out _),
                "crossover.toolbar.description");
        }

        public CrossoverPlatformsMod(ILogger logger)
        {
            QuinnBast.Shapez2.ToolbarKit.ToolbarKit.Log = logger;

            Logger = logger;
            Resources = ModResources.Locate(logger);

            AddCrossover(CrossoverKind.BeltBelt, "belt-belt", "Crossover_BeltBelt.png");
            AddCrossover(CrossoverKind.BeltPipe, "belt-pipe", "Crossover_BeltPipe.png");
            AddCrossover(CrossoverKind.PipePipe, "pipe-pipe", "Crossover_PipePipe.png");

            // Makes a dragged path cross an existing one instead of lifting over it.
            PlacementHandle = GameRewirers.AddRewirer(new CrossoverPlacementRewirer(logger));

            // Makes a crossing draw as two lengths of track rather than as a black platform.
            CrossoverAppearance.Install(logger);

            // Throughput readouts for the mirrored variants only - the straight ones get theirs
            // from the extender chain, and registering an id twice is a hard error.
            ModulesHandle = GameRewirers.AddRewirer(new CrossoverModulesRewirer());
        }

        /// Everything this mod added to the game, taken back out.
        ///
        /// A loader that disposes mods - a hot-reloader especially - leaves the game running, so
        /// anything left registered keeps firing against a dead instance. The re-arming
        /// registrations matter most: left alone they would re-register themselves on the next
        /// scenario load forever, which is a leak that survives the mod that made it.
        public void Dispose()
        {
            foreach (IDisposable registration in ReArmedRegistrations)
            {
                registration.Dispose();
            }

            ReArmedRegistrations.Clear();

            GameRewirers.RemoveRewirer(PlacementHandle);
            GameRewirers.RemoveRewirer(ModulesHandle);
            CrossoverAppearance.Uninstall();

            // Statics outlive the instance that filled them. A reloaded assembly gets fresh ones,
            // but a mod that is merely disposed does not, so it would keep logging through this
            // mod's dead channel.
            CrossoverScenario.Clear();
            QuinnBast.Shapez2.ToolbarKit.ToolbarKit.Log = null;
        }

        private void AddCrossover(CrossoverKind kind, string slug, string iconFile)
        {
            IslandDefinitionGroupId groupId = CrossoverIds.Group(kind);
            IslandDefinitionId definitionId = CrossoverIds.Original(kind);
            IslandDefinitionId mirroredId = CrossoverIds.Mirrored(kind);

            string titleId = $"crossover.group.{slug}.title";
            string descriptionId = $"crossover.group.{slug}.description";

            IIslandGroupBuilder groupBuilder = IslandGroup.Create(groupId)
               .WithTitle(titleId.T())
               .WithDescription(descriptionId.T())
               .WithIcon(FileTextureLoader.LoadTextureAsSprite(Resources.SubPath(iconFile), out _))
               .AsNonTransportableIsland()
               .WithPreferredPlacement(DefaultPreferredPlacementMode.Area);

            ChunkLayoutLookup<ChunkVector, IslandChunkData> layout = CrossoverLayout();

            MirroredIslandPair pair = new(
                CrossoverIsland(definitionId, kind, layout, mirrored: false),
                CrossoverIsland(mirroredId, kind, layout, mirrored: true));

            // The fluent interfaces fork here. WithSimulation off the unlockable extender lands
            // straight on IAtomicIslandExtender, which has no WithDefaultPlacement; placement is
            // reached only through IDefinedSimulatableIslandExtender, and nothing in the chain
            // returns that interface, so the cast is the only way in. It is safe because every
            // one of these interfaces is implemented by the same AtomicIslandExtender instance,
            // and the two WithDefaultPlacement overloads are the same no-op.
            IAtomicIslandExtender simulated = AtomicIslands.Extend()
               .AllScenarios()
               .WithIsland(pair, groupBuilder)
               // The first milestone rather than the last, so a crossing can be tested without
               // an endgame save.
               .UnlockedAtMilestone(new ByIndexMilestoneSelector(0))
               .WithSimulation(new CrossoverSimulationFactory(kind));

            // The same cast again on the way out, and for the same reason. What InToolbar returns
            // offers exactly two exits - WithPrediction, which this chain deliberately does not
            // take, and WithoutPrediction, which throws NotImplementedException
            // (AtomicIslandExtender:307). So there is no fluent route from here to
            // WithCustomModules that skips prediction; the cast is it.
            IDefinedAccessibleSimulatablePlaceableIslandExtender placed =
                ((IDefinedSimulatableIslandExtender)simulated)
               .WithDefaultPlacement()
               .InToolbar(ToolbarSlot(Resources));

            ((IAtomicIslandExtender)placed)
               // The straight variant's side panel. The mirror gets its own through
               // CrossoverModulesRewirer, because it never travels this chain.
               .WithCustomModules(new CrossoverPanelModules(kind))
               .Build();

            // Prediction stays off that chain on purpose, even for the straight variant.
            //
            // AtomicIslandExtender.Build re-arms itself only once every branch it was handed has
            // fired - WaitAllRewirers clears one link per branch and re-runs BuildExtenders when
            // the set empties. The prediction branch fires from PredictionSystemsInterceptor, a
            // postfix on BuiltinPredictionSimulationSystems.CreateSimulationSystems - and that
            // method has exactly one caller, GameSessionOrchestrator.SetupPredictions, which
            // StartPredictionUpdate skips entirely when SimulationSettings.Predict is false.
            //
            // So a player who turns predictions off in the settings never creates a prediction
            // system, the branch never completes, the chain never re-arms, and the definitions,
            // the toolbar entry and the research unlock are spent on the first scenario of the
            // process - which is the main menu's background game, not their save. The mod then
            // loads without a single error and has nothing in it. Reported by a player whose log
            // showed IslandPredictionExtender added six times (three chain, three mirror) and
            // removed none across four sessions, with the crossings present only in the menu one.
            //
            // Registering it here instead leaves the chain waiting only on branches that do
            // fire. Prediction then attaches whenever CreateSimulationSystems runs, and simply
            // stays armed and idle for a player who has it switched off - which is correct,
            // since nothing is predicting for them anyway.
            ReArmedRegistrations.Add(
                new ReArmingRewirer(() =>
                    new IslandPredictionExtender<CrossoverPredictionSimulation>(
                        definitionId, new CrossoverPredictionFactory(), Logger)));

            // The mirror never passes through the chain at all, and both systems are keyed by
            // island definition id, so without these it would be placeable but inert.
            ReArmedRegistrations.Add(
                new ReArmingRewirer<SpacePathConfiguration>(() =>
                    new IslandSimulationExtender<CrossoverSimulation, CrossoverSimulationState,
                        SpacePathConfiguration>(mirroredId, new CrossoverSimulationFactory(kind))));
            ReArmedRegistrations.Add(
                new ReArmingRewirer(() =>
                    new IslandPredictionExtender<CrossoverPredictionSimulation>(
                        mirroredId, new CrossoverPredictionFactory(), Logger)));
        }

        /// One crossing definition, either as declared or mirrored.
        private IIslandBuilder CrossoverIsland(
            IslandDefinitionId definitionId, CrossoverKind kind,
            ChunkLayoutLookup<ChunkVector, IslandChunkData> layout, bool mirrored)
        {
            return Island.Create(definitionId)
               .WithLayout(layout)
               // Per-chunk, not bounding: ShapezShifter's WithBoundingCollider sizes the box as
               // (max - min) * 20 over the chunk positions, which is one chunk short on every
               // axis and collapses to nothing at all for a single-chunk island. A zero-size
               // collider is why a crossing could not be hovered, selected or deleted.
               .WithPerChunkColliders()
               .WithConnectorData(CrossoverConnectors(kind, layout, mirrored))
               // Flippable, because the group now holds a mirrored pair. For belt-over-belt and
               // pipe-over-pipe the mirror is also reachable by rotating - the four rotations
               // already cover every combination of the two path directions - and only
               // belt-over-pipe genuinely needs it, to aim the belt and the pipe independently.
               // All three get it so that F behaves the same way on each.
               .WithInteraction(flippable: true, canHoldBuildings: false)
               .WithDefaultChunkCost()
               // A zeroed drawing context, not DrawAll: a crossing is track, and the frame layers
               // are what made it read as a solid black platform. The data itself still has to be
               // attached - every vanilla island has it, IslandChunkPlatformFramesCache skips an
               // island that lacks it, and IslandFramesDrawer then looks up a cache entry that was
               // never made and throws once per frame. So zero it rather than remove it.
               .WithRenderingOptions(
                    new HomogeneousChunkDrawing(default),
                    drawPlayingField: false);
        }

        /// One chunk with no buildable tiles, the same shape as a space path segment.
        private ChunkLayoutLookup<ChunkVector, IslandChunkData> CrossoverLayout()
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

            // Void every tile: a crossing is a piece of track, not a platform to build on.
            for (int i = 0; i < chunkData.TileVoidFlags_L.Length; i++)
            {
                chunkData.TileVoidFlags_L[i] = true;
            }

            yield return new KeyValuePair<ChunkVector, IslandChunkData>(origin, chunkData);
        }

        /// The four connectors, in the order the two paths are wired.
        ///
        /// Order is load-bearing. ConnectableIslandSimulation filters this array by
        /// ISpacePathInputConnector and ISpacePathOutputConnector and pairs the n-th of each with
        /// bundle n, so listing West-in, East-out, North-in, South-out yields inputs
        /// [West, North] and outputs [East, South] - giving path A the West-to-East run and path
        /// B the North-to-South run, which is what CrossoverSimulation expects.
        ///
        /// Vanilla straight segments take their input on West and emit on East, so a crossing
        /// keeps that convention and adds the perpendicular pair.
        ///
        /// The mirrored half of the pair reverses path B and leaves path A alone, because that is
        /// how the engine mirrors an island: it flips over ChunkAxis.YAxis, and
        /// ChunkDirection.Mirror swaps a direction only when the direction's own axis matches -
        /// North and South are the YAxis pair, East and West the XAxis one. The layout needs no
        /// mirroring to match, being a single chunk at the origin with no notches, which mirrors
        /// to itself.
        private IIslandConnectorData CrossoverConnectors(
            CrossoverKind kind, ChunkLayoutLookup<ChunkVector, IslandChunkData> layout,
            bool mirrored)
        {
            bool pipeA = kind == CrossoverKind.PipePipe;
            bool pipeB = kind != CrossoverKind.BeltBelt;

            ChunkDirection intoB = mirrored ? ChunkDirection.South : ChunkDirection.North;
            ChunkDirection outOfB = mirrored ? ChunkDirection.North : ChunkDirection.South;

            return new IslandConnectorData(
                new[]
                {
                    Input(ChunkDirection.West, pipeA),
                    Output(ChunkDirection.East, pipeA),
                    Input(intoB, pipeB),
                    Output(outOfB, pipeB)
                },
                layout.ChunkPositions);

            EntityIO<LocalChunkPivot, IIslandConnector> Input(ChunkDirection dir, bool pipe)
            {
                LocalChunkPivot pivot = new(ChunkVector.Zero, dir);
                IIslandConnector connector = pipe
                    ? new SpacePipeInputConnector()
                    : (IIslandConnector)new SpaceBeltInputConnector();
                return new EntityIO<LocalChunkPivot, IIslandConnector>(pivot, connector);
            }

            EntityIO<LocalChunkPivot, IIslandConnector> Output(ChunkDirection dir, bool pipe)
            {
                LocalChunkPivot pivot = new(ChunkVector.Zero, dir);
                IIslandConnector connector = pipe
                    ? new SpacePipeOutputConnector()
                    : (IIslandConnector)new SpaceBeltOutputConnector();
                return new EntityIO<LocalChunkPivot, IIslandConnector>(pivot, connector);
            }
        }
    }
}
