using System;
using System.Collections.Generic;
using Game.Core.Simulation;
using ShapezShifter.Hijack;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// The throughput readouts in a selected crossing's side panel.
    ///
    /// A crossing carries two independent paths, so it reports two figures where a straight
    /// segment reports one: shapes per minute for each belt run, litres per minute for each pipe
    /// run. The stats themselves are vanilla's - `StructureStatProcessingTime` and
    /// `StructureStatFluidThroughput` - built exactly as `SpaceBeltSidePanelModuleDataProvider`
    /// and `SpacePipeSidePanelModuleDataProvider` build them, so the numbers agree with the belt
    /// on either side and move with speed research the same way.
    internal sealed class CrossoverPanelModules : IIslandModuleDataProvider
    {
        private readonly CrossoverKind Kind;

        public CrossoverPanelModules(CrossoverKind kind)
        {
            Kind = kind;
        }

        public IEnumerable<IHUDSidePanelModuleData> GetStats()
        {
            if (!CrossoverScenario.TryGet(out CrossoverScenario scenario))
            {
                // The panel can only be opened inside a session, by which point the simulation
                // factory has run - but never throw from a HUD callback over a missing number.
                yield break;
            }

            // Path A is the West-to-East run and is the belt on a mixed crossing; path B is the
            // perpendicular one. Same assignment CrossoverConnectors makes.
            foreach (bool isPipe in new[]
                     {
                         Kind == CrossoverKind.PipePipe,
                         Kind != CrossoverKind.BeltBelt
                     })
            {
                yield return isPipe ? scenario.PipeStat() : scenario.BeltStat();
            }
        }

        public IEnumerable<IHUDSidePanelModuleData> GetModules(IslandModel island)
        {
            return Array.Empty<IHUDSidePanelModuleData>();
        }
    }

    /// The scenario-dependent numbers the panel needs, captured when the simulation is built.
    ///
    /// `IIslandModulesRewirer.AddModules` is handed nothing but the lookup, and the providers are
    /// constructed when the mod is - neither point has a `GameMode` to read belt speeds, pipe
    /// speeds or the fluid package size from. `CrossoverSimulationFactory` does, and it runs once
    /// per scenario load before any side panel can be opened, so it publishes them here.
    internal sealed class CrossoverScenario
    {
        /// Vanilla's constant for a space belt: `new SpaceBeltSidePanelModuleDataProvider(
        /// MaxBuildingLayer, researchId, 0.125f)`.
        private const float BeltProcessingDuration = 0.125f;

        private static CrossoverScenario Current;

        private readonly ResearchSpeedId BeltSpeedId;
        private readonly BeltSpeed PipeSpeed;
        private readonly FluidUnit FluidPackageSize;
        private readonly short MaxBuildingLayer;

        private CrossoverScenario(
            ResearchSpeedId beltSpeedId, BeltSpeed pipeSpeed, FluidUnit fluidPackageSize,
            short maxBuildingLayer)
        {
            BeltSpeedId = beltSpeedId;
            PipeSpeed = pipeSpeed;
            FluidPackageSize = fluidPackageSize;
            MaxBuildingLayer = maxBuildingLayer;
        }

        /// Replaced rather than added to, so a second scenario load does not keep the first one's
        /// speeds.
        public static void Publish(
            ResearchSpeedId beltSpeedId, BeltSpeed pipeSpeed, FluidUnit fluidPackageSize,
            short maxBuildingLayer)
        {
            Current = new CrossoverScenario(
                beltSpeedId, pipeSpeed, fluidPackageSize, maxBuildingLayer);
        }

        /// Dropped when the mod is disposed, so a disposed mod's speeds cannot outlive it.
        public static void Clear()
        {
            Current = null;
        }

        public static bool TryGet(out CrossoverScenario scenario)
        {
            scenario = Current;
            return scenario != null;
        }

        /// Twelve: four lanes on each of three layers, the full width of one space path.
        private int Lanes => (MaxBuildingLayer + 1) * SpacePathConstants.NumLanes;

        public IHUDSidePanelModuleData BeltStat()
        {
            return new HUDSidePanelModuleStats.Data(
                new StructureStatProcessingTime(
                    BeltProcessingDuration, BeltSpeedId, Lanes));
        }

        public IHUDSidePanelModuleData PipeStat()
        {
            FluidRate throughput = FluidRate.FromLitersPerMinute((int)Math.Round(
                (PipeSpeed.StepsPerTick * Ticks.FromSeconds(60)).FloatWorldUnits * 2f *
                FluidPackageSize.LitersApprox));
            return new HUDSidePanelModuleStats.Data(
                new StructureStatFluidThroughput(throughput, Lanes));
        }
    }

    /// Attaches the panel provider to the three *mirrored* crossing definitions.
    ///
    /// Only the mirrors. The straight variants get theirs through the extender chain's
    /// `WithCustomModules`, and the chain always registers something for the definition it carries
    /// - `WithoutModules` is not "no registration", it registers a `NoModulesProvider`. Since
    /// `IslandsModulesLookup.AddModuleProvider` is a plain `Dictionary.Add`, claiming a straight
    /// id here too throws `An item with the same key has already been added` and takes the main
    /// menu down with it. The mirror is the only one the chain never sees.
    internal sealed class CrossoverModulesRewirer : IIslandModulesRewirer
    {
        public void AddModules(IslandsModulesLookup modulesLookup)
        {
            foreach (CrossoverKind kind in Enum.GetValues(typeof(CrossoverKind)))
            {
                modulesLookup.AddModuleProvider(
                    CrossoverIds.Mirrored(kind), new CrossoverPanelModules(kind));
            }
        }
    }
}
