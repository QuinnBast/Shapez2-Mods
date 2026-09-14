using System.Collections.Generic;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// The ten side quest chains. The first seven are from JOBS-DRAFT.md; the last three came
    /// later, from reading vanilla's own quests out of `debug.export-game-data`.
    ///
    /// One rule shaped all of them: **every step adds one thing to the factory that made the step
    /// before it.** No step asks for a shape that needs a different production line from scratch.
    ///
    /// Each layer is a pattern repeated to fill the shape, so a chain is the same design in a quad
    /// save and a hexagonal one - `"Mr"` is four red domes or six of them depending on where it is
    /// read. See <see cref="SideQuestStep"/> for why a finished shape code could not be used.
    ///
    /// **Shape amounts come from the draft; reward amounts come from vanilla.** `SG_Fluids_1` in
    /// `Scenarios/Classic/Regular/SideTasks` is a five step chain paying 1, 2, 2, 4 and 12 research
    /// points and 30 to 60 chunk limit, for 1200 to 2500 shapes a step. Ours pay 1, 2, 4, 8 points
    /// and 30 to 60 capacity, which sits inside that range rather than beside it - the numbers are
    /// authored data and were read out of `resources.assets`, not invented.
    public static class SideQuestCatalog
    {
        /// Research points per step index, and platform capacity per step index. Vanilla pays for a
        /// step, not for a chain, so a three step chain pays the first three of each.
        public static readonly int[] ResearchPointsPerStep = { 1, 2, 4, 8 };

        public static readonly int[] ChunkLimitPerStep = { 30, 40, 50, 60 };

        /// The gates, and the evidence for each.
        ///
        /// A chain must not appear before a player can build it, and what a shape needs is mostly
        /// its *colours* rather than its machines: the cutter, rotator and stacker all arrive at
        /// `Milestone_Initial`, so gating on those would be a no-op.
        ///
        /// Colour availability was read out of `debug.export-game-data` by asking, for every colour,
        /// the earliest milestone goal and the earliest side quest in which vanilla itself asks for
        /// it. Both agree:
        ///
        ///     u r b   from the start
        ///     g       Milestone_ShapeTrains, and CBFluids_Extraction gates a quest using it
        ///     c y w   Milestone_SpaceFloor3 / CBFluids_Mixer      - the mixer's secondaries
        ///     m       Milestone_Crystals / CBSpecial_SpaceFloor3
        ///     k       Milestone_PostFinal_Tier2 / Milestone_PostFinal_Tier1  - post final, both
        ///
        /// Black being post-final is the one that changed a design: Widow's Web asks for it in its
        /// first step, so the whole chain is end game content rather than the mid game chain it
        /// looks like.
        ///
        /// These are ids, not guarantees. Every one is checked against the scenario before use.
        /// A chain names candidates in order and takes the first the scenario has. A chain whose
        /// gate is missing entirely is **skipped**, not un-gated: a scenario without the mixer
        /// cannot build a white shape either, so showing the chain would only ever frustrate. The
        /// onboarding scenario has none of these, and gets none of these chains.
        public static readonly string[] Mixer = { "CBFluids_Mixer" };

        public static readonly string[] SpaceFloor3 = { "CBSpecial_SpaceFloor3" };

        public static readonly string[] Crystals = { "CBSpecial_Crystals" };

        /// Where black becomes available, which is a different milestone in every family of
        /// scenario and is the only gate here that had to be worked out per scenario:
        ///
        ///     quad        Milestone_PostFinal_Tier1   - post final, and vanilla waits until Tier2
        ///     converter   ConverterMilestoneTier1     - early, and the converter goals lean on it
        ///     hexagonal   Milestone_Final             - never asked for there at all, so the last
        ///                                               milestone is the honest guess
        ///
        /// The hexagonal entry is the weak one: black is in that scenario's colour scheme, because
        /// every scenario shares `DefaultColorSchemeRGBFlex`, but nothing in hexagonal ever asks a
        /// player to make one. Gating on the final milestone is a guess that it is late rather than
        /// impossible. If it turns out to be unobtainable there, the chain should name a different
        /// colour rather than a different gate.
        public static readonly string[] BlackAvailable =
        {
            "Milestone_PostFinal_Tier1", "ConverterMilestoneTier1", "Milestone_Final",
        };

        private static IReadOnlyList<SideQuestChain> Built;

        /// Built on first access, for the same reason <see cref="ExtraShapePartCatalog.All"/> is: a
        /// static field initialiser here runs before anything declared below it.
        public static IReadOnlyList<SideQuestChain> All => Built ??= new[]
        {
            // The cleanest progression of the set: one dome line per step, one new colour each
            // time, nothing else changes. First for that reason as much as for how it looks.
            new SideQuestChain("rainbow-vortex", "Rainbow Vortex", Mixer,   // yellow and green
                new SideQuestStep("Red dome", 250, "Mr"),
                new SideQuestStep("Sunrise", 1000, "Mr", "My"),
                new SideQuestStep("Three deep", 4000, "Mr", "My", "Mg"),
                new SideQuestStep("Vortex", 8000, "Mr", "My", "Mg", "Mb")),

            // The pin is last because pinning shifts everything up: ShapeOperationPushPin discards
            // anything at MaxShapeLayers - 1 or above, so it only works on a shape that is within
            // one layer of the cap.
            new SideQuestChain("turn-the-wheel", "Turn the Wheel", Mixer,   // yellow; the pin pusher is earlier
                new SideQuestStep("Gear", 250, "Eu"),
                new SideQuestStep("Two deep", 1000, "Eu", "Ey"),
                new SideQuestStep("Gear tower", 4000, "Eu", "Ey", "Er"),
                new SideQuestStep("Pinned tower", 8000, "P-", "Eu", "Ey", "Er")),

            new SideQuestChain("both-ways", "Both Ways", Mixer,   // white
                new SideQuestStep("Red wedge", 250, "Tr"),
                new SideQuestStep("Wedge on dome", 1000, "Tr", "Mw"),
                new SideQuestStep("Three deep", 4000, "Tr", "Mw", "Tb"),
                new SideQuestStep("Contra", 8000, "Tr", "Mw", "Tb", "Mw")),

            // The crystal step is the point of this chain: step 2 deliberately leaves gaps so step 3
            // can fill them. ShapeOperationCrystallize replaces every empty part and every pin, so
            // the gaps have to be built first.
            new SideQuestChain("in-bloom", "In Bloom", Crystals,   // the crystal step, later than its magenta
                new SideQuestStep("Flower", 250, "Bm"),
                new SideQuestStep("Half a rose", 1000, "Bm", "Br--"),
                new SideQuestStep("Crystal rose", 4000, "Bm", "Brcr"),
                new SideQuestStep("Crowned rose", 8000, "Bm", "Brcr", "By")),

            // Three steps, not four - a fourth was a square plate on top, which added a whole
            // vanilla line for a step that did not earn it.
            new SideQuestChain("sharpen", "Sharpen", Mixer,   // white, at the last step
                new SideQuestStep("Sawblade", 250, "Zu"),
                new SideQuestStep("Buzzsaw", 1000, "Zu", "Or"),
                new SideQuestStep("Twin saw", 4000, "Zu", "Or", "Zw")),

            // The last step is the only one that *changes* a layer rather than adding one: the top
            // goes from dots to dots and bars alternating, which needs a half cutter and a
            // recombine. Everything below it is untouched.
            new SideQuestChain("fine-detail", "Fine Detail", SpaceFloor3,   // magenta, later than its cyan and yellow
                new SideQuestStep("Bar", 250, "Ic"),
                new SideQuestStep("Circuitry", 1000, "Ic", "Km"),
                new SideQuestStep("Three deep", 4000, "Ic", "Km", "Oy"),
                new SideQuestStep("Interleave", 8000, "Ic", "Km", "OyIy")),

            // The chain that leans on the base shapes. It opens on the one step in the set that
            // needs a half cutter before anything else, and the vanilla disc under the gear reads
            // as a hub.
            //
            // `1` and `2` are placeholders for the configuration's own common parts, not literal
            // codes - see `SideQuestStep.FirstCommonPart`. Written as `Ru` and `Cw` this chain was
            // a quad chain, and it dropped out of hexagonal saves entirely because `R` and `C` do
            // not exist there. Now it is square-and-circle in quad and hexagon in hexagonal, which
            // is what "the vanilla shapes" means in each.
            new SideQuestChain("foundations", "Foundations", Mixer,   // white
                new SideQuestStep("Inlay", 500, "2uDu"),
                new SideQuestStep("Porthole", 2000, "2uDu", "1w"),
                new SideQuestStep("Cog plate", 6000, "2uDu", "1w", "Ey")),

            // The three below came out of reading vanilla's own side quests, exported with
            // `debug.export-game-data`. All three use an idiom the first seven do not: a layer that
            // *changes* rather than grows, which is most of what vanilla's chains actually do.

            // Two toothed discs interleaved, then swapped round - the swap is one rotator on a
            // mixed layer, which nothing else here teaches.
            //
            // Design risk, recorded rather than argued: Gear and Sawblade are both toothed discs,
            // and interleaving them is a subtler difference than any other chain trades on. If the
            // middle two steps read as one shape in game, the fix is to swap Sawblade for Bar and
            // keep the structure.
            new SideQuestChain("millstone", "Millstone", Mixer,   // white, at the last step
                new SideQuestStep("Gear", 250, "Eu"),
                new SideQuestStep("Interleaved", 1000, "EuZu"),
                new SideQuestStep("Swapped", 4000, "EuZu", "ZuEu"),
                new SideQuestStep("Whitewashed", 8000, "EuZu", "ZuEu", "EwZw")),

            // Dome and Wedge are the only chiral parts in the set and they turn opposite ways, so a
            // layer alternating them cannot settle on a direction. Swapping the pair on the layer
            // above reverses it again.
            new SideQuestChain("both-hands", "Both Hands", Mixer,   // white, at the last step
                new SideQuestStep("Dome", 250, "Mr"),
                new SideQuestStep("Opposed", 1000, "MrTb"),
                new SideQuestStep("Mirrored", 4000, "MrTb", "TbMr"),
                new SideQuestStep("Snowblind", 8000, "MrTb", "TbMr", "MwTw")),

            // Black, which nothing else in this mod asks for. `k` is a real colour code - vanilla
            // spends it on `XkXkXkXk` in the quad scenario and never once in the hexagonal one -
            // and both scenarios share `DefaultColorSchemeRGBFlex`, so it resolves in either. In a
            // hexagonal save this is the only black shape a player will be asked for.
            new SideQuestChain("widows-web", "Widow's Web", BlackAvailable,   // black, from its first step
                new SideQuestStep("Black leaf", 250, "Lk"),
                new SideQuestStep("Beaded", 1000, "LkOw"),
                new SideQuestStep("Woven", 4000, "LkOw", "OwLk"),
                new SideQuestStep("Reversed", 8000, "LkOw", "OwLk", "LwOk")),
        };
    }
}
