using Core.Factory;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack.Predictions;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Supplies a crossing's prediction simulation.
    ///
    /// Unlike <see cref="CrossoverSimulationFactory"/> there is nothing to read off the scenario:
    /// a crossing predicts by forwarding, and forwarding has no speed, so the builder and the
    /// factory can be the same object.
    internal sealed class CrossoverPredictionFactory
        : IIslandPredictionFactoryBuilder<CrossoverPredictionSimulation>,
            IFactory<CrossoverPredictionSimulation>
    {
        public IFactory<CrossoverPredictionSimulation> BuildFactory(
            PredictionSystemsDependencies dependencies)
        {
            return this;
        }

        public CrossoverPredictionSimulation Produce()
        {
            return new CrossoverPredictionSimulation();
        }
    }
}
