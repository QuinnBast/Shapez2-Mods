using Game.Content.Features.Fluids;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace TrainCargoTools
{
    /// A straight space path that carries train cargo packages instead of loose shapes.
    ///
    /// Structurally this is the vanilla SpaceConveyorSimulation: one 12-lane bundle handed out
    /// as both the receiver and the provider, so items enter one side and leave the other.
    ///
    /// What makes it a *cargo* belt is the accept hook. A cargo container already travels as an
    /// ordinary belt item - PackageOnTrack&lt;TContainer&gt; implements IBeltItem, and the game
    /// already runs them down a BeltPathLane inside every train station via CargoPackageTrack.
    /// Nothing about a belt lane objects to carrying one. The hook is what stops loose shapes
    /// getting on, which is what keeps a cargo line legible and stops it silently acting as a
    /// very high capacity shape belt.
    public class CargoBeltSimulation : Simulation<CargoBeltSimulationState>, IItemBundleSimulation,
        ISimulation, IUpdatableSimulation
    {
        /// Typed on the mod's own lane, which is a FastBeltPathLane with no changes. The type
        /// is what lets a *sender* tell a cargo belt from an ordinary one - see CargoHandover.
        public readonly ItemLaneBundle<CargoBeltLane> PathBundle;

        /// One per layer, on the lane that carries cargo. What lets a train unloader - or an
        /// ordinary belt - feed a cargo belt at all. See CargoIntake.
        private readonly CargoIntake[] Intakes;

        /// The output side, wrapped so a package is never handed somewhere that would destroy
        /// it. See CargoHandover.
        private readonly CargoHandover.GuardedProviderBundle Outgoing;

        /// Two of each, both the same bundle. This is what makes one cargo belt serve both
        /// lines instead of there being a belt-tagged family and a pipe-tagged twin.
        ///
        /// `ConnectableIslandSimulation` builds one chunk connector per island connector, but its
        /// loop is bounded by `min(NumItemReceiverBundles, connectorCount)` - so a second
        /// connector at the same pivot is silently ignored unless the simulation claims a second
        /// bundle. Claiming two and handing back the same `PathBundle` for both gives the island a
        /// `ShapeItem` chunk connector *and* a `FluidPackageItem` one at each pivot, feeding the
        /// same lanes. `ItemInputChunkConnector<TItem>.CanConnect` then matches whichever the
        /// neighbour has, so the same belt snaps to a shape station and to a fluid one.
        public int NumItemReceiverBundles => 2;

        public int NumItemProviderBundles => 2;

        public CargoBeltSimulation(
            IBeltSpeed speed,
            ICargoContainerCapacityConfigProvider shapeCapacity,
            ICargoContainerCapacityConfigProvider fluidCapacity,
            IFluidRegistry fluids,
            CargoBeltSimulationState state)
            : base(state)
        {
            Intakes = new CargoIntake[SpacePathConstants.NumLayers];
            for (int layer = 0; layer < SpacePathConstants.NumLayers; layer++)
            {
                Intakes[layer] = new CargoIntake(
                    shapeCapacity, fluidCapacity, fluids,
                    state.ShapeIntake[layer], state.FluidIntake[layer]);
            }

            // Counted rather than passed in: the bundle factory is handed a lane state and
            // nothing else, so the lane index has to come from the call order. See
            // CargoLanes.LaneOfFactoryCall.
            int call = 0;

            PathBundle = ItemLaneBundle.Create(state.PathBundleState,
                (FastBeltPathLaneState laneState) =>
                {
                    int index = call++;
                    short laneIndex = CargoLanes.LaneOfFactoryCall(index);
                    short layer = (short)(index % SpacePathConstants.NumLayers);

                    // One lane per layer carries cargo and the rest refuse everything, so that
                    // what a loaded belt looks like is what it is holding. See CargoLanes.
                    if (!CargoLanes.Carries(laneIndex))
                    {
                        CargoBeltLane idle = new(speed, laneState);
                        idle.PreAcceptHook = RefuseEverything;
                        return idle;
                    }

                    // The carrying lane also takes loose items, which the intake packs into
                    // whole packages - see CargoIntake for why that is where an unloader gets
                    // to work at all.
                    CargoIntake intake = Intakes[layer];
                    CargoBeltLane lane = new(speed, laneState);
                    lane.PreAcceptHook = intake.CanEnter;
                    lane.AcceptHook = intake.OnAccept;
                    return lane;
                });

            Outgoing = new CargoHandover.GuardedProviderBundle(PathBundle);
        }

        /// Only train cargo rides a cargo belt.
        ///
        /// Both concrete package types are admitted - the game instantiates its cargo machinery
        /// as CargoPackage&lt;ShapeId&gt; and CargoPackage&lt;FluidId&gt; (see
        /// CargoExchangingOrchestrator) - so one belt type serves both rather than needing a
        /// shape variant and a fluid variant.
        private static bool IsCargo(IBeltItem item)
        {
            return IsCargoPackage(item);
        }

        /// Whether an item is train cargo, as opposed to a loose shape or fluid blob.
        ///
        /// Both concrete package types are admitted - the game instantiates its cargo machinery
        /// as CargoPackage&lt;ShapeId&gt; and CargoPackage&lt;FluidId&gt; - so one belt type
        /// serves both lines rather than needing a shape variant and a fluid variant.
        public static bool IsCargoPackage(IBeltItem item)
        {
            return item is PackageOnTrack<CargoPackage<ShapeId>>
                || item is PackageOnTrack<CargoPackage<FluidId>>;
        }

        /// The three lanes per layer a cargo belt does not use.
        ///
        /// Kept as a refusal rather than by leaving the lane out, because the bundle is a fixed
        /// twelve entries and the connector wiring pairs lane n to lane n on the neighbour. A
        /// missing lane is not expressible; a lane nothing will enter is.
        private static bool RefuseEverything(IBeltItem item)
        {
            return false;
        }

        public void ClearContent()
        {
            PathBundle.Clear();

            for (int layer = 0; layer < Intakes.Length; layer++)
            {
                Intakes[layer].Clear();
            }
        }

        public void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser
        {
            PathBundle.TraverseLanes(traverser);
        }

        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            PathBundle.Update(deltaTicks);

            for (short layer = 0; layer < Intakes.Length; layer++)
            {
                CargoIntake intake = Intakes[layer];

                // Only cleared once the package is actually on the belt. If the lane is full the
                // package waits in the intake, which is what makes a packed line back up rather
                // than lose cargo.
                if (intake.TryTakeReady(out IBeltItem package))
                {
                    CargoBeltLane lane = PathBundle.GetLane(CargoLanes.Travel, layer);
                    if (lane.CanAcceptItem(package))
                    {
                        lane.HandOverItem(package, Ticks.Zero);
                        intake.ClearTaken();
                    }
                }

                intake.Update(startTicks, deltaTicks);
            }
        }

        /// Both indices, deliberately. See NumItemReceiverBundles.
        public IItemReceiverBundle GetItemReceiverBundle(int inputIndex)
        {
            return PathBundle;
        }

        /// Guarded, unlike the receiving side.
        ///
        /// A cargo belt's output connector is belt-tagged and so will connect to an ordinary
        /// space belt, a space pipe, or a platform's belt port - all of which take a cargo
        /// package and destroy it somewhere downstream. The connection cannot be refused, so the
        /// hand-over is. See CargoHandover.
        ///
        /// One wrapper for both indices, built once: `ItemOutputChunkConnector.TryConnect`
        /// assigns `NextBundle` on whatever this returns, so a fresh wrapper per call would mean
        /// the connection landing on an object nothing else holds.
        public IItemProviderBundle GetItemProviderBundle(int outputIndex)
        {
            return Outgoing;
        }
    }
}
