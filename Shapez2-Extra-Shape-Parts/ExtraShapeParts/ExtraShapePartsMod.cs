using System;
using Game.Core.Content;
using Game.Orchestration;
using JetBrains.Annotations;
using MonoMod.RuntimeDetour;
using ShapezShifter.Hijack;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// Adds shape quadrant types beyond the game's circle, square, windmill and star, and puts them
    /// into map generation so they can actually be mined.
    ///
    /// Two injections, at two different moments, and the order between them matters.
    ///
    /// The parts in <see cref="ExtraShapePartCatalog"/> are appended to every `ShapesConfiguration`
    /// the game loaded, from a prefix on `Init_3_SavegameAndMode`. Everything downstream - the shape
    /// code parser, the map generator, the renderer, the map resource filter, research goal
    /// generation - reads its parts from there, so none of it needs its own hook.
    ///
    /// The chains in <see cref="SideQuestCatalog"/> are then appended to the scenario's research
    /// progression from an `IGameScenarioRewirer`, which ShapezShifter runs when `GameMode.From`
    /// constructs the `GameScenario` - **after** the prefix above, which is what lets a quest ask
    /// for a shape made of the parts this mod just added.
    [UsedImplicitly]
    public class ExtraShapePartsMod : IMod
    {
        /// The part count the catalogue's geometry is described against, and the one every
        /// shipped quad configuration uses. Not a limit - the injector builds whatever count a
        /// configuration declares - just the one worth validating at load.
        private const int ReferencePartCount = 4;

        private readonly ILogger Logger;

        private readonly Hook InjectionHook;

        private readonly RewirerHandle ConsoleHandle;

        private readonly RewirerHandle SideQuestHandle;

        private readonly Hook LegendLayoutHook;

        public ExtraShapePartsMod(ILogger logger)
        {
            Logger = logger;

            // Built here rather than at first use so a broken profile fails at mod load, where the
            // error is attributable, instead of half way through a session load.
            //
            // Only the four-part meshes: which part counts actually exist is not knowable until
            // `IGameData` arrives, and the rest are built per configuration at injection time,
            // inside the try/catch there. Four is enough to fail fast, because a profile that
            // throws - a null outline, too few points, a repeated point - throws at every sector.
            foreach (ShapePartProfile profile in ExtraShapePartCatalog.All)
            {
                ShapePartFactory.GetOrCreate(profile, ReferencePartCount);
            }

            InjectionHook = CreateInjectionHook();
            LegendLayoutHook = ShapeCodesPreviewLayout.Create(logger);
            ConsoleHandle = GameRewirers.AddRewirer(new ShapePartConsole(logger));
            SideQuestHandle = GameRewirers.AddRewirer(new SideQuestRewirer(logger));

            logger.Info?.Log(
                $"Extra Shape Parts ready: {ExtraShapePartCatalog.All.Count} parts, " +
                $"{SideQuestCatalog.All.Count} side quest chains");
        }

        /// Injection has to happen after `GameData` exists and before the session reads its shape
        /// configuration, and there is no event for that.
        ///
        /// `Init_3_SavegameAndMode` is the session stage that picks the `GameMode`, and so the
        /// configuration, out of `IGameData`. Everything that caches parts runs later:
        /// `StrictShapeDefinitionFactory` and `MapShapeGenerator` in stage 6, the
        /// `UniversalShapeRenderer` in stage 7. It is private, which the publicizer handles, and
        /// void, which `CreatePrefixHook` requires.
        private Hook CreateInjectionHook()
        {
            return DetourHelper.CreatePrefixHook<GameSessionOrchestrator, IContent, IGameData, IGameStartOptions>(
                (orchestrator, content, gameData, options) =>
                    orchestrator.Init_3_SavegameAndMode(content, gameData, options),
                (orchestrator, content, gameData, options) =>
                {
                    Inject(gameData);
                    return (content, gameData, options);
                });
        }

        /// A throw here would take session load down with it, and a missing shape part is a far
        /// smaller problem than a game that will not start.
        private void Inject(IGameData gameData)
        {
            try
            {
                ShapePartConsole.GameData = gameData;
                ShapePartInjector.Inject(gameData, ExtraShapePartCatalog.All, Logger);
            }
            catch (Exception exception)
            {
                Logger.Error?.Log("Could not register extra shape parts: " + exception);
            }
        }

        public void Dispose()
        {
            InjectionHook?.Dispose();
            LegendLayoutHook?.Dispose();
            GameRewirers.RemoveRewirer(ConsoleHandle);
            GameRewirers.RemoveRewirer(SideQuestHandle);
        }
    }
}
