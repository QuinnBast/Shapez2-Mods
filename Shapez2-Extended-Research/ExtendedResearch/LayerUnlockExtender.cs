using System;
using System.Collections.Generic;
using Core.Localization;
using Game.Core.Research;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtendedResearch;

/// <summary>
/// Adds building layers ("machine levels") and island layers ("space levels").
///
/// Neither limit is a constant. <c>GameMode.MaxBuildingLayer</c> is the <em>count</em> of
/// <c>Mechanics.BuildingLayerUnlocks</c> and <c>GameMode.MaxIslandLayer</c> the count of
/// <c>Mechanics.IslandLayerUnlocks</c>, so a layer exists precisely because a research
/// mechanic exists to unlock it. Adding a layer therefore means adding a mechanic, and
/// giving the player some way to earn it - here, a shop side upgrade bought with research
/// points.
///
/// Which layer a given unlock grants is not the same question as how many there are.
/// Building layers are positional: unlocking entry <c>i</c> allows layer <c>i + 1</c>. Island
/// layers carry an explicit <c>IslandLayersUnlockOrder</c> alongside them, because vanilla
/// hands out layers above and below the starting one in an interleaved order rather than
/// bottom to top - so new entries continue from the highest order in use, not from the count.
/// </summary>
public static class LayerUnlockExtender
{
    private const string MechanicIdPrefix = "ExtendedResearch.";
    private const string UpgradeIdPrefix = "ExtendedResearch.Unlock";

    public static int ExtendBuildingLayers(GameScenario scenario, ExtendedResearchConfig config, ILogger logger)
    {
        ResearchMechanics mechanics = scenario.Mechanics;
        List<ResearchMechanicId> unlocks = mechanics.BuildingLayerUnlocks;

        int room = ExtendedResearchConfig.MaxSupportedBuildingLayerUnlocks - unlocks.Count;
        int requested = Math.Max(0, config.ExtraBuildingLayers);
        int extra = Math.Min(requested, Math.Max(0, room));

        if (extra < requested)
        {
            logger.Error?.Log(
                "Extended Research: clamping extra building layers to " + extra +
                "; only 3 building layers have meshes (BuildingDrawDataFactory builds MainMeshPerLayer "
                + "as a 3-element array), and a building above that renders nothing and takes its whole "
                + "chunk's mesh down with it.");
        }

        if (extra == 0)
        {
            return 0;
        }

        if (!TryBorrowImageId(scenario.Progression, out GameImageId imageId))
        {
            logger.Error?.Log(
                "Extended Research: no existing side upgrade to borrow a preview image from; "
                + "skipping extra building layers rather than adding shop entries that would break the research screen.");
            return 0;
        }

        // Building layers are positional - unlocking entry n allows layer n + 1 - so the
        // current ceiling is simply the last entry.
        ResearchMechanicId? topLayerGate = unlocks.Count > 0
            ? unlocks[unlocks.Count - 1]
            : (ResearchMechanicId?)null;

        string iconId = BorrowIconId(mechanics, unlocks);
        ResearchUpgradeId previousUpgrade = default;

        for (int i = 0; i < extra; i++)
        {
            // Unlocking index n allows building layer n + 1, so this is the layer the
            // player is buying and the number worth showing them.
            int layer = unlocks.Count + 1;

            ResearchMechanicId mechanicId = AddMechanic(
                mechanics,
                MechanicIdPrefix + "BuildingLayer" + layer,
                "@extended-research.machine-level.mechanic.title",
                "@extended-research.machine-level.mechanic.description",
                iconId);

            unlocks.Add(mechanicId);

            previousUpgrade = AddShopUnlock(
                scenario,
                config,
                logger,
                upgradeId: UpgradeIdPrefix + "BuildingLayer" + layer,
                titleKey: "extended-research.machine-level.title",
                descriptionKey: "extended-research.machine-level.description",
                number: layer,
                mechanicId: mechanicId,
                imageId: imageId,
                cost: ScaledCost(config.BuildingLayerBaseCost, config.LayerCostGrowthFactor, i),
                // The first one appears only once vanilla's last layer is already unlocked;
                // after that they chain, so they are bought in order.
                gateMechanic: i == 0 ? topLayerGate : (ResearchMechanicId?)null,
                gateUpgrade: i == 0 ? (ResearchUpgradeId?)null : previousUpgrade);
        }

        logger.Info?.Log("Extended Research: building layers " + (unlocks.Count - extra) + " -> " + unlocks.Count + ".");
        return extra;
    }

