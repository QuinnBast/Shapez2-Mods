using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Game.Core.Research;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtendedResearch;

/// <summary>
/// The one hook the mod needs.
///
/// Shifter runs this immediately after the game builds a <see cref="GameScenario"/>, which is
/// the only moment where the research layout is fully assembled and nothing has read it yet.
/// The scenario's own validation has already passed by then, so additions here are not
/// re-checked - which is a licence to be careful rather than a licence to be sloppy.
///
/// Nothing is hardcoded to a vanilla id. The upgrades to extend are discovered through the
/// roles the scenario itself declares: the speed-to-upgrade mapping, and the named chunk
/// limit / hub input / shape quantity fields. A game update that renames an upgrade or adds
/// a sixth speed is picked up rather than missed, and every id that gets touched is logged
/// so an override can be written against it.
/// </summary>
internal class ExtendedResearchScenarioExtender : IGameScenarioRewirer
{
    private readonly ExtendedResearchConfig Config;
    private readonly ILogger Logger;

    /// <summary>
    /// GameMode.From builds a fresh scenario per call, so this normally never fires. It is
    /// here because applying twice would silently double every tier list, and that is a bad
    /// enough failure to be worth one dictionary.
    /// </summary>
    private readonly ConditionalWeakTable<GameScenario, object> AlreadyExtended =
        new ConditionalWeakTable<GameScenario, object>();

    public ExtendedResearchScenarioExtender(ExtendedResearchConfig config, ILogger logger)
    {
        Config = config;
        Logger = logger;
    }

    public GameScenario ModifyGameScenario(GameScenario gameScenario)
    {
        if (gameScenario == null)
        {
            return null;
        }

        try
        {
            if (AlreadyExtended.TryGetValue(gameScenario, out _))
            {
                Logger.Info?.Log("Extended Research: scenario already extended, skipping.");
                return gameScenario;
            }

            AlreadyExtended.Add(gameScenario, string.Empty);

            Logger.Info?.Log("Extended Research: extending scenario '" + gameScenario.UniqueId + "'.");

            ExtendLinearUpgrades(gameScenario);
            LayerUnlockExtender.ExtendBuildingLayers(gameScenario, Config, Logger);
            LayerUnlockExtender.ExtendIslandLayers(gameScenario, Config, Logger);
        }
        catch (Exception exception)
        {
            // A throw here would take the scenario - and the save - with it. A partially
            // extended research tree is recoverable; a game that will not load is not.
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log("Extended Research: extension failed, continuing with whatever was applied.");
        }

        return gameScenario;
    }

    private void ExtendLinearUpgrades(GameScenario scenario)
    {
        ResearchProgressionLinearUpgrades linear = scenario.Progression.LinearUpgrades;
        HashSet<ResearchLinearUpgradeId> handled = new HashSet<ResearchLinearUpgradeId>();

        foreach (KeyValuePair<ResearchSpeedId, ResearchLinearUpgradeId> entry in linear.SpeedsToUpgradeMapping)
        {
            ExtendOne(linear, entry.Value, TiersForSpeed(entry.Key), handled);
        }

        ExtendOne(linear, linear.ChunkLimitAddUpgrade, Config.ExtraPlatformCapacityTiers, handled);
        ExtendOne(linear, linear.HubInputSize, Config.ExtraHubInputTiers, handled);
        ExtendOne(linear, linear.ShapeQuantityUpgrade, Config.ExtraShapeQuantityTiers, handled);
    }

    /// <summary>
    /// Train wagon capacity is modelled as a "speed" like everything else - see
    /// <c>TrainCargoExchangeConfiguration.TrainWagonCapacityResearch</c> - so the two train
    /// dials are told apart by id rather than by type. Anything unrecognised is treated as
    /// production speed, which is the safe default: it is the bucket every belt, cutter,
    /// stacker and painter already falls into.
    /// </summary>
    private int TiersForSpeed(ResearchSpeedId speed)
    {
        string id = speed.Id ?? string.Empty;

        if (id == "TrainSpeed")
        {
            return Config.ExtraTrainSpeedTiers;
        }

        if (id.IndexOf("Train", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return Config.ExtraTrainCapacityTiers;
        }

        return Config.ExtraProductionSpeedTiers;
    }

    private void ExtendOne(
        ResearchProgressionLinearUpgrades linear,
        ResearchLinearUpgradeId upgradeId,
        int defaultTiers,
        HashSet<ResearchLinearUpgradeId> handled)
    {
        if (upgradeId.IsEmpty || !handled.Add(upgradeId))
        {
            return;
        }

        if (!linear.UpgradesById.TryGetValue(upgradeId, out ResearchLinearUpgrade upgrade))
        {
            Logger.Error?.Log("Extended Research: scenario references unknown linear upgrade '" + upgradeId.Id + "'.");
            return;
        }

        int tiers = Config.TryGetOverride(upgradeId.Id, out int overridden) ? overridden : defaultTiers;
        if (tiers <= 0)
        {
            Logger.Info?.Log("Extended Research: leaving '" + upgradeId.Id + "' at " + upgrade.MaxLevel + " tiers.");
            return;
        }

        LinearUpgradeExtender.Extend(upgrade, tiers, Config, Logger);
    }
}
