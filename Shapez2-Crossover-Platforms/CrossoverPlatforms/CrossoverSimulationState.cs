using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Serialization;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Two independent 12-lane bundles, saved side by side.
    ///
    /// Mirrors the vanilla SpaceConveyorSimulationState, which keeps one bundle for a straight
    /// segment. A crossing is two straight segments that never touch, so it needs two.
    [SyncableIdentifier("CrossoverPlatformsPathState")]
    public class CrossoverSimulationState : ISimulationState, ISyncable
    {
        /// Vanilla space paths use 16 slots per lane. Matching it keeps a crossing's buffering
        /// indistinguishable from the belt either side of it, so inserting one cannot change
        /// a line's throughput.
        private const short NumItemsPerLane = 16;

        public BundleState<FastBeltPathLaneState> PathAState { get; }

        public BundleState<FastBeltPathLaneState> PathBState { get; }

        public CrossoverSimulationState()
        {
            PathAState = BundleState.Create(() => new FastBeltPathLaneState(NumItemsPerLane));
            PathBState = BundleState.Create(() => new FastBeltPathLaneState(NumItemsPerLane));
        }

        public void Sync(ISerializationVisitor visitor)
        {
            PathAState.Sync(visitor);
            PathBState.Sync(visitor);
        }
    }
}
