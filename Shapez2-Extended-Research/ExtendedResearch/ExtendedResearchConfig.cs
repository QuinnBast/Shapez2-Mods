using System;
using System.IO;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtendedResearch;

/// <summary>
/// Everything the mod adds, as numbers.
///
/// Loaded from config.json beside the assembly when present, defaults otherwise. A missing
/// or malformed file is not an error - the defaults are the intended experience and the
/// file only exists for people who want a different curve.
///
/// The tier counts are deliberately expressed as "extra on top of vanilla" rather than as
/// totals. The mod never needs to know what vanilla ships, which is what lets it survive a
/// game update that adds a tier of its own.
/// </summary>
[Serializable]
public class ExtendedResearchConfig
{
    public const string FileName = "config.json";

    /// <summary>
    /// Three building layers exist and no more, because the art does not go further.
    /// <c>BuildingDrawDataFactory.FromMeta</c> builds <c>MainMeshPerLayer</c> as a
    /// hardcoded 3-element array, <c>IslandLayoutFactory</c> lays out 3, and
    /// <c>StaticBuildingMeshBuilder.BuildBaseMesh</c> indexes that array by the building's
    /// layer - so a building on layer 3 throws IndexOutOfRange, which aborts the mesh build
    /// for its whole chunk and makes everything there invisible.
    ///
    /// Valid z runs 0..MaxBuildingLayer inclusive (IslandLayoutQuery), and MaxBuildingLayer
    /// is BuildingLayerUnlocks.Count, so the count must stay at or below 2. Vanilla already
    /// ships 2. There is no headroom without authoring a mesh per building per new layer.
    /// </summary>
    public const int MaxSupportedBuildingLayerUnlocks = 2;

    // ---- Linear upgrade tiers ------------------------------------------------------

    /// Extra tiers for belt, cutter, stacker and painter speed - every speed that is not a train.
    public int ExtraProductionSpeedTiers = 5;

    /// Extra tiers for the train speed upgrade.
    public int ExtraTrainSpeedTiers = 5;

    /// Extra tiers for train wagon capacity.
    public int ExtraTrainCapacityTiers = 3;

    /// Extra tiers for the chunk limit - "platform capacity" in the research screen.
    public int ExtraPlatformCapacityTiers = 5;

    /// Off by default; the hub is not what anyone means by "more research".
    public int ExtraHubInputTiers = 0;

    /// Off by default.
    public int ExtraShapeQuantityTiers = 0;

    /// <summary>
    /// Multiplies the per-tier value increment inherited from vanilla's last step. 1.0 keeps
    /// the game's own pace, which is almost always what you want - the costs are the dial
    /// that should move, not the rewards.
    /// </summary>
    public float ValueStepMultiplier = 1.0f;

    /// Each new tier costs this much more than the one before it.
    public float CostGrowthFactor = 1.6f;

    /// Used when an upgrade's last vanilla tier had no cost to extrapolate from.
    public int FallbackTierCost = 500;

    // ---- Layer unlocks -------------------------------------------------------------

    /// <summary>
    /// Extra building layers per platform. Off, and clamped to zero on stock art - see
    /// <see cref="MaxSupportedBuildingLayerUnlocks"/>. Only useful alongside a mod that
    /// supplies a fourth per-layer mesh for every building.
    /// </summary>
    public int ExtraBuildingLayers = 0;

    /// <summary>
    /// Extra platform layers - the vertical stacking of platforms in space. Off by default,
    /// but unlike building layers this does work: platforms place and rail lifts adapt to any
    /// range. The caveat is that the authored space belt pieces do not reach every new layer,
    /// so belt routing between distant layers can be impossible. Set to 1 or 2 if you can
    /// live with that.
    /// </summary>
    public int ExtraIslandLayers = 0;

    public int BuildingLayerBaseCost = 2500;
    public int IslandLayerBaseCost = 4000;
    public float LayerCostGrowthFactor = 2.0f;

    /// <summary>
    /// Shop tab the layer unlocks appear under. A category name is a translation key
    /// (<c>research.category.&lt;name&gt;</c>), so a custom one needs an entry in
    /// translations.json - which is why this defaults to the one the mod ships.
    /// </summary>
    public string ShopCategory = "ExtendedResearch";

    // ---- Per-upgrade escape hatch --------------------------------------------------

    [Serializable]
    public class UpgradeOverride
    {
        public string UpgradeId;
        public int ExtraTiers;
    }

    /// <summary>
    /// Wins over every count above, matched on the linear upgrade's own id. The mod logs
    /// every upgrade it touched along with that id, so the first run tells you what to
    /// write here.
    /// </summary>
    public UpgradeOverride[] Overrides = new UpgradeOverride[0];

    public bool TryGetOverride(string upgradeId, out int extraTiers)
    {
        if (Overrides != null)
        {
            foreach (UpgradeOverride entry in Overrides)
            {
                if (entry != null && entry.UpgradeId == upgradeId)
                {
                    extraTiers = entry.ExtraTiers;
                    return true;
                }
            }
        }

        extraTiers = 0;
        return false;
    }

    public static ExtendedResearchConfig Load(ILogger logger)
    {
        try
        {
            string path = ModDirectoryLocator.CreateLocator<ExtendedResearchMod>().SubPath(FileName);
            if (!File.Exists(path))
            {
                logger.Info?.Log("Extended Research: no " + FileName + ", using defaults.");
                return new ExtendedResearchConfig();
            }

            ExtendedResearchConfig config = JsonUtility.FromJson<ExtendedResearchConfig>(File.ReadAllText(path));
            if (config == null)
            {
                logger.Error?.Log("Extended Research: " + FileName + " parsed to nothing, using defaults.");
                return new ExtendedResearchConfig();
            }

            logger.Info?.Log("Extended Research: loaded " + FileName + ".");
            return config;
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            logger.Error?.Log("Extended Research: could not read " + FileName + ", using defaults.");
            return new ExtendedResearchConfig();
        }
    }
}