    public static int ExtendIslandLayers(GameScenario scenario, ExtendedResearchConfig config, ILogger logger)
    {
        ResearchMechanics mechanics = scenario.Mechanics;
        List<ResearchMechanicId> unlocks = mechanics.IslandLayerUnlocks;
        List<int> order = mechanics.IslandLayersUnlockOrder;

        int extra = Math.Max(0, config.ExtraIslandLayers);
        if (extra == 0)
        {
            return 0;
        }

        if (order.Count != unlocks.Count)
        {
            logger.Error?.Log(
                "Extended Research: IslandLayersUnlockOrder has " + order.Count + " entries for " +
                unlocks.Count + " unlocks; skipping island layers rather than guessing.");
            return 0;
        }

        if (!TryBorrowImageId(scenario.Progression, out GameImageId imageId))
        {
            logger.Error?.Log(
                "Extended Research: no existing side upgrade to borrow a preview image from; "
                + "skipping extra island layers rather than adding shop entries that would break the research screen.");
            return 0;
        }

        // Vanilla hands out island layers above and below the starting one in an interleaved
        // order, so the topmost layer is the one with the highest entry here - not the last
        // unlock in the list. That distinction matters for the gate below: the first new
        // layer should follow on from the current ceiling, whichever unlock granted it.
        int highestOrder = 0;
        int highestOrderIndex = -1;
        for (int i = 0; i < order.Count; i++)
        {
            if (highestOrderIndex < 0 || order[i] > highestOrder)
            {
                highestOrder = order[i];
                highestOrderIndex = i;
            }
        }

        ResearchMechanicId? topLayerGate = highestOrderIndex >= 0
            ? unlocks[highestOrderIndex]
            : (ResearchMechanicId?)null;

        string iconId = BorrowIconId(mechanics, unlocks);
        ResearchUpgradeId previousUpgrade = default;

        for (int i = 0; i < extra; i++)
        {
            int layer = highestOrder + i + 1;

            ResearchMechanicId mechanicId = AddMechanic(
                mechanics,
                MechanicIdPrefix + "IslandLayer" + layer,
                "@extended-research.space-level.mechanic.title",
                "@extended-research.space-level.mechanic.description",
                iconId);

            unlocks.Add(mechanicId);
            order.Add(layer);

            previousUpgrade = AddShopUnlock(
                scenario,
                config,
                logger,
                upgradeId: UpgradeIdPrefix + "IslandLayer" + layer,
                titleKey: "extended-research.space-level.title",
                descriptionKey: "extended-research.space-level.description",
                number: layer,
                mechanicId: mechanicId,
                imageId: imageId,
                cost: ScaledCost(config.IslandLayerBaseCost, config.LayerCostGrowthFactor, i),
                gateMechanic: i == 0 ? topLayerGate : (ResearchMechanicId?)null,
                gateUpgrade: i == 0 ? (ResearchUpgradeId?)null : previousUpgrade);
        }

        logger.Info?.Log("Extended Research: island layers up to " + (highestOrder + extra) + ".");
        return extra;
    }

    private static ResearchMechanicId AddMechanic(
        ResearchMechanics mechanics, string id, string titleKey, string descriptionKey, string iconId)
    {
        ResearchMechanicId mechanicId = new ResearchMechanicId(id);

        // ResearchMechanic only builds from serialized data, and SerializedResearchMechanic is
        // a plain field bag - so this is the constructor the game itself uses, not a bypass.
        mechanics.Mechanics.Add(new ResearchMechanic(new SerializedResearchMechanic
        {
            Id = id,
            Title = titleKey,
            Description = descriptionKey,
            IconId = iconId,
            HideReward = false,
        }));

        return mechanicId;
    }

