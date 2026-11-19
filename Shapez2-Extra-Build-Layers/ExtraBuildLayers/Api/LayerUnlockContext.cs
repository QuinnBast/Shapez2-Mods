using System;
using System.Collections.Generic;
using Core.Localization;
using Game.Core.Research;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Everything a <see cref="ILayerUnlockPolicy"/> needs, for one <see cref="LayerKind"/>.
///
/// The layers already exist by the time a policy sees this: the mechanics are registered and
/// the ceilings are raised. What is left is entirely a question of presentation - how, or
/// whether, the player earns them. A policy that does nothing at all is valid and leaves the
/// new layers permanently out of reach, which is the correct outcome for "this mod adds the
/// capacity, another mod decides the progression".
///
/// The helpers here exist because the two ways to get this wrong are not obvious and are
/// both fatal rather than cosmetic - see <see cref="TryBorrowPreviewImage"/> and
/// <see cref="AddShopUpgrade"/>.
/// </summary>
public sealed class LayerUnlockContext
{
    private readonly List<ResearchUpgradeId> PendingGrants;

    internal LayerUnlockContext(
        GameScenario scenario,
        LayerKind kind,
        IReadOnlyList<AddedLayer> layers,
        ResearchMechanicId? ceilingGate,
        string borrowedIconId,
        List<ResearchUpgradeId> pendingGrants,
        ILogger logger)
    {
        Scenario = scenario;
        Kind = kind;
        Layers = layers;
        CeilingGate = ceilingGate;
        IconId = borrowedIconId;
        PendingGrants = pendingGrants;
        Logger = logger;
    }

    public GameScenario Scenario { get; }

    public LayerKind Kind { get; }

    /// The layers this mod just created, in ascending <see cref="AddedLayer.Level"/> order.
    public IReadOnlyList<AddedLayer> Layers { get; }

    /// <summary>
    /// The mechanic that granted the scenario's own topmost layer of this kind, if it had one.
    /// Gating the first new unlock behind this is what keeps the additions at the end of the
    /// existing progression instead of alongside it.
    ///
    /// For <see cref="LayerKind.Space"/> this is the unlock with the highest entry in
    /// <c>IslandLayersUnlockOrder</c>, which is **not** the last entry in the unlock list -
    /// vanilla interleaves layers above and below the start.
    /// </summary>
    public ResearchMechanicId? CeilingGate { get; }

    /// <summary>
    /// An icon id borrowed from the scenario's own last layer unlock of this kind. Guaranteed
    /// to resolve, and already reads as "another layer" on the research screen. Empty if the
    /// scenario had no layer unlocks to borrow from.
    /// </summary>
    public string IconId { get; }

    public ILogger Logger { get; }

    /// <summary>
    /// A shop entry's preview image is **not optional**. <c>HUDResearchSideUpgradeDisplay.RebuildView</c>
    /// calls <c>GameData.GetImage(upgrade.ImageId)</c> unconditionally and <c>GetImage</c> throws on an
    /// id it cannot resolve, the empty one included. That throw comes out of the whole
    /// <c>HUDResearchTree</c> prefab construction and leaves the research screen half-built and
    /// unclosable - a broken game, not a missing picture.
    ///
    /// Image ids live in Unity assets and cannot be read from here, so borrowing one off an upgrade
    /// the game already renders is the only way to be certain. A policy that cannot get one should
    /// add no shop entries at all.
    /// </summary>
    public bool TryBorrowPreviewImage(out GameImageId imageId)
    {
        foreach (ResearchSideUpgrade upgrade in Scenario.Progression.SideUpgrades)
        {
            if (upgrade.ImageId.HasValue)
            {
                imageId = upgrade.ImageId;
                return true;
            }
        }

        imageId = GameImageId.Empty;
        return false;
    }

    /// <summary>
    /// Registers a side upgrade that grants <paramref name="mechanics"/>, and returns its id so
    /// the next one can be chained behind it.
    ///
    /// Shifter's builder does the four-list registration the progression needs - side upgrades,
    /// shop items, all upgrades, id lookup - and registers the category. Doing it by hand and
    /// missing one list produces an upgrade that exists but never renders.
    /// </summary>
    /// <param name="cost">
    /// In raw points. <c>Format(this ResearchPointCurrency)</c> renders <c>Amount * 100</c>, so
    /// this number is a hundredth of what the shop shows: 2500 here is "250k" on screen. Getting
    /// it backwards prices a node a hundredfold wrong and still looks plausible.
    /// </param>
    public ResearchUpgradeId AddShopUpgrade(
        string upgradeId,
        IText title,
        IText description,
        IReadOnlyList<ResearchMechanicId> mechanics,
        GameImageId imageId,
        string category,
        int cost,
        ResearchMechanicId? requiredMechanic = null,
        ResearchUpgradeId? requiredUpgrade = null,
        bool hidden = false)
    {
        ResearchUpgradeId id = new ResearchUpgradeId(upgradeId);

        SideUpgradePresentationData presentation = new SideUpgradePresentationData(
            id, imageId, GameVideoId.Empty, title, description, hidden, category);

        IResearchReward[] rewards = new IResearchReward[mechanics.Count];
        for (int i = 0; i < mechanics.Count; i++)
        {
            rewards[i] = new ResearchRewardMechanic(mechanics[i]);
        }

        SideUpgrade.New()
            .WithPresentationData(presentation)
            .WithCost(new IResearchCost[] { new ResearchCostPoints(new ResearchPointCurrency(cost)) })
            .WithCustomRequirements(
                requiredMechanic.HasValue ? new[] { requiredMechanic.Value } : Array.Empty<ResearchMechanicId>(),
                requiredUpgrade.HasValue ? new[] { requiredUpgrade.Value } : Array.Empty<ResearchUpgradeId>())
            .WithAdditionalRewards(rewards)
            .Build(Scenario.UniqueId, Scenario.Progression);

        Logger.Info?.Log(
            "Extra Build Layers: registered '" + upgradeId + "' granting " + mechanics.Count +
            " mechanic(s) for " + cost + " points.");

        return id;
    }

    /// <summary>
    /// Marks an upgrade to be unlocked outright once a session exists, with no player action.
    ///
    /// This cannot be done by writing into the progress manager's unlocked-mechanic set:
    /// <c>RecomputeUnlocks</c> clears that set and rebuilds it from the rewards of unlocked
    /// upgrades, so anything injected is wiped the next time anything changes. The supported
    /// route is <c>TryUnlockInternal</c>, which adds to <c>_ManuallyUnlockedUpgrades</c> and
    /// recomputes - and that needs a live session, which the scenario rewirer does not have.
    ///
    /// So the id is queued here and drained by the mod on the first tick of a session. Note
    /// that <c>_ManuallyUnlockedUpgrades</c> is **serialized**, so a granted upgrade persists
    /// into the save; removing the mod leaves an id the progress manager's constructor logs a
    /// warning for and skips, which is graceful but not silent.
    /// </summary>
    public void GrantWithoutResearch(ResearchUpgradeId upgradeId)
    {
        PendingGrants.Add(upgradeId);
    }
}
