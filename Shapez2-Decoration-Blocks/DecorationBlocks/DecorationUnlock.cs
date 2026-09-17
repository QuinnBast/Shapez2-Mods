using System;
using System.Linq;
using Core.Localization;
using Game.Core.Research;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// One research node that unlocks all 21 blocks.
///
/// UnlockedWithNewSideUpgrade is the obvious call and it is wrong for a set. Its extender runs
/// once per building group, and its body is SideUpgradeBuilder.Build, which appends to
/// _SideUpgrades, _ShopItems, _AllUpgrades and _UpgradesById every time. 21 blocks sharing
/// one builder puts 21 identical nodes in the shop. CustomSideUpgradeSelector is not the
/// way out either - its Select is itself a call to Build.
///
/// So this is a get-or-create selector. The first block to be extended in a given scenario
/// builds the node; the twenty after it find the built one by id, and
/// UnlockBuildingWithExistingSideUpgradeResearchProgressionExtender appends each group to its
/// Rewards. Order does not matter and nothing is registered twice.
///
/// The lookup keys on the ResearchProgression it is handed rather than on a cached field:
/// ExtendResearch runs once per scenario load with a fresh progression each time, and a
/// remembered upgrade would be appended to a progression that never contained it.
internal sealed class DecorationUnlock : ISideUpgradeSelector
{
    /// 4.8k as the research screen renders it. ResearchPointCurrency is in hundreds of
    /// displayed points - StringFormattingExtensions.Format is FormatIntegerMax4Digits(Amount
    /// * 100) - so passing 4800 here would price the node at 480k and still look plausible
    /// next to the five-figure nodes further down the tree.
    private const int Cost = 48;

    /// Preferred home in the research tree. The categories are authored per scenario and are
    /// not readable at build time, so this is matched against what the scenario actually has
    /// rather than asserted.
    private const string PreferredCategoryMarker = "Building";

    private readonly ResearchUpgradeId UpgradeId = new ResearchUpgradeId("DecorationBlocks_Blocks");

    private readonly ILogger Log;

    public DecorationUnlock(ILogger log)
    {
        Log = log;
    }

    public ResearchSideUpgrade Select(ScenarioId scenarioId, ResearchProgression progression)
    {
        // `is`, not a cast: _UpgradesById holds milestone levels and side quests too, and a
        // milestone answers this lookup as readily as a side upgrade does.
        if (progression.TryGetUpgrade(UpgradeId, out IResearchUpgrade existing)
            && existing is ResearchSideUpgrade built)
        {
            return built;
        }

        return Create(scenarioId, progression);
    }

    private ResearchSideUpgrade Create(ScenarioId scenarioId, ResearchProgression progression)
    {
        string category = ChooseCategory(progression);
        GameImageId image = BorrowImage(progression);

        SideUpgradePresentationData presentation = new SideUpgradePresentationData(
            UpgradeId,
            image,
            GameVideoId.Empty,
            "decoration-blocks.research.title".T(),
            "decoration-blocks.research.description".T(),
            hidden: false,
            category);

        ResearchSideUpgrade upgrade = SideUpgrade.New()
           .WithPresentationData(presentation)
           .WithCost(new IResearchCost[] { new ResearchCostPoints(new ResearchPointCurrency(Cost)) })
           // No prerequisites. Decoration is not progression: a player who wants to make their
           // factory look like something should not have to unlock a machine first.
           .WithCustomRequirements(Array.Empty<ResearchMechanicId>(), Array.Empty<ResearchUpgradeId>())
           .Build(scenarioId, progression);

        Log?.Info?.Log(
            "Decoration blocks: research node '" + UpgradeId.Id + "' added to category '"
            + category + "' for " + Cost * 100 + " points.");

        return upgrade;
    }

    /// The wrong tab beats no tab. ResearchProgression checks every shop item's category
    /// against UpgradeCategories and logs an error for one it does not know, and an
    /// unrecognised category has nowhere to render - so the node would be unreachable rather
    /// than merely misplaced.
    private string ChooseCategory(ResearchProgression progression)
    {
        foreach (string category in progression.SideUpgradeCategories)
        {
            if (category.IndexOf(PreferredCategoryMarker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return category;
            }
        }

        return progression.SideUpgradeCategories.FirstOrDefault() ?? string.Empty;
    }

    /// A shop entry's preview image is not optional. HUDResearchSideUpgradeDisplay.RebuildView
    /// calls GameData.GetImage(upgrade.ImageId) unconditionally, and GetImage throws on an id
    /// it cannot resolve - GameImageId.Empty included. The throw propagates out of the whole
    /// HUDResearchTree construction, leaving the research screen half-built and unclosable, so
    /// an empty image id is not a cosmetic shortcut but a broken game.
    ///
    /// Image ids live in Unity assets, so borrowing one off a node the game already renders is
    /// the only way to be certain it resolves.
    private static GameImageId BorrowImage(ResearchProgression progression)
    {
        foreach (ResearchSideUpgrade upgrade in progression.SideUpgrades)
        {
            if (upgrade.ImageId.HasValue)
            {
                return upgrade.ImageId;
            }
        }

        return GameImageId.Empty;
    }
}
