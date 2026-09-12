using System;
using Core.Factory;
using Game.Content.Features.Predictions;
using Game.Content.Features.SpacePaths.Prediction;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack.Predictions;

namespace TrainCargoTools
{
    /// What a cargo island contributes to the item prediction graph.
    ///
    /// Prediction is a second simulation the game runs beside the real one, purely to answer
    /// "what will arrive here". `IslandPredictionRenderer` draws a shape or fluid bubble at the
    /// pivot of every provider bundle whose `NextBundle` is null - that is, at the end of a line
    /// that feeds nothing. An island with no prediction simulation registers no receiver bundle,
    /// so `ItemPredictionOutputChunkConnector.TryConnect` finds nothing to link to and the belt
    /// feeding it keeps reading as dangling. That is where the bubbles on the entrance of a cargo
    /// packager came from: they belonged to the ordinary belt in front of it, not to the packager.
    ///
    /// Every piece here predicts by forwarding. That is not a shortcut: a package is not an
    /// `IItem`, so there is nothing to predict *as* a package, and the useful readout at the end
    /// of a cargo line is which shape or fluid is inside the packages anyway. A packager's output
    /// therefore predicts its input shape, a belt carries it along, and an unpackager - which
    /// really does emit that shape - predicts it correctly by accident of the same rule.
    public sealed class CargoPredictionSimulation : IItemBundlePredictionSimulation, ISimulation,
        IUpdatableSimulation
    {
        /// One bundle, handed out however many times the island claims. The dual-connector
        /// islands claim two of each for the same reason `CargoBeltSimulation` does - a belt
        /// connector and a pipe connector share a pivot, and `ConnectableIslandPredictionSimulation`
        /// bounds its loop by `min(NumItemReceiverBundles, connectors.Count)`, so a second
        /// connector is silently dropped unless a second bundle is claimed.
        ///
        /// Sharing one bundle rather than making two matters: whichever connector links first sets
        /// `NextBundle`, and the renderer then sees a non-null `NextBundle` through *both* indices.
        /// Two separate bundles would leave the unused one dangling.
        private readonly ItemPredictionBundle<ItemPredictionConverter> Bundle =
            ItemPredictionBundle.Create<ItemPredictionConverter>();

        private readonly int Bundles;

        public CargoPredictionSimulation(int bundles)
        {
            Bundles = bundles;
        }

        public int NumItemReceiverBundles => Bundles;

        public int NumItemProviderBundles => Bundles;

        public IItemPredictionReceiverBundle GetItemReceiverBundle(int inputIndex)
        {
            return Check(inputIndex);
        }

        public IItemPredictionProviderBundle GetItemProviderBundle(int outputIndex)
        {
            return Check(outputIndex);
        }

        public void Update(Ticks startTicks, Ticks deltaTicks)
        {
            Bundle.Update(deltaTicks);
        }

        private ItemPredictionBundle<ItemPredictionConverter> Check(int index)
        {
            if (index < 0 || index >= Bundles)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, null);
            }

            return Bundle;
        }
    }

    /// Supplies it. Forwarding has nothing to read off the scenario, so - as in
    /// CrossoverPredictionFactory - the builder and the factory are the same object.
    internal sealed class CargoPredictionFactory
        : IIslandPredictionFactoryBuilder<CargoPredictionSimulation>,
            IFactory<CargoPredictionSimulation>
    {
        private readonly int Bundles;

        public CargoPredictionFactory(int bundles)
        {
            Bundles = bundles;
        }

        public IFactory<CargoPredictionSimulation> BuildFactory(
            PredictionSystemsDependencies dependencies)
        {
            return this;
        }

        public CargoPredictionSimulation Produce()
        {
            return new CargoPredictionSimulation(Bundles);
        }
    }
}
