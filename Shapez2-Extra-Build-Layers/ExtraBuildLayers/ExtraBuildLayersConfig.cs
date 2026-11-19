using System;
using System.IO;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// What the mod does when nobody has told it otherwise.
///
/// Loaded from config.json beside the assembly when present, defaults otherwise. A missing or
/// malformed file is not an error - the defaults are the intended experience and the file only
/// exists for people who want different numbers.
///
/// This config only ever *seeds* <see cref="BuildLayers"/>. A dependent mod sets the
/// properties there from its own constructor and wins, because mod constructors all run before
/// the first scenario is built. That ordering is the whole reason the config is not consulted
/// again later.
///
/// Both counts are "extra on top of whatever the scenario ships" rather than totals, so the
/// mod never needs to know what vanilla's ceiling is - which is what lets it survive a game
/// update that adds a layer of its own, and behave on a modded scenario.
/// </summary>
[Serializable]
public class ExtraBuildLayersConfig
{
    public const string FileName = "config.json";

    /// <summary>
    /// Extra building layers inside every platform. Clamped so the top layer stays at or below
    /// <see cref="BuildLayers.MaxMachineLevel"/>, past which a building's layer would alias onto
    /// the next island layer's ground floor.
    ///
    /// Only layers 0-2 can carry a port onto a space belt or space pipe
    /// (<see cref="BuildLayers.PortConnectedMachineLevels"/>); belts between platforms that
    /// touch work on every floor. Everything else - machines,
    /// belts, lifts, the hub's own input slots - reaches every floor.
    ///
    /// The default is the whole room there is: vanilla's top layer is 2, so 17 more reaches 19.
    /// A scenario that already ships more layers gets clamped rather than refused.
    /// </summary>
    public int ExtraMachineLevels = 17;

    /// <summary>
    /// Extra platform layers in space, clamped so the top one stays at or below
    /// <see cref="BuildLayers.MaxSpaceLevel"/>. The default, 997, reaches that ceiling from
    /// vanilla's top level of 2; the shop sells them in three tiers (SpaceLevelTiers).
    /// </summary>
    public int ExtraSpaceLevels = 997;

    /// <summary>
    /// Which built-in <see cref="ILayerUnlockPolicy"/> to use: <c>Shop</c>, <c>Granted</c> or
    /// <c>None</c>. Anything unrecognised falls back to <c>Shop</c> with a logged error rather
    /// than silently registering nothing.
    ///
    /// A mod that wants something else assigns <see cref="BuildLayers.UnlockPolicy"/> directly
    /// and this value stops mattering.
    /// </summary>
    public string UnlockPolicy = "Shop";

    /// How many layers one shop purchase grants, when no tiers are set. 1 gives the
    /// one-entry-per-layer chain.
    public int LayersPerShopEntry = 100;

    /// <summary>
    /// The machine level each shop purchase ends at: [3, 10, 19] sells level 3, then 4-10, then
    /// 11-19. Levels past the last end go in one final purchase. Empty uses LayersPerShopEntry.
    /// </summary>
    public int[] MachineLevelTiers = { 3, 10, 19 };

    /// <summary>The same for space levels: [25, 250, 999] sells 3-25, 26-250, then 251-999.</summary>
    public int[] SpaceLevelTiers = { 25, 250, 999 };

    /// Raw points for the first shop entry of each kind. The shop renders these times 100.
    public int MachineLevelBaseCost = 2500;

    public int SpaceLevelBaseCost = 4000;

    /// Each shop entry costs this much more than the one before it.
    public float CostGrowthFactor = 1.6f;

    /// <summary>
    /// Shop tab the entries appear under. A category name is a translation key
    /// (<c>research.category.&lt;name&gt;</c>), so a custom one needs its own translations.json
    /// entry.
    /// </summary>
    public string ShopCategory = "ExtraBuildLayers";

    /// <summary>
    /// Seconds a layer key must be held before it starts repeating. Repeating starts at about
    /// eight layers a second and speeds up to one a frame over a couple of seconds. 0 or less
    /// turns repeating off.
    /// </summary>
    public float LayerKeyRepeatDelay = 1f;

    /// <summary>
    /// Selecting a single platform or building on another layer moves the view to its layer.
    /// </summary>
    public bool JumpToSelectedLayer = true;

    /// <summary>
    /// Space layers moved per layer key press in the overview (fully zoomed out). 0 picks a
    /// tenth of the unlocked range - about 100 at 999 layers.
    /// </summary>
    public int OverviewLayerStep = 0;

