using System;
using System.Runtime.CompilerServices;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// The hook the side quests need, and the reason they do not need a detour.
    ///
    /// ShapezShifter runs `IGameScenarioRewirer` immediately after the game constructs a
    /// `GameScenario`, which is the only moment where the research progression is fully assembled
    /// and nothing has read it yet. That is later than this mod's own shape part injection - the
    /// prefix on `Init_3_SavegameAndMode` runs before `GameMode.From` builds the scenario - so by
    /// the time this fires, the extra parts are already in every shape configuration and a quest
    /// asking for one will resolve.
    ///
    /// It is also after `ResearchProgression`'s constructor has validated and pruned, so nothing
    /// added here is re-checked. That is a licence to be careful rather than a licence to be
    /// sloppy: <see cref="SideQuestInjector"/> does its own validation because nothing else will.
    internal class SideQuestRewirer : IGameScenarioRewirer
    {
        private readonly ILogger Logger;

        /// `GameMode.From` builds a fresh scenario per call, so this normally never fires. It is
        /// here because applying twice would duplicate every chain, and `ResearchProgression` throws
        /// on a duplicate upgrade id - in its constructor, which has already run, so the throw would
        /// land somewhere much less obvious instead.
        private readonly ConditionalWeakTable<GameScenario, object> AlreadyAdded =
            new ConditionalWeakTable<GameScenario, object>();

        public SideQuestRewirer(ILogger logger)
        {
            Logger = logger;
        }

        public GameScenario ModifyGameScenario(GameScenario gameScenario)
        {
            if (gameScenario?.Progression == null)
            {
                return gameScenario;
            }

            try
            {
                if (AlreadyAdded.TryGetValue(gameScenario, out _))
                {
                    return gameScenario;
                }

                AlreadyAdded.Add(gameScenario, string.Empty);

                IGameData gameData = ShapePartConsole.GameData;
                if (gameData == null)
                {
                    // The shape configuration is looked up by id, and `GameScenario` carries the id
                    // but not the configuration. Without `IGameData` there is no way to know how
                    // many parts a shape has here, and a quest built for the wrong part count is
                    // an unbuildable quest rather than a cosmetic problem.
                    Logger.Warning?.Log(
                        "Side quests skipped: no game data yet. This means the shape part " +
                        "injection hook did not run before the scenario was built.");
                    return gameScenario;
                }

                int added = SideQuestInjector.Inject(gameScenario, gameData, Logger);
                Logger.Info?.Log(
                    $"Side quests: {added} added to scenario '{gameScenario.UniqueId}'.");
            }
            catch (Exception exception)
            {
                // A throw here would take the scenario, and so the save, with it. Missing side
                // quests are recoverable; a game that will not load is not.
                Logger.Exception?.LogException(exception);
                Logger.Error?.Log("Side quests could not be added; continuing without them.");
            }

            return gameScenario;
        }
    }
}
