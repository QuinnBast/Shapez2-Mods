using System;
using System.Collections.Generic;
using Game.Core.Research;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Extra Build Layers.
///
/// More machine levels inside every platform, and more platform levels in space. Nothing new
/// is invented: both ceilings are already counts of a list the game keeps at runtime, and this
/// adds rows to those lists.
///
/// The mod is deliberately a **substrate**. Creating a layer and deciding how the player earns
/// it are different problems, and only the first has one right answer - so the second is a
/// replaceable <see cref="ILayerUnlockPolicy"/> and everything is reachable from
/// <see cref="BuildLayers"/>. A mod that wants fifty layers available on the first frame, or
/// wants them hung off its own research, sets two properties in its constructor and never
/// touches this assembly again.
///
/// The pieces:
/// <list type="bullet">
/// <item><see cref="LayerScenarioExtender"/> registers the layers and runs the policy.</item>
/// <item><see cref="BuildingMeshLayerWidener"/> (twice: early, and late for other mods'
/// buildings) widens each building's per-layer mesh array, without which a machine on a new
/// floor takes its whole chunk's mesh down.</item>
/// <item><see cref="ExtraLiftBuildings"/> and <see cref="ExtraLiftSystems"/> make belts and pipes
/// inside a platform climb any number of floors.</item>
/// <item><see cref="LiftStationIslands"/>, <see cref="LiftStationPlacementRewirer"/> and
/// <see cref="LiftStationConnectors"/> make space belts and pipes climb any number of space levels,
/// with an entry and an exit station joined by a shaft rather than a definition per height.</item>
/// <item><see cref="LayerVisualResourcesExtender"/> and <see cref="LayerNumberArt"/> give the new
/// floors colours, floor planes and their own numbers; <see cref="HighFloorBlueprints"/> keeps
/// previews lit above floor 9.</item>
/// <item><see cref="PendingGrantDrainer"/> unlocks whatever a policy asked to be granted.</item>
/// <item><see cref="CompactLayerStrip"/> keeps the HUD layer strip usable at high counts.</item>
/// <item><see cref="SavegameAdoption"/> lets an existing save take the mod.</item>
/// </list>
///
/// It changes what a save contains: buildings and platforms above the scenario's own ceilings
/// are written into the savegame, and research progress records mechanics and upgrade ids that
/// only exist while the mod is loaded. Hence AffectsSaveGames - relaxed for adding the mod to a
/// save, never for removing it (see <see cref="SavegameAdoption"/>).
/// </summary>
[UsedImplicitly]
public class ExtraBuildLayersMod : IMod
{
    private readonly ILogger Logger;
    private readonly RewirerHandle ScenarioHandle;
    private readonly RewirerHandle BuildingsHandle;
    private readonly RewirerHandle VisualsHandle;
    private readonly RewirerHandle GrantHandle;
    private readonly RewirerHandle ShaderParamsHandle;
    private readonly RewirerHandle DumpHandle;
    private readonly RewirerHandle LiftDumpHandle;
    private readonly RewirerHandle LiftBuildingsHandle;
    private readonly RewirerHandle LiftSystemsHandle;
    private readonly RewirerHandle? StationArtHandle;
    private readonly RewirerHandle? StationPlacementHandle;
    private readonly LiftStationIslands Stations;
    private readonly LiftStationConnectors StationConnectors;
    private readonly LegacyTallLiftRemoval LegacyLifts;
    private readonly LiftShaftPicking ShaftPicking;
    private readonly LiftStationPairedDeletion PairedDeletion;
    private readonly TallPipeShaftInstaller PipeShafts;
    private readonly HighFloorBlueprints HighFloorPreviews;
    private readonly LiveGridNumber GridNumber;
    private readonly LayerKeyRepeat KeyRepeat;
    private readonly LayerJumpOnSelect JumpOnSelect;
    private readonly SpaceCullingBounds CullingBounds;
    private readonly OverviewLayerParking OverviewParking;
    private readonly ViewSelectionAction ViewAction;
    private readonly ActionButtonRows ButtonRows;
    private readonly SavegameAdoption SaveAdoption;
    private readonly RewirerHandle LateWidenerHandle;
    private readonly LayerShaderParamsRefresher ShaderParams;
    private readonly CompactLayerStrip LayerStrip;