    private static ResearchUpgradeId AddShopUnlock(
        GameScenario scenario,
        ExtendedResearchConfig config,
        ILogger logger,
        string upgradeId,
        string titleKey,
        string descriptionKey,
        int number,
        ResearchMechanicId mechanicId,
        GameImageId imageId,
        int cost,
        ResearchMechanicId? gateMechanic,
        ResearchUpgradeId? gateUpgrade)
    {
        ResearchUpgradeId id = new ResearchUpgradeId(upgradeId);

        IText title = titleKey.T().Bind("level", new RawText(number.ToString()));
        IText description = descriptionKey.T().Bind("level", new RawText(number.ToString()));

        SideUpgradePresentationData presentation = new SideUpgradePresentationData(
            id,
            imageId,
            GameVideoId.Empty,
            title,
            description,
            hidden: false,
            config.ShopCategory);

        ResearchMechanicId[] requiredMechanics = gateMechanic.HasValue
            ? new[] { gateMechanic.Value }
            : Array.Empty<ResearchMechanicId>();

        ResearchUpgradeId[] requiredUpgrades = gateUpgrade.HasValue
            ? new[] { gateUpgrade.Value }
            : Array.Empty<ResearchUpgradeId>();

        // Shifter's builder does the four-list registration the progression needs
        // (side upgrades, shop items, all upgrades, id lookup) and registers the category.
        SideUpgrade.New()
            .WithPresentationData(presentation)
            .WithCost(new IResearchCost[] { new ResearchCostPoints(new ResearchPointCurrency(cost)) })
            .WithCustomRequirements(requiredMechanics, requiredUpgrades)
            .WithAdditionalRewards(new IResearchReward[] { new ResearchRewardMechanic(mechanicId) })
            .Build(scenario.UniqueId, scenario.Progression);

        logger.Info?.Log("Extended Research: added shop upgrade '" + upgradeId + "' for " + cost + " points.");
        return id;
    }

    /// <summary>
    /// A shop entry's preview image is not optional. <c>HUDResearchSideUpgradeDisplay.RebuildView</c>
    /// calls <c>GameData.GetImage(upgrade.ImageId)</c> unconditionally, and <c>GetImage</c> throws
    /// on an id it cannot resolve - including the empty one. That throw propagates out of the
    /// whole <c>HUDResearchTree</c> prefab construction, leaving the research screen half-built
    /// and unclosable, so an empty image id is not a cosmetic problem but a broken game.
    ///
    /// Borrowing an id from an upgrade the game already renders is the only way to be certain
    /// it resolves, since image ids live in Unity assets rather than anywhere readable here.
    /// </summary>
    private static bool TryBorrowImageId(ResearchProgression progression, out GameImageId imageId)
    {
        foreach (ResearchSideUpgrade upgrade in progression.SideUpgrades)
        {
            if (upgrade.ImageId.HasValue)
            {
                imageId = upgrade.ImageId;
                return true;
            }
        }

        imageId = GameImageId.Empty;
        return false;
    }

    /// <summary>
    /// Reuses the icon of the layer unlock that came before, which is both guaranteed to
    /// resolve and already means "another layer" to anyone reading the research screen.
    /// </summary>
    private static string BorrowIconId(ResearchMechanics mechanics, List<ResearchMechanicId> unlocks)
    {
        for (int i = unlocks.Count - 1; i >= 0; i--)
        {
            if (mechanics.TryGetMechanic(unlocks[i], out ResearchMechanic mechanic))
            {
                return mechanic.IconId.Id;
            }
        }

        return string.Empty;
    }


    private static int ScaledCost(int baseCost, float growth, int step)
    {
        double scaled = baseCost * Math.Pow(Math.Max(1.0, growth), step);
        return (int)Math.Min(int.MaxValue, Math.Max(1, Math.Round(scaled)));
    }
}
