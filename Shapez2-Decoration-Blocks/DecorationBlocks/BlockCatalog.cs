using System.Collections.Generic;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// The blocks the mod ships, in toolbar order.
///
/// Ordered by what a player reaches for, not alphabetically: building materials first, then
/// ground, then the ore blocks that are really colour swatches, then the oddities. The toolbar
/// renders them in this order and so does the research reward list.
///
/// Texture names are the vanilla resource-pack file names, so a player who wants a different
/// look can drop their own pack's PNGs into Resources/Textures under the same names and the
/// mod picks them up with no code change. That is the whole reason the names are not an enum.
internal static class BlockCatalog
{
    public static IReadOnlyList<BlockDefinition> All { get; } = new[]
    {
        // Building materials.
        new BlockDefinition("Stone", "Stone", "stone"),
        new BlockDefinition("Cobblestone", "Cobblestone", "cobblestone"),
        new BlockDefinition("StoneBricks", "Stone Bricks", "stone_bricks"),
        new BlockDefinition("Bricks", "Bricks", "bricks"),
        new BlockDefinition("OakPlanks", "Oak Planks", "oak_planks"),
        new BlockDefinition("OakLog", "Oak Log", "oak_log", topTexture: "oak_log_top"),

        // Ground.
        new BlockDefinition("Dirt", "Dirt", "dirt"),
        new BlockDefinition("GrassBlock", "Grass Block", "grass_block_side",
            topTexture: "grass_block_top", bottomTexture: "dirt"),
        new BlockDefinition("Sand", "Sand", "sand"),
        new BlockDefinition("Gravel", "Gravel", "gravel"),
        new BlockDefinition("Snow", "Snow Block", "snow"),

        // Colour swatches that happen to be ore blocks. These are what a decorator actually
        // uses to pick out a shape against grey stone.
        new BlockDefinition("GoldBlock", "Block of Gold", "gold_block"),
        new BlockDefinition("IronBlock", "Block of Iron", "iron_block"),
        new BlockDefinition("DiamondBlock", "Block of Diamond", "diamond_block"),
        new BlockDefinition("EmeraldBlock", "Block of Emerald", "emerald_block"),
        new BlockDefinition("LapisBlock", "Block of Lapis Lazuli", "lapis_block"),

        // Not filed with the ore blocks it resembles: it is a permanent power source, so it sits
        // in the Redstone tab beside the components that consume it.
        new BlockDefinition("SparkstoneBlock", "Block of Sparkstone", "redstone_block",
            tab: BlockTab.Redstone),

        // Oddities.
        new BlockDefinition("Obsidian", "Obsidian", "obsidian"),
        new BlockDefinition("Neitherrack", "Neitherrack", "netherrack"),
        new BlockDefinition("Bookshelf", "Bookshelf", "bookshelf", topTexture: "oak_planks"),

        // Alpha, so it goes through the translucent material. Kept last because it is the one
        // most likely to need its own look fixing after the first play session.
        new BlockDefinition("Glass", "Glass", "glass", translucent: true),
    };

    /// Every distinct texture file the catalog names, which is what the atlas has to pack.
    /// Blocks share files freely - three of them use oak_planks or dirt on some face - so this
    /// is meaningfully shorter than three times the block count.
    public static IReadOnlyList<string> DistinctTextures()
    {
        List<string> names = new List<string>();
        HashSet<string> seen = new HashSet<string>();

        foreach (BlockDefinition block in All)
        {
            Add(block.SideTexture);
            Add(block.TopTexture);
            Add(block.BottomTexture);
        }

        return names;

        void Add(string name)
        {
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }
    }
}