    public ExtraBuildLayersMod(ILogger logger)
    {
        Logger = logger;

        ExtraBuildLayersConfig config = ExtraBuildLayersConfig.Load(logger);

        // Seeds only. Mod constructors all run before the first scenario is built, so a
        // dependent mod that sets these in its own constructor overrides the config rather
        // than racing it.
        BuildLayers.ExtraMachineLevels = config.ExtraMachineLevels;
        BuildLayers.ExtraSpaceLevels = config.ExtraSpaceLevels;
        BuildLayers.UnlockPolicy = config.CreatePolicy(logger);

        List<ResearchUpgradeId> pendingGrants = new List<ResearchUpgradeId>();

        ScenarioHandle = GameRewirers.AddRewirer(new LayerScenarioExtender(config, pendingGrants, logger));
        BuildingsHandle = GameRewirers.AddRewirer(new BuildingMeshLayerWidener(logger));
        LateWidenerHandle = GameRewirers.AddRewirer(new LateBuildingMeshLayerWidener(logger));
        VisualsHandle = GameRewirers.AddRewirer(new LayerVisualResourcesExtender(logger, config.ExtendLayerRamps));
        LiftBuildingsHandle = GameRewirers.AddRewirer(new ExtraLiftBuildings(logger));
        LiftSystemsHandle = GameRewirers.AddRewirer(new ExtraLiftSystems(logger));
        PipeShafts = new TallPipeShaftInstaller(logger);
        HighFloorPreviews = new HighFloorBlueprints(logger);
        GridNumber = new LiveGridNumber(logger);
        CullingBounds = new SpaceCullingBounds(logger);
        OverviewParking = new OverviewLayerParking(logger);
        ViewAction = new ViewSelectionAction(logger, ModResources.Locate(logger));
        ButtonRows = new ActionButtonRows(logger);
        if (config.LayerKeyRepeatDelay > 0f)
        {
            KeyRepeat = new LayerKeyRepeat(config.LayerKeyRepeatDelay, config.OverviewLayerStep, logger);
        }

        if (config.JumpToSelectedLayer)
        {
            JumpOnSelect = new LayerJumpOnSelect(logger);
        }

        if (config.AllowAddingToExistingSaves)
        {
            SaveAdoption = new SavegameAdoption(logger);
        }
        GrantHandle = GameRewirers.AddRewirer(new PendingGrantDrainer(pendingGrants, logger));
        DumpHandle = GameRewirers.AddRewirer(new LayerVisualsDump(logger));
        LiftDumpHandle = GameRewirers.AddRewirer(new LiftGeometryDump(logger));

        // Space lifts of any height. Contained: a throw out of a mod constructor is not caught
        // by ModLoader, and the rest of the mod works without these.
        try
        {
            Stations = new LiftStationIslands(logger, ModResources.Locate(logger));
            StationConnectors = new LiftStationConnectors(logger);
            LegacyLifts = new LegacyTallLiftRemoval(logger);
            ShaftPicking = new LiftShaftPicking(logger);
            PairedDeletion = new LiftStationPairedDeletion(logger);
            StationArtHandle = GameRewirers.AddRewirer(new LiftStationArt(logger));
            StationPlacementHandle = GameRewirers.AddRewirer(new LiftStationPlacementRewirer(logger));
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            logger.Error?.Log("Extra Build Layers: lift stations unavailable; space paths climb only as far as vanilla's lift.");
        }

        if (config.RefreshLayerShaderParams)
        {
            ShaderParams = new LayerShaderParamsRefresher(logger);
            ShaderParamsHandle = GameRewirers.AddRewirer(ShaderParams);
        }
        else
        {
            Logger.Info?.Log("Extra Build Layers: layer shader parameter refresh disabled by config.");
        }

        if (config.CompactLayerStrip)
        {
            LayerStrip = new CompactLayerStrip(config.LayerStripMaxHeight, config.LayerStripNumberAbove, logger);
        }

        Logger.Info?.Log(
            "Extra Build Layers ready: +" + config.ExtraMachineLevels + " machine level(s), +" +
            config.ExtraSpaceLevels + " space level(s), policy " +
            (BuildLayers.UnlockPolicy?.GetType().Name ?? "none") + ".");
    }

    public void Dispose()
    {
        GameRewirers.RemoveRewirer(ScenarioHandle);
        GameRewirers.RemoveRewirer(BuildingsHandle);
        GameRewirers.RemoveRewirer(VisualsHandle);
        GameRewirers.RemoveRewirer(GrantHandle);
        GameRewirers.RemoveRewirer(ShaderParamsHandle);
        GameRewirers.RemoveRewirer(DumpHandle);
        GameRewirers.RemoveRewirer(LiftDumpHandle);
        GameRewirers.RemoveRewirer(LiftBuildingsHandle);
        GameRewirers.RemoveRewirer(LiftSystemsHandle);
        if (StationArtHandle.HasValue)
        {
            GameRewirers.RemoveRewirer(StationArtHandle.Value);
        }

        if (StationPlacementHandle.HasValue)
        {
            GameRewirers.RemoveRewirer(StationPlacementHandle.Value);
        }

        StationConnectors?.Dispose();
        LegacyLifts?.Dispose();
        ShaftPicking?.Dispose();
        PairedDeletion?.Dispose();
        Stations?.Dispose();
        PipeShafts?.Dispose();
        HighFloorPreviews?.Dispose();
        GridNumber?.Dispose();
        CullingBounds?.Dispose();
        OverviewParking?.Dispose();
        ViewAction?.Dispose();
        ButtonRows?.Dispose();
        KeyRepeat?.Dispose();
        JumpOnSelect?.Dispose();
        SaveAdoption?.Dispose();
        GameRewirers.RemoveRewirer(LateWidenerHandle);
        ShaderParams?.Dispose();
        LayerStrip?.Dispose();
    }
}
