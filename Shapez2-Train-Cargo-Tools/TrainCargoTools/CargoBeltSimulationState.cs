using Game.Content.Features.Fluids;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Serialization;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// One 12-lane bundle, saved.
    ///
    /// Mirrors the vanilla SpaceConveyorSimulationState. The slot count is deliberately larger
    /// than a space belt: buffering is the whole reason a cargo belt exists, since a train
    /// station holds only a handful of containers and a stalled train stalls the line behind it.
    [SyncableIdentifier("TrainCargoToolsCargoBeltState")]
    public class CargoBeltSimulationState : ISimulationState, ISyncable
    {
        /// Shared with the renderer so the two cannot disagree about spacing - see CargoLanes.
        ///
        /// Changing this is save-safe, unlike adding a field: FastBeltPathLaneState.Sync reads
        /// the stored capacity and calls Clear() when it differs, so an existing belt comes back
        /// empty rather than corrupt.
        private const short NumItemsPerLane = CargoLanes.SlotsPerLane;

        public BundleState<FastBeltPathLaneState> PathBundleState { get; }

        /// Partially packed cargo, one per layer, for the belt's own intake - see CargoIntake.
        ///
        /// Both item types are kept even though a given belt only ever sees one, because its
        /// connector tags decide which and the state type is shared between the two lines.
        ///
        /// **Deliberately not serialized.** Adding these to Sync changed the blob's length, and
        /// every save written before they existed then failed to load: each simulation state sits
        /// in its own length-delimited ReadBlob, so a Sync that reads more than was written walks
        /// off the end of its blob into the next one and the error surfaces far away as
        /// `Bad string LUT index` out of `ShapeItemSerializer`. That cost a real save.
        ///
        /// Not serializing costs at most PackageSize-1 items per layer on a belt that is
        /// mid-pack, and only on the few segments actually fed loose items - everything already
        /// packed is on the lane bundle, which is saved. A bounded, near-invisible loss against
        /// breaking every existing save.
        ///
        /// If this ever does need persisting, it needs a scheme that can tell an old blob from a
        /// new one. `IPrimitiveSerializationVisitor.Version` is the *game's* version, so it
        /// cannot distinguish two versions of this mod; a mod-level version written once at the
        /// front of this state would, and would have to be added before the first field.
        public readonly TrainCargoFillingContainerState<ShapeId>[] ShapeIntake;
        public readonly TrainCargoFillingContainerState<FluidId>[] FluidIntake;

        public CargoBeltSimulationState()
        {
            PathBundleState = BundleState.Create(() => new FastBeltPathLaneState(NumItemsPerLane));

            ShapeIntake = new TrainCargoFillingContainerState<ShapeId>[SpacePathConstants.NumLayers];
            FluidIntake = new TrainCargoFillingContainerState<FluidId>[SpacePathConstants.NumLayers];
            for (int layer = 0; layer < SpacePathConstants.NumLayers; layer++)
            {
                ShapeIntake[layer] = new TrainCargoFillingContainerState<ShapeId>();
                FluidIntake[layer] = new TrainCargoFillingContainerState<FluidId>();
            }
        }

        /// Only the lane bundle. See the note on the intake fields for why they are left out.
        public void Sync(ISerializationVisitor visitor)
        {
            PathBundleState.Sync(visitor);
        }
    }
}
