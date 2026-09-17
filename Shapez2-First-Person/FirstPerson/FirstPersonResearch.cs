using System;
using System.Runtime.CompilerServices;
using Core.Localization;
using Game.Core.Research;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// The mod's research nodes: flight, waypoint travel and riding trains.
///
/// Each is a *rewardless* side upgrade - it grants a research mechanic and nothing else -
/// and the mod reads that mechanic back at runtime to decide whether the feature works.
/// All are configurable through <see cref="FirstPersonControl"/>, and each can be off
/// entirely, free, or bought.
///
/// These are definitions, not logic, so they are built once per scenario load and a hot
/// reload will not pick them up: **restart the game after installing.**
/// </summary>
public sealed class FirstPersonResearch : IGameScenarioRewirer
{
    /// <summary>
    /// One configurable unlock. Keeping the nodes as data rather than as three copies of
    /// the same fifty lines is what stops them drifting apart as options are added.
    /// </summary>
    private sealed class Unlock
    {
        public string Key;
        public Func<bool> Enabled;
        public Func<bool> RequiresResearch;
        public Func<int> Cost;

        public string MechanicId => "first-person." + Key;
        public string UpgradeId => "first-person." + Key + ".unlock";

        /// <summary>
        /// Off, or free, or bought. "Off" is a different question from "not yet researched":
        /// a mod that turns a feature off wants it gone, not pending.
        /// </summary>
        public bool IsUnlocked
        {
            get
            {
                if (!Enabled())
                {
                    return false;
                }

                if (!RequiresResearch())
                {
                    // No node was added, so there is no mechanic to ask about.
                    return true;
                }

                try
                {
                    IGameSessionManagers core = GameHelper.Core;
                    return core?.Research != null
                           && core.Research.Progress.IsUnlocked(new ResearchMechanicId(MechanicId));
                }
                catch (Exception)
                {
                    // No session - the main menu's background game, or a teardown.
                    return false;
                }
            }
        }

        public bool ShouldRegister => Enabled() && RequiresResearch();
    }

    /// Gets its own research category so it does not have to pretend to be a building.
    /// The screen renders `research.category.FirstPerson` from translations.json for it.
    private const string Category = "FirstPerson";

    private static readonly Unlock Flight = new Unlock
    {
        Key = "flight",
        Enabled = () => FirstPersonControl.FlightEnabled,
        RequiresResearch = () => FirstPersonControl.FlightRequiresResearch,
        Cost = () => FirstPersonControl.FlightResearchCostPoints,
    };

    private static readonly Unlock Travel = new Unlock
    {
        Key = "travel",
        Enabled = () => FirstPersonControl.TravelEnabled,
        RequiresResearch = () => FirstPersonControl.TravelRequiresResearch,
        Cost = () => FirstPersonControl.TravelResearchCostPoints,
    };

    private static readonly Unlock TrainRiding = new Unlock
    {
        Key = "train-riding",
        Enabled = () => FirstPersonControl.TrainRidingEnabled,
        RequiresResearch = () => FirstPersonControl.TrainRidingRequiresResearch,
        Cost = () => FirstPersonControl.TrainRidingResearchCostPoints,
    };

    public static bool FlightUnlocked => Flight.IsUnlocked;

    public static bool TrainRidingUnlocked => TrainRiding.IsUnlocked;

    public static bool TravelUnlocked => Travel.IsUnlocked;

    private readonly ILogger Logger;

    /// <summary>
    /// <c>GameMode.From</c> builds a fresh scenario per call so this normally never fires,
    /// but applying twice would put duplicate nodes in the shop, and
    /// <c>SideUpgradeBuilder.Build</c> appends unconditionally.
    /// </summary>
    private readonly ConditionalWeakTable<GameScenario, object> AlreadyExtended =
        new ConditionalWeakTable<GameScenario, object>();

