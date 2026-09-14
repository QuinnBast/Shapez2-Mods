using System.Collections.Generic;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// The seven side quest chains, from JOBS-DRAFT.md.
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

        private static IReadOnlyList<SideQuestChain> Built;

        /// Built on first access, for the same reason <see cref="ExtraShapePartCatalog.All"/> is: a
        /// static field initialiser here runs before anything declared below it.
        public static IReadOnlyList<SideQuestChain> All => Built ??= new[]
        {
            // The cleanest progression of the set: one dome line per step, one new colour each
            // time, nothing else changes. First for that reason as much as for how it looks.
            new SideQuestChain("rainbow-vortex", "Rainbow Vortex",
                new SideQuestStep("Red dome", 250, "Mr"),
                new SideQuestStep("Sunrise", 1000, "Mr", "My"),
                new SideQuestStep("Three deep", 4000, "Mr", "My", "Mg"),
                new SideQuestStep("Vortex", 8000, "Mr", "My", "Mg", "Mb")),

            // The pin is last because pinning shifts everything up: ShapeOperationPushPin discards
            // anything at MaxShapeLayers - 1 or above, so it only works on a shape that is within
            // one layer of the cap.
            new SideQuestChain("turn-the-wheel", "Turn the Wheel",
                new SideQuestStep("Gear", 250, "Eu"),
                new SideQuestStep("Two deep", 1000, "Eu", "Ey"),
                new SideQuestStep("Gear tower", 4000, "Eu", "Ey", "Er"),
                new SideQuestStep("Pinned tower", 8000, "P-", "Eu", "Ey", "Er")),

            new SideQuestChain("both-ways", "Both Ways",
                new SideQuestStep("Red wedge", 250, "Tr"),
                new SideQuestStep("Wedge on dome", 1000, "Tr", "Mw"),
                new SideQuestStep("Three deep", 4000, "Tr", "Mw", "Tb"),
                new SideQuestStep("Contra", 8000, "Tr", "Mw", "Tb", "Mw")),

            // The crystal step is the point of this chain: step 2 deliberately leaves gaps so step 3
            // can fill them. ShapeOperationCrystallize replaces every empty part and every pin, so
            // the gaps have to be built first.
            new SideQuestChain("in-bloom", "In Bloom",
                new SideQuestStep("Flower", 250, "Bm"),
                new SideQuestStep("Half a rose", 1000, "Bm", "Br--"),
                new SideQuestStep("Crystal rose", 4000, "Bm", "Brcr"),
                new SideQuestStep("Crowned rose", 8000, "Bm", "Brcr", "By")),

            // Three steps, not four - a fourth was a square plate on top, which added a whole
            // vanilla line for a step that did not earn it.
            new SideQuestChain("sharpen", "Sharpen",
                new SideQuestStep("Sawblade", 250, "Zu"),
                new SideQuestStep("Buzzsaw", 1000, "Zu", "Or"),
                new SideQuestStep("Twin saw", 4000, "Zu", "Or", "Zw")),

            // The last step is the only one that *changes* a layer rather than adding one: the top
            // goes from dots to dots and bars alternating, which needs a half cutter and a
            // recombine. Everything below it is untouched.
            new SideQuestChain("fine-detail", "Fine Detail",
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
            new SideQuestChain("foundations", "Foundations",
                new SideQuestStep("Inlay", 500, "2uDu"),
                new SideQuestStep("Porthole", 2000, "2uDu", "1w"),
                new SideQuestStep("Cog plate", 6000, "2uDu", "1w", "Ey")),
        };
    }
}
