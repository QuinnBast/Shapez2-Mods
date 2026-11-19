using System;
using System.Collections.Generic;
using Game.Core.Research;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Unlocks the upgrades a policy asked to be granted outright, on the first tick of a session
/// that has one.
///
/// A mechanic cannot be unlocked by writing into the progress manager's set:
/// <c>RecomputeUnlocks</c> clears <c>_CachedUnlockedMechanics</c> and rebuilds it from the
/// rewards of unlocked upgrades, so an injected entry survives exactly until the next research
/// change. The supported route is <c>TryUnlockInternal</c>, which adds the upgrade id to
/// <c>_ManuallyUnlockedUpgrades</c> and recomputes - and that needs a live
/// <c>ResearchManager</c>, which the scenario rewirer does not have.
///
/// Hence a tick. <see cref="ITickRewirer"/> fires from <c>GameSessionOrchestrator.Tick</c>, so
/// a session exists by definition; <c>GameHelper.Core.Research</c> is still guarded, because
/// the first tick is early and this runs on every session including the main menu's background
/// game.
///
/// <c>TryUnlockInternal</c> returns false when the id is already in the set, which is what
/// happens on every load of a save that was granted before - so re-granting is free, and the
/// drain is not conditional on being a new save.
/// </summary>
internal class PendingGrantDrainer : ITickRewirer
{
    private readonly List<ResearchUpgradeId> PendingGrants;
    private readonly ILogger Logger;

    public PendingGrantDrainer(List<ResearchUpgradeId> pendingGrants, ILogger logger)
    {
        PendingGrants = pendingGrants;
        Logger = logger;
    }

    public void Tick(float deltaTime)
    {
        if (PendingGrants.Count == 0)
        {
            return;
        }

        try
        {
            ResearchManager research = GameHelper.Core?.Research;
            if (research?.Progress == null)
            {
                return;   // too early; try again next tick
            }

            foreach (ResearchUpgradeId id in PendingGrants)
            {
                if (!research.Layout.TryGetUpgrade(id, out IResearchUpgrade upgrade))
                {
                    Logger.Error?.Log(
                        "Extra Build Layers: cannot grant '" + id.Id + "' - this session's progression " +
                        "does not have it. The layers it carried stay locked.");
                    continue;
                }

                if (research.Progress.TryUnlockInternal(upgrade))
                {
                    Logger.Info?.Log("Extra Build Layers: granted '" + id.Id + "'.");
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log("Extra Build Layers: granting failed; the added layers stay locked.");
        }
        finally
        {
            // Cleared either way. Retrying a throwing unlock every tick for the rest of the
            // session would turn one logged failure into a flooded log.
            PendingGrants.Clear();
        }
    }
}
