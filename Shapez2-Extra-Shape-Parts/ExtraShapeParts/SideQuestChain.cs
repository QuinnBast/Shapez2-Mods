using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// A titled chain of side quests, described independently of how many parts a shape has.
    ///
    /// A chain cannot carry finished shape codes, because a shape code is only valid for one
    /// `PartCount`: `ShapeHashParser` reads a layer as pairs and `StrictShapeDefinitionFactory`
    /// rejects any hash whose first layer is not exactly `PartCount` pairs long. So a quest written
    /// as `MrMrMrMr` is a quad quest and is *invalid* in hexagonal mode rather than merely ugly.
    ///
    /// Each layer is therefore stored as a **pattern** - a short fragment of a shape code that is
    /// repeated until it fills the layer. `"Mr"` fills any part count; `"RuDu"` alternates and fills
    /// any even one. That is the same shape vanilla's own side tasks take (`SG_Fluids_1` costs
    /// `Rb--Rb--:Cu--Cu--`), so it covers everything the draft needed and nothing more.
    public sealed class SideQuestChain
    {
        /// Slug, used to build quest ids. Ids become save state - `ResearchUpgradeId` is what
        /// completion is recorded against - so they carry a mod prefix and must never be reused for
        /// a different quest.
        public readonly string Id;

        public readonly string Title;

        public readonly SideQuestStep[] Steps;

        /// The upgrade id that has to be unlocked before the chain appears, or null for none.
        ///
        /// One gate for the whole chain, because that is all the game offers:
        /// `SerializedResearchSideQuest` has no requirements field of its own, and
        /// `ResearchSideQuestGroup` derives each quest's from the group's plus the quests before it.
        /// So the gate has to cover what the chain needs *anywhere*, not what its first step needs -
        /// otherwise a player reaches the last step and finds it unbuildable.
        ///
        /// It is resolved against the scenario at injection time and dropped if that scenario does
        /// not define it; see <see cref="SideQuestInjector"/>. Nothing here can be assumed to exist,
        /// because `ResearchProgression.Validate` has already run by then and a dangling requirement
        /// would not throw - it would simply never unlock.
        /// Candidates, in order; the first the scenario defines wins.
        ///
        /// More than one because scenarios do not share a late game. `Milestone_PostFinal_Tier1`
        /// exists in the quad scenarios and **not** in the hexagonal one, which stops at
        /// `Milestone_Final` after nine milestones instead of thirteen. A chain gated on the end of
        /// the game therefore has to name the end of *each* game.
        public readonly string[] Gates;

        public SideQuestChain(string id, string title, string[] gates, params SideQuestStep[] steps)
        {
            Id = id;
            Title = title;
            Gates = gates ?? Array.Empty<string>();
            Steps = steps;
        }
    }

    /// One quest: deliver <see cref="ShapeAmount"/> of a shape, for a research point and platform
    /// capacity reward.
    public sealed class SideQuestStep
    {
        /// Layer patterns, **bottom layer first** - the order a shape code is written in, and the
        /// order `ShapeHashParser` reads. A pin step is `P-` at the bottom, because
        /// `ShapeOperationPushPin` puts pins at layer 0 and lifts everything else up.
        public readonly string[] Layers;

        public readonly string Title;

        public readonly int ShapeAmount;

        public SideQuestStep(string title, int shapeAmount, params string[] layers)
        {
            Title = title;
            ShapeAmount = shapeAmount;
            Layers = layers;
        }

        /// Whether every layer pattern tiles a shape of <paramref name="partCount"/> parts exactly.
        ///
        /// A pattern that does not divide the part count would run off the end mid-repeat - `RuDuCu`
        /// in a four-part shape would be `RuDuCuRu`, which parses and is not the shape anyone wrote.
        /// Better to drop the chain and say so.
        public bool Fits(int partCount)
        {
            return Layers.All(layer => layer.Length >= 2
                                       && layer.Length % 2 == 0
                                       && partCount % (layer.Length / 2) == 0);
        }

        /// `1` and `2` in a pattern mean "this configuration's first and second common part",
        /// resolved against <paramref name="commonParts"/> rather than written down.
        ///
        /// A chain that wants a vanilla shape underneath cannot name one: `R` and `C` are the quad
        /// configuration's square and circle and **do not exist in the hexagonal one**, whose
        /// common part is `H`. Hardcoding them is what made the Foundations chain drop out of a
        /// hexagonal save entirely, with `RuDuRuDuRuDu` failing to parse. Digits are safe as
        /// placeholders because a digit is not a shape code anywhere in the base game.
        public const char FirstCommonPart = '1';

        public const char SecondCommonPart = '2';

        /// The shape code for a given part count.
        public string ShapeCode(int partCount, IReadOnlyList<char> commonParts)
        {
            if (!Fits(partCount))
            {
                throw new InvalidOperationException(
                    $"Side quest step '{Title}' does not tile {partCount} parts.");
            }

            return string.Join(":", Layers.Select(layer => Fill(layer, partCount, commonParts)));
        }

        private static string Fill(string pattern, int partCount, IReadOnlyList<char> commonParts)
        {
            int slots = pattern.Length / 2;
            StringBuilder layer = new StringBuilder(partCount * 2);

            for (int part = 0; part < partCount; part++)
            {
                int slot = part % slots;
                layer.Append(Resolve(pattern[slot * 2], commonParts));
                layer.Append(pattern[slot * 2 + 1]);
            }

            return layer.ToString();
        }

        /// A configuration with only one common part - the hexagonal one has only `CubeHex` - gets
        /// that one for both placeholders rather than nothing.
        private static char Resolve(char code, IReadOnlyList<char> commonParts)
        {
            if (commonParts == null || commonParts.Count == 0)
            {
                return code;
            }

            if (code == FirstCommonPart)
            {
                return commonParts[0];
            }

            if (code == SecondCommonPart)
            {
                return commonParts[Math.Min(1, commonParts.Count - 1)];
            }

            return code;
        }
    }
}