    public FirstPersonResearch(ILogger logger)
    {
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
            if (AlreadyExtended.TryGetValue(gameScenario, out object _))
            {
                return gameScenario;
            }

            AlreadyExtended.Add(gameScenario, string.Empty);

            // A shop entry's preview image is not optional. HUDResearchSideUpgradeDisplay
            // .RebuildView calls GameData.GetImage(upgrade.ImageId) unconditionally and
            // GetImage throws on an id it cannot resolve, GameImageId.Empty included - and
            // that throw comes out of the whole HUDResearchTree construction, leaving a
            // research screen that is half built and cannot be closed. Better no node than a
            // broken screen, so this is checked once before adding anything.
            if (!TryBorrowImageId(gameScenario.Progression, out GameImageId imageId))
            {
                Logger.Error?.Log("First Person: no research image to borrow, skipping the unlocks.");
                return gameScenario;
            }

            Add(gameScenario, Flight, imageId);
            Add(gameScenario, Travel, imageId);
            Add(gameScenario, TrainRiding, imageId);
        }
        catch (Exception exception)
        {
            // A throw here would take the scenario, and with it the save. A feature staying
            // locked is a worse mod; a game that will not load is a worse day.
            Logger.Exception?.LogException(exception);
            Logger.Error?.Log("First Person: could not add the research, those features stay locked.");
        }

        return gameScenario;
    }

    private void Add(GameScenario gameScenario, Unlock unlock, GameImageId imageId)
    {
        // Either the feature is off, or it is free. Adding a node nothing gates would leave
        // a purchasable no-op in the research shop.
        if (!unlock.ShouldRegister)
        {
            return;
        }

        ResearchMechanics mechanics = gameScenario.Mechanics;

        // ResearchMechanic only builds from serialized data, and SerializedResearchMechanic
        // is a plain field bag - this is the constructor the game itself uses.
        mechanics.Mechanics.Add(new ResearchMechanic(new SerializedResearchMechanic
        {
            Id = unlock.MechanicId,
            Title = "@first-person." + unlock.Key + ".mechanic.title",
            Description = "@first-person." + unlock.Key + ".mechanic.description",
            IconId = BorrowIconId(mechanics),
            HideReward = false,
        }));

        // Our own artwork when it loaded, the borrowed one when it did not. A node without
        // an image that resolves takes the research screen down with it, so the fallback is
        // not a nicety.
        if (!FirstPersonIcons.TryGetImageId(unlock.Key, out GameImageId icon))
        {
            icon = imageId;
        }

        SideUpgrade.New()
            .WithPresentationData(new SideUpgradePresentationData(
                new ResearchUpgradeId(unlock.UpgradeId),
                icon,
                GameVideoId.Empty,
                ("first-person." + unlock.Key + ".title").T(),
                ("first-person." + unlock.Key + ".description").T(),
                hidden: false,
                Category))
            .WithCost(new IResearchCost[] { new ResearchCostPoints(new ResearchPointCurrency(unlock.Cost())) })
            // No prerequisites on purpose. Every other node in the tree gates content that
            // needs a factory behind it; these gate a camera, they are meant to be affordable
            // early, and there is no vanilla node they belong next to.
            .WithCustomRequirements(Array.Empty<ResearchMechanicId>(), Array.Empty<ResearchUpgradeId>())
            .WithAdditionalRewards(new IResearchReward[]
            {
                new ResearchRewardMechanic(new ResearchMechanicId(unlock.MechanicId)),
            })
            .Build(gameScenario.UniqueId, gameScenario.Progression);

        Logger.Info?.Log("First Person: added the " + unlock.Key + " research to scenario '"
                         + gameScenario.UniqueId + "'.");
    }

    /// <summary>
    /// Image ids live in Unity assets rather than anywhere readable from here, so borrowing
    /// one from an upgrade the game already renders is the only way to be sure it resolves.
    /// </summary>
    private static bool TryBorrowImageId(ResearchProgression progression, out GameImageId imageId)
    {
        foreach (ResearchSideUpgrade upgrade in progression.SideUpgrades)
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
    /// Same reasoning as the image, one level down: the mechanic's icon has to resolve too,
    /// and any existing mechanic's icon does.
    /// </summary>
    private static string BorrowIconId(ResearchMechanics mechanics)
    {
        foreach (ResearchMechanic mechanic in mechanics.Mechanics)
        {
            if (!string.IsNullOrEmpty(mechanic.IconId.Id))
            {
                return mechanic.IconId.Id;
            }
        }

        return string.Empty;
    }
}
