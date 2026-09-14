using System;
using System.Collections.Generic;
using System.Linq;
using Core.Localization;
using Game.Core.Research;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// One research node shared by many islands.
    ///
    /// The obvious call is `UnlockedWithNewSideUpgrade`, and it is wrong here. Its extender is
    /// registered *per island group* - `UnlockIslandWithNewSideUpgradeResearchProgressionExtender`
    /// calls `SideUpgradeBuilder.Build` once per group - and `Build` appends to
    /// `_SideUpgrades`, `_ShopItems` and `_AllUpgrades` every time. Nine islands sharing one
    /// builder would therefore put nine identical nodes in the shop. `CustomSideUpgradeSelector`
    /// is no better: its `Select` is a call to `Build`, so it duplicates too.
    ///
    /// So this is a get-or-create selector. The first island to be extended in a given scenario
    /// builds the node; every island after it finds the built one by id and
    /// `UnlockIslandWithExistingSideUpgradeResearchProgressionExtender` appends its group to the
    /// node's rewards. Order does not matter and nothing is registered twice.
    ///
    /// Keying on the progression rather than on a field is deliberate: `ExtendResearch` runs once
    /// per scenario load, each with its own `ResearchProgression`, and a cached upgrade object
    /// from a previous session would be appended to a progression that never contained it.
    internal sealed class CargoUnlock : ISideUpgradeSelector
    {
        /// What a train station island group is actually called. Nothing vanilla is named
        /// "TrainStation" except the *spacers* - `AuthoringIslands` has
        /// `TrainStationStraightSpacerShapeGroup` and three more like it, while the stations
        /// themselves are `TrainShapeLoadersGroup`, `TrainFluidUnloadersGroup` and so on. An
        /// earlier version matched on "TrainStation" and duly anchored the cargo nodes to
        /// `CBTrains_StationSpacer`, gating them behind cosmetic filler.
        ///
        /// "Loader" covers unloaders too, being a substring of the word. A loader is the right
        /// anchor in any case: it is the thing a cargo belt exists to feed.
        private const string AnchorGroupMarker = "Train";

        private const string AnchorGroupRole = "Loader";

        /// The spacers, kept only as a last resort. They are the wrong gate, but they are at
        /// least a train gate, and they proved at runtime that group ids do mirror the
        /// `AuthoringIslands` field names - which is the only evidence there is for that.
        private const string AnchorGroupFallbackMarker = "TrainStation";

        private readonly ResearchUpgradeId UpgradeId;

        private readonly string TitleId;

        private readonly string DescriptionId;

        private readonly int Cost;

        /// The mod's own picture of the machines this node unlocks, served by CargoImages, or
        /// null when that detour did not install - in which case the anchor's picture is
        /// borrowed as before. A node whose image id will not resolve takes the research screen
        /// down with it, so this is never set hopefully.
        private readonly string ImageId;

        /// A mod node this one sits behind, or null to sit behind the vanilla train station
        /// unlock. Stores hang off machines: a store with nothing to store is not a first step.
        private readonly ResearchUpgradeId? Prerequisite;

        private readonly ILogger Log;

        /// Run once per scenario, when this node is first built into it. Anything else that has
        /// to be added to a scenario's research and has no extender of its own rides here - the
        /// wiki references do. It is idempotent on its own account, since either node may be the
        /// one that happens to be built first.
        private readonly Action<ResearchProgression> OnScenario;

        public CargoUnlock(
            string upgradeId, string titleId, string descriptionId, int cost,
            ResearchUpgradeId? prerequisite, ILogger log, Action<ResearchProgression> onScenario,
            string imageId)
        {
            UpgradeId = new ResearchUpgradeId(upgradeId);
            ImageId = imageId;
            TitleId = titleId;
            DescriptionId = descriptionId;
            Cost = cost;
            Prerequisite = prerequisite;
            Log = log;
            OnScenario = onScenario;
        }

        public ResearchUpgradeId Id => UpgradeId;

        public ResearchSideUpgrade Select(ScenarioId scenarioId, ResearchProgression progression)
        {
            // `as`, not a cast: `_UpgradesById` holds every kind of upgrade, and a milestone
            // level answers this lookup as readily as a side upgrade does.
            if (progression.TryGetUpgrade(UpgradeId, out IResearchUpgrade existing)
                && existing is ResearchSideUpgrade built)
            {
                return built;
            }

            return Create(scenarioId, progression);
        }

        private ResearchSideUpgrade Create(ScenarioId scenarioId, ResearchProgression progression)
        {
            IResearchUpgrade anchor = FindTrainStationUnlock(progression);

            // Category and preview image are both authored data - they live in scenario JSON and
            // Unity assets, not in anything readable at build time - and both differ between
            // scenarios. Copying them off the node that already unlocks train stations is the
            // only way to be certain they resolve in whatever scenario this is, and it puts the
            // cargo nodes in the same part of the tree a player already associates with trains.
            //
            // Only a side upgrade carries a category. If train stations turn out to be handed
            // out by a milestone level instead, the level still serves as a prerequisite and an
            // image, and only the tab has to be guessed.
            string category = (anchor as ResearchSideUpgrade)?.Category ?? FallbackCategory(progression);
            GameImageId image = ImageId != null
                ? new GameImageId(ImageId)
                : anchor != null && anchor.ImageId.HasValue
                    ? anchor.ImageId
                    : BorrowImage(progression);

            List<ResearchUpgradeId> required = new List<ResearchUpgradeId>();
            if (Prerequisite.HasValue)
            {
                required.Add(Prerequisite.Value);
            }
            else if (anchor != null)
            {
                required.Add(anchor.Id);
            }

            SideUpgradePresentationData presentation = new SideUpgradePresentationData(
                UpgradeId, image, GameVideoId.Empty, TitleId.T(), DescriptionId.T(),
                hidden: false, category);

            ResearchSideUpgrade upgrade = SideUpgrade.New()
               .WithPresentationData(presentation)
               .WithCost(new IResearchCost[] { new ResearchCostPoints(new ResearchPointCurrency(Cost)) })
               .WithCustomRequirements(Array.Empty<ResearchMechanicId>(), required)
               .Build(scenarioId, progression);

            // Named in full because the category, the image and the prerequisite are all
            // inherited from the anchor: if the nodes land in the wrong tab, this line says
            // which vanilla node they copied.
            Log.Info?.Log(
                $"Research: added '{UpgradeId.Id}' to category '{category}' for {Cost * 100} points, "
                + $"behind {(required.Count == 0 ? "nothing" : required[0].Id)}, "
                + $"anchored on {(anchor == null ? "nothing" : anchor.Id.Id)}.");

            OnScenario?.Invoke(progression);

            return upgrade;
        }

        /// The node that rewards a train station island group. Searched by reward rather than by
        /// id or title, because a reward is structural: whatever the scenario calls its nodes,
        /// the one that hands out the train station group is the one a player reaches before
        /// cargo tools are of any use.
        ///
        /// `AllUpgrades` rather than `SideUpgrades` because a milestone level is an
        /// `IResearchUpgrade` too and hands out island groups the same way, so which of the two
        /// unlocks train stations is an authoring decision this does not need to know. A side
        /// upgrade is still preferred when both match, since only a side upgrade carries the
        /// category the cargo nodes want to sit in.
        private IResearchUpgrade FindTrainStationUnlock(ResearchProgression progression)
        {
            IResearchUpgrade best = null;
            int bestScore = 0;

            foreach (IResearchUpgrade upgrade in progression.AllUpgrades)
            {
                foreach (IResearchReward reward in upgrade.Rewards)
                {
                    if (!(reward is ResearchRewardIslandGroup islandGroup))
                    {
                        continue;
                    }

                    string name = islandGroup.GroupId.Name ?? string.Empty;

                    int score;
                    if (Contains(name, AnchorGroupMarker) && Contains(name, AnchorGroupRole))
                    {
                        score = 4;
                    }
                    else if (Contains(name, AnchorGroupFallbackMarker))
                    {
                        score = 1;
                    }
                    else
                    {
                        continue;
                    }

                    // A side upgrade breaks the tie, being the only kind that carries a category.
                    score += upgrade is ResearchSideUpgrade ? 1 : 0;

                    if (score > bestScore)
                    {
                        best = upgrade;
                        bestScore = score;
                    }
                }
            }

            if (best == null)
            {
                Log.Error?.Log(
                    "Research: nothing in this scenario rewards a train station island group, so "
                    + "the cargo nodes cannot copy a category or a preview image from one. They "
                    + "fall back to the first category in the scenario and a borrowed image, "
                    + "which puts them in the wrong tab but keeps them buyable.");
            }

            return best;
        }

        private static bool Contains(string name, string marker)
        {
            return name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// The wrong tab beats no tab. A category string the progression does not know is logged
        /// as an error by the game itself (`ResearchProgression` checks every shop item against
        /// `UpgradeCategories`) and has nowhere to render, so an unrecognised one would make the
        /// node unreachable rather than merely misplaced.
        private static string FallbackCategory(ResearchProgression progression)
        {
            return progression.SideUpgradeCategories.FirstOrDefault() ?? string.Empty;
        }

        /// A shop entry's preview image is not optional. `HUDResearchSideUpgradeDisplay.RebuildView`
        /// calls `GameData.GetImage(upgrade.ImageId)` unconditionally and `GetImage` throws on an
        /// id it cannot resolve - including the empty one - and that throw propagates out of the
        /// whole `HUDResearchTree` construction, leaving the research screen half-built and
        /// unclosable. Borrowing an id off a node the game already renders is the only way to be
        /// sure it resolves, since image ids live in Unity assets.
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

    /// The two nodes, built once and shared by every island that belongs to them.
    internal sealed class CargoResearch
    {
        /// Also named in the wiki references, which gate each entry on the node that unlocks
        /// the machine it describes - see CargoWiki.
        public const string MachinesUpgradeId = "CargoTools_CargoMachines";

        public const string StoresUpgradeId = "CargoTools_CargoStores";

        public CargoResearch(
            ILogger log, Action<ResearchProgression> onScenario, bool ownImages)
        {
            Machines = new CargoUnlock(
                MachinesUpgradeId,
                "cargo-tools.research.cargo-machines.title",
                "cargo-tools.research.cargo-machines.description",
                MachinesCost, prerequisite: null, log, onScenario,
                ownImages ? CargoImages.MachinesImage : null);

            Stores = new CargoUnlock(
                StoresUpgradeId,
                "cargo-tools.research.cargo-stores.title",
                "cargo-tools.research.cargo-stores.description",
                StoresCost, prerequisite: Machines.Id, log, onScenario,
                ownImages ? CargoImages.StoresImage : null);
        }

        /// Belts, corners and the four packagers - everything needed to move cargo at all.
        public CargoUnlock Machines { get; }

        /// Buffering, which is worth having only once cargo can be moved.
        public CargoUnlock Stores { get; }

        // 4.8k as the research screen shows it. `ResearchPointCurrency` is not what it displays:
        // `StringFormattingExtensions.Format` renders `Amount * 100`, so the number here is
        // hundreds of points and 48 reads as "4.8k". Passing 4800 would price these at 480k.
        private const int MachinesCost = 48;

        private const int StoresCost = 48;
    }
}
