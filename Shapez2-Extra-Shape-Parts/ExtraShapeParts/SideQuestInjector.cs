using System;
using System.Collections.Generic;
using System.Linq;
using Core.Localization;
using Game.Core.Research;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// Adds <see cref="SideQuestCatalog"/>'s chains to a scenario's research progression.
    ///
    /// `ResearchSideQuestGroup` has a public constructor taking a title and a list of serialized
    /// quests, and it builds the chain itself - it folds each quest's id into the dependency set for
    /// the next, so step 2 is reachable only once step 1 is done. That is the whole progression
    /// behaviour, for free, and it is why none of this needs scenario JSON.
    ///
    /// What it does need is list surgery. `ResearchProgression` keeps its quests in four places, and
    /// appending to one is the kind of half-working that does not announce itself: the tab renders
    /// from `_SideQuestGroups`, so the quests would *appear* and then fail to resolve by id.
    public static class SideQuestInjector
    {
        /// Registers every chain that is valid for this scenario's shape configuration.
        ///
        /// Returns how many quests were added, for the log line.
        public static int Inject(GameScenario scenario, IGameData gameData, ILogger logger)
        {
            ResearchProgression progression = scenario.Progression;
            ResearchConfig config = scenario.ResearchConfig;

            ShapesConfiguration shapes = gameData.GetShapesConfiguration(config.ShapesConfigurationId);
            ShapeColorScheme colors = gameData.GetColorScheme(config.ColorSchemeConfigurationId);

            if (shapes == null || colors == null)
            {
                logger.Warning?.Log(
                    $"Side quests skipped for '{scenario.UniqueId}': no shape configuration " +
                    $"('{config.ShapesConfigurationId}') or colour scheme " +
                    $"('{config.ColorSchemeConfigurationId}').");
                return 0;
            }

            // The game's own parser, over this scenario's own parts and colours, so a shape that
            // validates here is a shape the session can resolve. The id manager is a throwaway -
            // `UniversalShapeRenderer` builds one the same way for the same reason.
            StrictShapeDefinitionFactory validator = new StrictShapeDefinitionFactory(
                shapes.PartCount, shapes.Parts, colors.Colors, new ShapeHashParser(), new ShapeIdManager());

            // The configuration's own common parts, which is what a chain means when it asks for
            // "a vanilla shape". This mod adds nothing common, so these are vanilla's.
            List<char> commonParts = shapes.MapGenerationCommonParts.Select(part => part.Code).ToList();

            int added = 0;
            foreach (SideQuestChain chain in SideQuestCatalog.All)
            {
                added += AddChain(progression, chain, shapes.PartCount, config.MaxShapeLayers,
                    commonParts, validator, logger);
            }

            return added;
        }

        private static int AddChain(ResearchProgression progression, SideQuestChain chain,
            int partCount, int maxShapeLayers, IReadOnlyList<char> commonParts,
            IShapeDefinitionFactory validator, ILogger logger)
        {
            // A scenario with a lower layer cap gets a shorter chain rather than no chain: the
            // steps are ordered, so dropping the tallest ones off the end leaves a coherent
            // progression. A step that fails for any *other* reason drops the whole chain, because
            // a hole in the middle is not a progression at all.
            List<SideQuestStep> steps = chain.Steps
                .Where(step => step.Layers.Length <= maxShapeLayers)
                .ToList();

            if (steps.Count < chain.Steps.Length)
            {
                logger.Info?.Log(
                    $"Side quest chain '{chain.Title}' trimmed to {steps.Count} of " +
                    $"{chain.Steps.Length} steps: this scenario caps shapes at {maxShapeLayers} layers.");
            }

            if (steps.Count == 0)
            {
                return 0;
            }

            List<SerializedResearchSideQuest> serialized = new List<SerializedResearchSideQuest>();
            for (int index = 0; index < steps.Count; index++)
            {
                SideQuestStep step = steps[index];

                if (!step.Fits(partCount))
                {
                    logger.Warning?.Log(
                        $"Side quest chain '{chain.Title}' skipped: step '{step.Title}' does not " +
                        $"tile {partCount} parts.");
                    return 0;
                }

                string code = step.ShapeCode(partCount, commonParts);
                if (!validator.TryCreateShapeDefinition(code, out _))
                {
                    // Almost always a part or a colour this scenario does not have. Naming the code
                    // is what makes that diagnosable without a debugger.
                    logger.Warning?.Log(
                        $"Side quest chain '{chain.Title}' skipped: step '{step.Title}' produced " +
                        $"'{code}', which this scenario cannot parse.");
                    return 0;
                }

                serialized.Add(new SerializedResearchSideQuest
                {
                    Id = QuestId(chain, index),
                    IsFollowupForLevel = false,
                    Costs = new[]
                    {
                        new SerializedResearchCostShapes { Shape = code, Amount = step.ShapeAmount },
                    },
                    // A quest with no rewards is deleted: `RemoveUpgradesWithZeroRewards` prunes
                    // any non-level upgrade whose reward list is empty, and it runs from the
                    // constructor. Two rewards, matching what vanilla's own chains pay.
                    Rewards = new ISerializedResearchReward[]
                    {
                        new SerializedResearchRewardResearchPoints { Amount = Reward(SideQuestCatalog.ResearchPointsPerStep, index) },
                        new SerializedResearchRewardChunkLimit { Amount = Reward(SideQuestCatalog.ChunkLimitPerStep, index) },
                    },
                });
            }

            if (serialized.Any(quest => progression.TryGetUpgrade(new ResearchUpgradeId(quest.Id), out _)))
            {
                // Either this scenario has been built twice in one process, or somebody else owns
                // the id. Both are worth a line; neither is worth adding a duplicate, which
                // `ResearchProgression`'s own constructor would have thrown on.
                logger.Warning?.Log(
                    $"Side quest chain '{chain.Title}' skipped: its ids are already registered.");
                return 0;
            }

            ResearchSideQuestGroup group = new ResearchSideQuestGroup(
                new RawText(chain.Title),
                // No gating. Vanilla groups require an authored upgrade id such as
                // `CBFluids_Extraction`, which is scenario data a mod cannot count on existing -
                // and a required id that is not defined is exactly what `ResearchProgression.Validate`
                // refuses to build. Visible from the start is the honest option.
                Array.Empty<ResearchUpgradeId>(),
                Array.Empty<ResearchMechanicId>(),
                serialized);

            Register(progression, group);

            logger.Info?.Log(
                $"Side quest chain '{chain.Title}': {group.SideQuests.Count} quests, " +
                $"{partCount} parts, first shape '{serialized[0].Costs[0].Shape}'.");

            return group.SideQuests.Count;
        }

        /// The four collections a quest has to land in.
        ///
        /// `_SideQuestGroups` renders the tab, `_SideQuests` is what the research manager iterates,
        /// `_AllUpgrades` is what unlock resolution walks and `_UpgradesById` is what
        /// `GetUpgrade`/`TryGetUpgrade` read - and a quest missing from the last one is removed from
        /// the others the next time anything calls `TryRemoveUpgrade`.
        ///
        /// There is a fifth field, `_SideQuestsIncludingHidden`, and it is **dead** - declared,
        /// never read, never assigned anywhere in the assembly. Writing to it would be cargo cult.
        private static void Register(ResearchProgression progression, ResearchSideQuestGroup group)
        {
            progression._SideQuestGroups.Add(group);

            foreach (ResearchSideQuest quest in group.SideQuests)
            {
                progression._SideQuests.Add(quest);
                progression._AllUpgrades.Add(quest);
                progression._UpgradesById[quest.Id] = quest;
            }
        }

        /// Quest ids are save state - `ResearchUpgradeId` is what completion is recorded against -
        /// so they carry a mod prefix and are never derived from anything that could reorder.
        private static string QuestId(SideQuestChain chain, int index)
        {
            return $"esp.{chain.Id}.{index + 1}";
        }

        /// A chain longer than the reward table pays the last entry for every further step.
        private static int Reward(int[] table, int index)
        {
            return table[Math.Min(index, table.Length - 1)];
        }
    }
}