    /// <summary>
    /// Whether to lengthen <c>LayerColors</c> and <c>BeltArrowColors</c>.
    ///
    /// Turning this off also leaves <c>_G_LayerCount</c> at its authored value, because
    /// <c>GenerateLayerTexture</c> publishes it from <c>LayerColors.Length</c>. Upper floors then
    /// share the top authored colour instead of cycling.
    ///
    /// Exposed because this and <see cref="RefreshLayerShaderParams"/> are the only two things
    /// this mod changes that the building and blueprint shaders read per layer, so switching them
    /// independently is the way to tell which one is responsible for a shading problem on high
    /// floors. Decoration-style buildings that draw with their own material are unaffected by
    /// either, which is what makes them a useful control.
    /// </summary>
    public bool ExtendLayerRamps = true;

    /// <summary>
    /// Whether to republish <c>_G_LayersParams</c> and <c>_G_IslandLayersParams</c> from live
    /// bounds - see <see cref="LayerShaderParamsRefresher"/>.
    ///
    /// Off means the game's own values stand, captured when the session began: correct for layers
    /// unlocked before the session, stale for any bought during it.
    /// </summary>
    public bool RefreshLayerShaderParams = true;

    /// <summary>
    /// Whether to rescale the HUD's layer strip so a tall stack still fits on screen. The
    /// game's own layout is linear in the layer count and runs off the bottom of a 1080p
    /// screen somewhere around thirty layers - see <see cref="CompactLayerStrip"/>.
    /// </summary>
    public bool CompactLayerStrip = true;

    /// Tallest the layer strip may get, in pixels, when <see cref="CompactLayerStrip"/> is on.
    public float LayerStripMaxHeight = 420f;

    /// <summary>
    /// Above this many layers the strip stops drawing one handle per layer and shows the current
    /// layer's number instead (with the top layer under it). A stack of handles stops being
    /// readable long before it stops fitting: at fifty, nine-pixel slots tell you nothing a "37"
    /// would not. 0 never switches. Needs <see cref="CompactLayerStrip"/> on.
    /// </summary>
    public int LayerStripNumberAbove = 8;

    /// <summary>
    /// Whether an existing save made without this mod may be opened with it. The game would
    /// otherwise refuse, because the manifest marks the mod as affecting saves (which it must, so
    /// that *removing* it from a save that uses it is refused). See <see cref="SavegameAdoption"/>.
    /// </summary>
    public bool AllowAddingToExistingSaves = true;

    public ILayerUnlockPolicy CreatePolicy(ILogger logger)
    {
        string requested = UnlockPolicy ?? string.Empty;

        if (requested.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return new NoUnlockPolicy();
        }

        if (requested.Equals("Granted", StringComparison.OrdinalIgnoreCase))
        {
            return new GrantedUnlockPolicy { Category = ShopCategory };
        }

        if (!requested.Equals("Shop", StringComparison.OrdinalIgnoreCase))
        {
            logger.Error?.Log(
                "Extra Build Layers: unknown UnlockPolicy '" + requested + "'; using Shop. " +
                "Valid values are Shop, Granted and None.");
        }

        return new ShopUnlockPolicy
        {
            LayersPerEntry = LayersPerShopEntry,
            MachineTiers = MachineLevelTiers,
            SpaceTiers = SpaceLevelTiers,
            MachineBaseCost = MachineLevelBaseCost,
            SpaceBaseCost = SpaceLevelBaseCost,
            CostGrowthFactor = CostGrowthFactor,
            Category = ShopCategory,
        };
    }

    public static ExtraBuildLayersConfig Load(ILogger logger)
    {
        try
        {
            // ModDirectoryLocator throws on hot reload - a byte-loaded assembly has an empty
            // Location - which is one more reason this whole read sits inside a try.
            string path = ModDirectoryLocator.CreateLocator<ExtraBuildLayersMod>().SubPath(FileName);
            if (!File.Exists(path))
            {
                logger.Info?.Log("Extra Build Layers: no " + FileName + ", using defaults.");
                return new ExtraBuildLayersConfig();
            }

            ExtraBuildLayersConfig config = JsonUtility.FromJson<ExtraBuildLayersConfig>(File.ReadAllText(path));
            if (config == null)
            {
                logger.Error?.Log("Extra Build Layers: " + FileName + " parsed to nothing, using defaults.");
                return new ExtraBuildLayersConfig();
            }

            logger.Info?.Log("Extra Build Layers: loaded " + FileName + ".");
            return config;
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            logger.Error?.Log("Extra Build Layers: could not read " + FileName + ", using defaults.");
            return new ExtraBuildLayersConfig();
        }
    }
}
