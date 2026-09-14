using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtendedResearch;

/// <summary>
/// Extended Research.
///
/// Vanilla's research runs out before the factory does. This adds more of what was already
/// there rather than anything new: further tiers on the speed, platform capacity and train
/// upgrades, and additional machine and space levels bought from the shop.
///
/// The whole mod is one <see cref="IGameScenarioRewirer"/>. There is no simulation, no
/// rendering and no hook on the tick - the research layout is data, and this edits the data
/// once, at the moment the scenario is built.
///
/// It does change what a save contains: upgrade levels past vanilla's maximum are written
/// into the savegame, and loading such a save without the mod throws when the research
/// manager finds a level higher than the upgrade has tiers. Hence AffectsSaveGames.
/// </summary>
[UsedImplicitly]
public class ExtendedResearchMod : IMod
{
    private readonly ILogger Logger;
    private readonly RewirerHandle Handle;

    public ExtendedResearchMod(ILogger logger)
    {
        Logger = logger;

        ExtendedResearchConfig config = ExtendedResearchConfig.Load(logger);
        Handle = GameRewirers.AddRewirer(new ExtendedResearchScenarioExtender(config, logger));

        Logger.Info?.Log("Extended Research ready.");
    }

    public void Dispose()
    {
        GameRewirers.RemoveRewirer(Handle);
    }
}
