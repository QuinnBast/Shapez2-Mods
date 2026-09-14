using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PredictableBeltFilters
{
    /// <summary>
    /// Predictable Belt Filters.
    ///
    /// Shape predictions are a second simulation the game runs beside the real one, purely to
    /// answer "what can arrive here" - it is what fills the shape readouts on belts, ports and
    /// placement previews. Belt filters are the one item-routing building whose entry in that
    /// graph does not match its behaviour: BuiltinPredictionSimulationSystems registers the filter
    /// with a SplitterPredictionSimulationFactory(2), so both outputs are predicted to carry
    /// everything the input carries.
    ///
    /// The cost is not cosmetic. A predicted set holds at most four items, and anything past the
    /// fourth is dropped without a marker; separately, an operation that fails on every candidate
    /// it is handed returns PredictedItem.Degenerated, which the next belt lane turns into
    /// nothing at all. A filter that never narrows feeds both of those - which is why the usual
    /// report is not "slightly wrong" but "my belts stopped showing anything".
    ///
    /// The whole mod is one <see cref="IPredictionSystemsRewirer"/>. There is no new building, no
    /// toolbar entry, no rendering and no hook on the tick, and nothing is serialised - which is
    /// why AffectsSaveGames is false and hot reload works.
    /// </summary>
    [UsedImplicitly]
    public class PredictableBeltFiltersMod : IMod
    {
        /// The prediction half: replaces vanilla's splitter-shaped prediction for the filter.
        private readonly RewirerHandle PredictionHandle;

        /// The real-simulation half: publishes where each filter's live state actually lives.
        /// Needed because the prediction graph's own BuildingInstance cannot be trusted to carry
        /// it - see <see cref="FilterStateRegistry"/>.
        private readonly RewirerHandle StateHandle;

        public PredictableBeltFiltersMod(ILogger logger)
        {
            FilterStateRegistry registry = new FilterStateRegistry();

            StateHandle = GameRewirers.AddRewirer(new FilterStateRewirer(registry, logger));
            PredictionHandle = GameRewirers.AddRewirer(new BeltFilterPredictionRewirer(registry, logger));

            logger.Info?.Log("Predictable Belt Filters ready.");
        }

        public void Dispose()
        {
            GameRewirers.RemoveRewirer(PredictionHandle);
            GameRewirers.RemoveRewirer(StateHandle);
        }
    }
}
