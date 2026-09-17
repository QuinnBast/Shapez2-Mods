namespace QuinnBast.Shapez2.DecorationBlocks;

/// Which toolbar tab a block appears under.
///
/// Almost every block is decoration, but the block of redstone is also a permanent power source,
/// so it belongs with the components that use it rather than filed between the ore blocks it
/// looks like. Carried on the definition rather than special-cased in the registrar: the catalog
/// is where a block's facts live.
public enum BlockTab
{
    Decorations,
    Redstone,
}

/// One decorative block: an id that ends up in save files, the texture on each of its three
/// distinct faces, and whether it needs the translucent material.
///
/// Three faces, not six. Every block Minecraft ships is either uniform or top/side/bottom
/// symmetric, and carrying six would put four identical strings in every entry of the catalog.
/// A block that genuinely needs per-side faces can be added later by giving the cube builder a
/// six-element overload; nothing else depends on the count.
internal sealed class BlockDefinition
{
    public BlockDefinition(
        string id, string displayName, string sideTexture,
        string topTexture = null, string bottomTexture = null, bool translucent = false,
        BlockTab tab = BlockTab.Decorations)
    {
        Tab = tab;
        Id = id;
        DisplayName = displayName;
        SideTexture = sideTexture;
        TopTexture = topTexture ?? sideTexture;
        BottomTexture = bottomTexture ?? TopTexture;
        Translucent = translucent;
    }

    /// Goes into the BuildingDefinitionId and the BuildingDefinitionGroupId, and therefore into
    /// every save that places one. Renaming one orphans placed blocks in existing saves.
    public string Id { get; }

    /// The English name, written into translations.json by the build rather than looked up, so
    /// that adding a block does not mean editing two files in step.
    public string DisplayName { get; }

    /// File names under Resources/Textures, without the .png. Several blocks share a texture -
    /// a bookshelf's top is oak planks - and the atlas packs each distinct file once.
    public string SideTexture { get; }

    public string TopTexture { get; }

    public string BottomTexture { get; }

    /// Routed to a material cloned from BuildingsGlassMaterial instead of BuildingMaterial, so
    /// that a texture with alpha in it is actually blended. The opaque building shader writes
    /// depth and ignores alpha, which would render glass as a solid grey cube.
    public bool Translucent { get; }

    public BlockTab Tab { get; }

    /// The face a toolbar icon is cut from. The side is what a player sees when the block is
    /// standing on a platform, so it is what makes the entry recognisable; a grass block
    /// identified by its top is a green square and nothing else.
    public string IconTexture => SideTexture;

    public string TranslationKey => "decoration-blocks.block." + Id;
}
