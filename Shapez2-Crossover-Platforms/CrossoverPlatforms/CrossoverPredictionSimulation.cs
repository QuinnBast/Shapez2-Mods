using System;
using Game.Content.Features.Predictions;
using Game.Content.Features.SpacePaths.Prediction;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// What a crossing contributes to the item prediction graph.
    ///
    /// Prediction is a second simulation the game runs beside the real one, purely to answer
    /// "what will arrive here". It is what fills in the shape and fluid readouts on ports and in
    /// the build overlay. Nothing links it to <see cref="CrossoverSimulation"/>: an island that
    /// simulates correctly but registers no prediction is a hole in that graph, and everything
    /// downstream of it reads as empty even while real items are flowing through.
    ///
    /// Vanilla's SpacePathPredictionSimulation is a single bundle handed out as both receiver and
    /// provider, which makes a straight segment forward whatever it is told. A crossing is two of
    /// those side by side, indexed the same way <see cref="CrossoverSimulation"/> indexes its
    /// lanes - ConnectableIslandPredictionSimulation pairs prediction bundles to connectors by the
    /// same declaration order that ConnectableIslandSimulation uses for the real ones, so one
    /// order serves both.
    public class CrossoverPredictionSimulation : IItemBundlePredictionSimulation, ISimulation,
        IUpdatableSimulation
    {
        /// West to East.
        private readonly ItemPredictionBundle<ItemPredictionConverter> PathA =
            ItemPredictionBundle.Create<ItemPredictionConverter>();

        /// North to South.
        private readonly ItemPredictionBundle<ItemPredictionConverter> PathB =
            ItemPredictionBundle.Create<ItemPredictionConverter>();

        public int NumItemReceiverBundles => 2;

        public int NumItemProviderBundles => 2;

        public IItemPredictionReceiverBundle GetItemReceiverBundle(int inputIndex)
        {
            return inputIndex switch
            {
                0 => PathA,
                1 => PathB,
                _ => throw new ArgumentOutOfRangeException(nameof(inputIndex), inputIndex, null)
            };
        }

        public IItemPredictionProviderBundle GetItemProviderBundle(int outputIndex)
        {
            return outputIndex switch
            {
                0 => PathA,
                1 => PathB,
                _ => throw new ArgumentOutOfRangeException(nameof(outputIndex), outputIndex, null)
            };
        }

        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            PathA.Update(deltaTicks);
            PathB.Update(deltaTicks);
        }
    }
}
