using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Game.Core.Research;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// The research half of the mod.
///
/// Shifter runs this immediately after the game builds a <see cref="GameScenario"/>, which is
/// the only moment where the research layout is fully assembled and nothing has read it yet.
/// The scenario's own validation has already passed by then, so additions here are not
/// re-checked - a licence to be careful rather than a licence to be sloppy.
///
/// Ordering is in our favour. <c>GameMode.From</c> builds the scenario first and the buildings
/// second, so the machine-level count is already raised when
/// <see cref="BuildingMeshLayerWidener"/> runs. The two do not read each other, though: both
/// work from the same requested counts. That way a failure here leaves over-long mesh arrays,
/// which is harmless, rather than a layer with no mesh, which is not.
/// </summary>
internal class LayerScenarioExtender : IGameScenarioRewirer
{
    private readonly ExtraBuildLayersConfig Config;
    private readonly List<ResearchUpgradeId> PendingGrants;
    private readonly ILogger Logger;

    /// <summary>
    /// GameMode.From builds a fresh scenario per call, so this normally never fires. It is here
    /// because applying twice would silently double every layer added, and that is a bad enough
    /// failure to be worth one table.
    /// </summary>
    private readonly ConditionalWeakTable<GameScenario, object> AlreadyExtended =
        new ConditionalWeakTable<GameScenario, object>();

    public LayerScenarioExtender(ExtraBuildLayersConfig config, List<ResearchUpgradeId> pendingGrants, ILogger logger)
    {
        Config = config;
        PendingGrants = pendingGrants;
        Logger = logger;
    }

    public GameScenario ModifyGameScenario(GameScenario gameScenario)
    {
        if (gameScenario == null)
        {
            return null;
        }

        try
        {
            if (AlreadyExtended.TryGetValue(gameScenario, out _))
            {
                Logger.Info?.Log("Extra Build Layers: scenario already extended, skipping.");
                return gameScenario;
            }

            AlreadyExtended.Add(gameScenario, string.Empty);

            // Grants are per scenario. Carrying them between sessions would try to unlock an
            // upgrade id belonging to a progression that no longer exists.
            PendingGrants.Clear();

            Logger.Info?.Log("Extra Build Layers: extending scenario '" + gameScenario.UniqueId + "'.");

            LayerRegistrar.Register(
                gameScenario,
                BuildLayers.ExtraMachineLevels,
                BuildLayers.ExtraSpaceLevels,
                BuildLayers.UnlockPolicy,
                PendingGrants,
                Logger);
        }
        catch (Exception exception)
        {
            // A throw here would take the scenario - and the save - with it. A partially
            // extended research tree is recoverable; a game that will not load is not.
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log("Extra Build Layers: extension failed, continuing with whatever was applied.");
        }

        return gameScenario;
    }
}
