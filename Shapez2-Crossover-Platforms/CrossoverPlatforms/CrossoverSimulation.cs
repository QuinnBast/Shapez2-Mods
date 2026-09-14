using System;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Two straight paths through one island chunk that never exchange items.
    ///
    /// The independence is not enforced here - it falls out of how the game wires islands up.
    /// ConnectableIslandSimulation pairs receiver bundle i with the i-th
    /// ISpacePathInputConnector and provider bundle i with the i-th ISpacePathOutputConnector,
    /// both in declaration order. So returning the same bundle for receiver 0 and provider 0
    /// makes the first input feed the first output and nothing else, exactly as the vanilla
    /// SpaceConveyorSimulation does for a single path.
    ///
    /// That index pairing is also what makes belt-crossing-belt possible at all: both paths use
    /// SpaceBeltInputConnector, so nothing in the connector types distinguishes them, and
    /// declaration order is the only thing that has to be right.
    public class CrossoverSimulation : Simulation<CrossoverSimulationState>, IItemBundleSimulation,
        ISimulation, IUpdatableSimulation
    {
        /// West to East.
        public readonly ItemLaneBundle<FastBeltPathLane> PathA;

        /// North to South.
        public readonly ItemLaneBundle<FastBeltPathLane> PathB;

        public int NumItemReceiverBundles => 2;

        public int NumItemProviderBundles => 2;

        public CrossoverSimulation(BeltSpeed speedA, BeltSpeed speedB, CrossoverSimulationState state)
            : base(state)
        {
            PathA = ItemLaneBundle.Create(state.PathAState,
                (FastBeltPathLaneState laneState) => new FastBeltPathLane(speedA, laneState));
            PathB = ItemLaneBundle.Create(state.PathBState,
                (FastBeltPathLaneState laneState) => new FastBeltPathLane(speedB, laneState));
        }

        public void ClearContent()
        {
            PathA.Clear();
            PathB.Clear();
        }

        public void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser
        {
            PathA.TraverseLanes(traverser);
            PathB.TraverseLanes(traverser);
        }

        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            PathA.Update(deltaTicks);
            PathB.Update(deltaTicks);
        }

        public IItemReceiverBundle GetItemReceiverBundle(int inputIndex)
        {
            return inputIndex switch
            {
                0 => PathA,
                1 => PathB,
                _ => throw new ArgumentOutOfRangeException(nameof(inputIndex), inputIndex, null)
            };
        }

        public IItemProviderBundle GetItemProviderBundle(int outputIndex)
        {
            return outputIndex switch
            {
                0 => PathA,
                1 => PathB,
                _ => throw new ArgumentOutOfRangeException(nameof(outputIndex), outputIndex, null)
            };
        }
    }
}
