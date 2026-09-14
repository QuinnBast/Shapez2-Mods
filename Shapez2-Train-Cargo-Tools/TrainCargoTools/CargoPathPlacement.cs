using System;
using System.Collections.Generic;
using Core.Localization;
using Game.Content.Features.SpacePaths.IslandIO;
using Game.Core.Content.Islands;
using Game.Core.Coordinates;
using Game.Interaction.EntitiesPlacement;
using Game.Placement.Data;
using ShapezShifter;
using ShapezShifter.Flow.Toolbar;
using ShapezShifter.Hijack;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Makes cargo belts drag out like space belts, corners and all.
    ///
    /// A space belt is not one island, it is a *family* - forward, turns, and in vanilla also
    /// splitters, mergers and lifts - plus a placer that picks the right member for each node
    /// of a dragged run. Placing cargo belts one chunk at a time was not a missing feature so
    /// much as a missing placer.
    ///
    /// Nothing here reimplements dragging. Every piece of the placer is a game class with a
    /// public constructor, so BuildInitiator assembles vanilla's own sequence rather than any
    /// placement logic - MatchingDefinitionFinder takes any list of definitions, so the cargo
    /// family becomes a path family just by handing it one.
    ///
    /// It used to go one step further and call
    /// `PlatformIslandsPlacersCreators.CreateSpacePathPlacementInitiator` through a throwaway
    /// instance of the creator, which was better while nothing needed changing. It stopped being
    /// enough when the placer's world IO query had to be substituted - see EitherTagIOQuery - and
    /// that method builds the query internally.
    ///
    /// Also an IToolbarDataRewirer, because the toolbar entry cannot be built until the placer
    /// is registered: a placement entry holds a PlacementInitiatorId, and that id only exists
    /// once RegisterInitiator has returned it. One object doing both phases is the simplest way
    /// to carry the id from one to the other.
    ///
    /// Still generic over the connector pair, though there is only one cargo path now. The pair
    /// decides which of a cargo belt's own two connectors the placer reasons about, and both
    /// answer the same, so the type parameters are a record of what it is picking rather than a
    /// choice that has to be made twice.
    ///
    /// What the pair must *not* decide is which neighbours a run will snap to - see
    /// EitherTagIOQuery, and BuildInitiator for where it goes in.
    internal sealed class CargoPathPlacement<TInput, TOutput>
        : IPlatformIslandPlacementRewirers, IToolbarDataRewirer
        where TInput : class, IEntityConnector, new()
        where TOutput : class, IEntityConnector, new()
    {
        private readonly string PlacerSerialName;
        private readonly string ForwardId;
        private readonly string LeftTurnId;
        private readonly string RightTurnId;

        /// The junction pieces, in no particular order. Handed to the same definition finder as
        /// the corners: vanilla's placer picks whichever family member matches the connections a
        /// node ends up with, so a branching drag chooses a splitter for exactly the reason a
        /// turning drag chooses a corner. Nothing here has to know what a branch is.
        private readonly string[] SplitterIds;
        private readonly string TitleId;
        private readonly string DescriptionId;

        private readonly ILogger Logger;
        private readonly Func<Sprite> Icon;
        private readonly Func<IToolbarEntryInsertLocation> Slot;

        /// Set during placer registration, read during toolbar rewiring.
        private PlacementInitiatorId? Placer;

        public CargoPathPlacement(
            ILogger logger, string placerSerialName, string forwardId, string leftTurnId,
            string rightTurnId, string[] splitterIds, string titleId, string descriptionId,
            Func<Sprite> icon, Func<IToolbarEntryInsertLocation> slot)
        {
            Logger = logger;
            PlacerSerialName = placerSerialName;
            ForwardId = forwardId;
            LeftTurnId = leftTurnId;
            RightTurnId = rightTurnId;
            SplitterIds = splitterIds ?? Array.Empty<string>();
            TitleId = titleId;
            DescriptionId = descriptionId;
            Icon = icon;
            Slot = slot;
        }

        public void ModifyIslandPlacers(
            IslandInitiatorsParams initiators, IPlacementInitiatorIdRegistry registry)
        {
            try
            {
                if (!TryFamily(initiators.Islands, out IIslandDefinition forward,
                        out IReadOnlyList<IIslandDefinition> family))
                {
                    // A scenario this mod did not extend. Nothing to place.
                    return;
                }

                // The family's own connector pair. A cargo belt carries both tags, so either
                // would do here; the belt one is picked because a cargo line starts from a shape
                // line more often than not.
                IMatchingDefinitionFinder<IslandDescriptor, GlobalChunkPivot> finder =
                    new MatchingDefinitionFinder<IslandDescriptor, ChunkVector, ChunkDirection,
                        GlobalChunkCoordinate, LocalChunkPivot, GlobalChunkPivot, GlobalChunkTransform,
                        TInput, TOutput>(family, new IslandPlacementAdapter());

                IPlacementInitiator initiator = BuildInitiator(initiators, finder, forward, family);

                Placer = registry.RegisterInitiator(new SerializedPlacerId(PlacerSerialName), initiator);
                Logger.Info?.Log($"{ForwardId} can be dragged.");
            }
            catch (Exception exception)
            {
                // A broken placer must not take the build menu with it. Without the catch, every
                // island placement throws, not just this one.
                Logger.Exception?.LogException(exception);
            }
        }

        /// Vanilla's `PlatformIslandsPlacersCreators.CreateSpacePathPlacementInitiator`, rebuilt.
        ///
        /// This used to *call* it, through a throwaway instance of the creator - which worked, and
        /// was the right call while nothing needed changing. One thing does now: the placer's
        /// world IO query is constructed inside `IslandPlacersCreator.CreatePathPlacer` from the
        /// same `TInput`/`TOutput` as everything else, and a belt-typed query cannot see a fluid
        /// packager's pipe output. See EitherTagIOQuery for why that shows up as a cargo belt
        /// refusing to snap to something it connects to perfectly well.
        ///
        /// Every part below is the game's own class with a public constructor, so this is vanilla's
        /// sequence with one object substituted, not a reimplementation of any placement logic.
        /// Two differences from vanilla, both deliberate:
        ///
        ///   - **No pipette registration.** Vanilla adds every family member to the pipette map,
        ///     and `DefaultIslandPlacementExtender` - which the builder chain gives no way to skip
        ///     - has already added each of them. `Dictionary.Add` throws on a duplicate key, which
        ///     took startup down with "An item with the same key has already been added. Key:
        ///     CargoBelt". Building the initiator here means simply not making that call, which is
        ///     cleaner than the scratch dictionary it needed before. Pipetting a cargo belt picks
        ///     the single-chunk placer instead of the drag placer.
        ///   - **No port buildings.** Vanilla threads `portSender`/`portReceiver` down to
        ///     `CreatePathPlacer`, and the island overload ignores both - only the *building*
        ///     overload uses them, for `PathAtNotchesUpgradeToPortsProcessor`. Passing them was
        ///     cargo cult.
        private IPlacementInitiator BuildInitiator(
            IslandInitiatorsParams initiators,
            IMatchingDefinitionFinder<IslandDescriptor, GlobalChunkPivot> finder,
            IIslandDefinition forward, IReadOnlyList<IIslandDefinition> family)
        {
            IslandAccessorAdapter islands = new();

            ModularEntityPlacer<OverlappingPlacementData> placer = new(
                new PathPlacer<IslandPlacement, IslandDescriptor, GlobalChunkPivot,
                    GlobalChunkTransform, GlobalChunkCoordinate, ChunkVector, ChunkDirection,
                    LocalChunkPivot, TInput, TOutput, ChunkAxis, IslandInstanceModel,
                    IslandConnector, IslandConnection>(
                    new ChunkSpace(), new IslandPlacementAdapter(), finder, islands,
                    new IslandConnectionFactory(), new EitherTagIOQuery(islands),
                    initiators.ViewportLayersController, EntityType.Island, ChunkVector.Up),
                new PlacerDataBasedOnRepresentingIsland(forward, initiators.IslandsModulesLookup));

            placer.AddProcessorAtEnd(new ChunkCostPlacementProcessor(initiators.ChunkLimitManager));

            // Lifting is what lets a dragged run change layer. It needs its own definition finder
            // because it re-picks a definition after deciding to lift.
            placer.InsertProcessor(
                new PathLiftingProcessor<IslandPlacement, IslandDescriptor, GlobalChunkCoordinate,
                    ChunkVector, ChunkDirection, ChunkAxis, GlobalChunkPivot, LocalChunkPivot,
                    GlobalChunkTransform, TInput, TOutput, IslandInstanceModel, IslandConnector,
                    IslandConnection>(
                    new MatchingDefinitionFinder<IslandDescriptor, ChunkVector, ChunkDirection,
                        GlobalChunkCoordinate, LocalChunkPivot, GlobalChunkPivot,
                        GlobalChunkTransform, TInput, TOutput>(family, new IslandPlacementAdapter()),
                    new IslandPlacementAdapter(), new IslandAccessorAdapter(), new ChunkSpace(),
                    new LiftVerticalOffsetProvider(initiators.ViewportLayersController)),
                placer.ProcessorIndex<IPathUpgradeProcessor>() + 1);

            IPlacementInitiator initiator = new TutorialGamePlacementInitiator(
                // Unlocked with the group, as every island placer is.
                new AnyIdUnlockedWithResearchRewards<IslandDefinitionGroupId>(
                    initiators.ProgressManager,
                    forward.CustomData.Get<IIslandDefinitionGroup>().Id,
                    new IslandResearchLockStatusSolver(), new IslandRewardIdSolver()),
                placer, initiators.EntityPlacementRunner, initiators.TutorialState,

                // Placements of interest drive tutorial prompts. UnknownIsland is the enum's own
                // value for "not one of the builtin ones", so a cargo belt cannot accidentally
                // satisfy a step about space belts.
                BuiltinPlacementOfInterest.UnknownIsland);

            placer.AddProcessorAtEnd(new TargetedIslandsPlacementRulesProcessor(family));
            return initiator;
        }

        /// The forward piece plus its turns.
        ///
        /// Looked up rather than held from construction, because definitions are rebuilt for
        /// every scenario load while this rewirer outlives them. No lifts: a cargo line
        /// therefore cannot cross another cargo line the way a space belt can, which is a known
        /// gap rather than an oversight - lifts would need their own multi-chunk definitions.
        private bool TryFamily(
            GameIslands islands, out IIslandDefinition forward,
            out IReadOnlyList<IIslandDefinition> family)
        {
            forward = null;
            family = null;

            if (!islands.TryGetDefinition(new IslandDefinitionId(ForwardId), out forward) ||
                !islands.TryGetDefinition(new IslandDefinitionId(LeftTurnId),
                    out IIslandDefinition left) ||
                !islands.TryGetDefinition(new IslandDefinitionId(RightTurnId),
                    out IIslandDefinition right))
            {
                return false;
            }

            // Forward first: CreateSpacePathPlacementInitiator uses the first entry as the
            // representing node when it builds the placer.
            List<IIslandDefinition> members = new() { forward, left, right };

            // Missing junctions are not fatal - a run still lays and turns, it just will not
            // branch - so they are skipped with a warning rather than failing the whole family.
            foreach (string id in SplitterIds)
            {
                if (islands.TryGetDefinition(new IslandDefinitionId(id), out IIslandDefinition splitter))
                {
                    members.Add(splitter);
                }
                else
                {
                    Logger.Warning?.Log($"Junction definition '{id}' is missing; cargo belts will not branch there.");
                }
            }

            family = members;
            return true;
        }

        public ToolbarData ModifyToolbarData(ToolbarData toolbarData)
        {
            try
            {
                if (!Placer.HasValue)
                {
                    // Either the family was missing or placer registration threw - both already
                    // logged. Say so anyway, because the visible symptom is a missing button.
                    Logger.Warning?.Log(
                        $"The {ForwardId} drag placer was never registered, so it has no toolbar " +
                        "entry and those belts cannot be placed at all.");
                    return toolbarData;
                }

                PlacementToolbarElementData entry = new(
                    TitleId.T(),
                    DescriptionId.T(),
                    Placer.Value,
                    Icon());

                Slot().AddEntry(toolbarData, entry);
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
            }

            return toolbarData;
        }
    }
}
