using System;
using System.Linq;
using Game.Interaction.EntitiesPlacement;
using Game.Placement.Data;
using Game.Placement.Processing;
using ShapezShifter;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Puts <see cref="CrossoverPathProcessor"/> into the space belt and space pipe placers.
    ///
    /// Both placers are built by PlatformIslandsPlacersCreators and never exposed, so the only
    /// way in is the postfix hook Shifter puts on RegisterPlacers: by then the initiators are in
    /// the registry under their serial names and can be resolved back out. The processor list of
    /// a ModularEntityPlacer stays mutable afterwards, which is what makes this work at all.
    ///
    /// The processor goes in at the index vanilla uses for the wire crossing - ahead of the path
    /// upgrade processor, and so ahead of the lifting processor that sits just after it. Order is
    /// the whole trick: a node that has been turned into a crossing is no longer invalid, so
    /// PathLiftingProcessor never sees it and never builds the detour.
    internal sealed class CrossoverPlacementRewirer : IPlatformIslandPlacementRewirers
    {
        /// The serial names PlatformIslandsPlacersCreators registers its two path placers under.
        /// They come from Enum.GetName over a private enum, so there is nothing to reference.
        private const string SpaceBeltPlacer = "SpaceBeltPlacementInitiator";
        private const string SpacePipePlacer = "SpacePipePlacementInitiator";

        private readonly ILogger Logger;

        public CrossoverPlacementRewirer(ILogger logger)
        {
            Logger = logger;
        }

        public void ModifyIslandPlacers(
            IslandInitiatorsParams islandInitiatorsParams, IPlacementInitiatorIdRegistry registry)
        {
            try
            {
                if (!TryBuildProcessor(islandInitiatorsParams.Islands, out CrossoverPathProcessor processor))
                {
                    return;
                }

                Insert(registry, SpaceBeltPlacer, processor);
                Insert(registry, SpacePipePlacer, processor);
            }
            catch (Exception exception)
            {
                // A broken crossing preference must not take the build menu with it - without
                // this the placers are half-modified and every path placement throws.
                Logger.Exception?.LogException(exception);
            }
        }

        /// Resolves the vanilla forwards and this mod's six crossings out of the island registry.
        ///
        /// All of it is looked up rather than held from construction, because the definitions are
        /// rebuilt for every scenario load while this rewirer outlives them.
        private bool TryBuildProcessor(GameIslands islands, out CrossoverPathProcessor processor)
        {
            processor = null;

            IIslandDefinition beltForward = islands.SpaceBelts.FirstOrDefault();
            IIslandDefinition pipeForward = islands.SpacePipes.FirstOrDefault();
            if (beltForward == null || pipeForward == null)
            {
                return false;
            }

            IIslandDefinition[,] crossings = new IIslandDefinition[3, 2];
            foreach (CrossoverKind kind in Enum.GetValues(typeof(CrossoverKind)))
            {
                if (!islands.TryGetDefinition(CrossoverIds.Original(kind), out IIslandDefinition original) ||
                    !islands.TryGetDefinition(CrossoverIds.Mirrored(kind), out IIslandDefinition mirrored))
                {
                    // A scenario this mod did not extend. Leave the placers alone.
                    return false;
                }

                crossings[(int)kind, 0] = original;
                crossings[(int)kind, 1] = mirrored;
            }

            processor = new CrossoverPathProcessor(beltForward.Id, pipeForward.Id, crossings);
            return true;
        }

        private void Insert(
            IPlacementInitiatorIdRegistry registry, string serialName,
            CrossoverPathProcessor processor)
        {
            if (!registry.TryResolve(new SerializedPlacerId(serialName), out _,
                    out IPlacementInitiator initiator))
            {
                Logger.Warning?.Log($"No placer registered as {serialName}; crossings stay manual");
                return;
            }

            if (initiator is not GamePlacementInitiator game ||
                game.Placer is not ModularEntityPlacer<OverlappingPlacementData> placer)
            {
                Logger.Warning?.Log($"{serialName} is not a modular placer; crossings stay manual");
                return;
            }

            int upgradeIndex = placer.ProcessorIndex<IPathUpgradeProcessor>();
            if (upgradeIndex < 0)
            {
                Logger.Warning?.Log($"{serialName} has no path upgrade step; crossings stay manual");
                return;
            }

            placer.InsertProcessor(processor, upgradeIndex);
        }
    }
}
