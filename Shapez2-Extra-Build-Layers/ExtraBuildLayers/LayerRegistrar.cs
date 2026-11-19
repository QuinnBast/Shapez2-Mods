using System;
using System.Collections.Generic;
using Game.Core.Research;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Creates the layers. Everything about *earning* them is delegated to an
/// <see cref="ILayerUnlockPolicy"/>.
///
/// Neither ceiling is a constant:
/// <code>
/// // GameMode
/// public short MaxBuildingLayer => (short)Mechanics.BuildingLayerUnlocks.Count;
/// public short MaxIslandLayer   => (short)(MinIslandLayer + Mechanics.IslandLayerUnlocks.Count);
/// </code>
/// A layer exists precisely because a mechanic exists to unlock it, so creating a layer is
/// appending a <see cref="ResearchMechanic"/> and a <see cref="ResearchMechanicId"/> - nothing
/// more. That is the whole reason this mod can be a substrate: the expensive, fragile part is
/// eight lines, and the part worth arguing about is the progression, which is somebody else's
/// call.
///
/// The player's own ceiling is a second, separate walk in <c>BaseMapInteractionMode</c>, which
/// scans the list backwards and stops at the first entry the player has unlocked. So a
/// mechanic nobody owns leaves the ceiling exactly where it was: registering fifty layers and
/// presenting none of them changes nothing the player can see, which is what makes
/// <see cref="NoUnlockPolicy"/> safe rather than broken.
/// </summary>
internal static class LayerRegistrar
{
    private const string MechanicIdPrefix = "ExtraBuildLayers.";

    public static void Register(
        GameScenario scenario,
        int extraMachineLevels,
        int extraSpaceLevels,
        ILayerUnlockPolicy policy,
        List<ResearchUpgradeId> pendingGrants,
        ILogger logger)
    {
        IReadOnlyList<AddedLayer> machine = AddMachineLevels(scenario, extraMachineLevels, logger);
        IReadOnlyList<AddedLayer> space = AddSpaceLevels(scenario, extraSpaceLevels, logger);

        ResearchMechanics mechanics = scenario.Mechanics;

        Present(scenario, LayerKind.Machine, machine, MachineCeilingGate(mechanics, machine.Count),
            BorrowIconId(mechanics, mechanics.BuildingLayerUnlocks, machine.Count), policy, pendingGrants, logger);

        Present(scenario, LayerKind.Space, space, SpaceCeilingGate(mechanics, space.Count),
            BorrowIconId(mechanics, mechanics.IslandLayerUnlocks, space.Count), policy, pendingGrants, logger);

        BuildLayers.Publish(
            new LayerRegistration(
                scenario.UniqueId.ToString(),
                machine,
                space,
                topMachineLevel: mechanics.BuildingLayerUnlocks.Count,
                topSpaceLevel: HighestSpaceOrder(mechanics)),
            logger);
    }

    /// <summary>
    /// Building layers are positional: unlocking entry <c>n</c> allows layer <c>n + 1</c>, so
    /// the new levels simply continue the list and their numbers fall out of its length.
    /// </summary>
    private static IReadOnlyList<AddedLayer> AddMachineLevels(GameScenario scenario, int extra, ILogger logger)
    {
        if (extra <= 0)
        {
            return Array.Empty<AddedLayer>();
        }

        List<ResearchMechanicId> unlocks = scenario.Mechanics.BuildingLayerUnlocks;

        // A clamp, not advice: layer 20 is layer 0 of the island layer above, because a
        // building's layer is recovered as SafeMod(tile.z, 20). Letting the count past that
        // would put machines on another platform's ground floor.
        int room = BuildLayers.MaxMachineLevel - unlocks.Count;
        if (extra > room)
        {
            logger.Error?.Log(
                "Extra Build Layers: " + extra + " extra machine levels requested but only " +
                Math.Max(0, room) + " fit. A building layer is SafeMod(tile.z, 20), so layer " +
                (BuildLayers.MaxMachineLevel + 1) + " aliases onto the next island layer's ground " +
                "floor. Clamping.");
            extra = Math.Max(0, room);
            if (extra == 0)
            {
                return Array.Empty<AddedLayer>();
            }
        }

        if (unlocks.Count + extra >= BuildLayers.PortConnectedMachineLevels)
        {
            // Info, not error. Nothing is broken and nothing can be done about it - it is worth
            // saying once so "my belt port on floor 5 does nothing" is not a mystery.
            logger.Info?.Log(
                "Extra Build Layers: note that only building layers 0-" +
                (BuildLayers.PortConnectedMachineLevels - 1) + " can carry ports to another " +
                "platform. SpacePathPortSystem builds sender and receiver simulations over a " +
                "hardcoded 3 layers and is a generic type, so this cannot be raised. Lifts and " +
                "everything inside a platform reach every floor.");
        }

        List<AddedLayer> added = new List<AddedLayer>(extra);

        // Borrowed once, before anything is appended: after the first iteration the end of the
        // list is our own mechanic, and borrowing from ourselves answers nothing.
        string iconId = BorrowIconId(scenario.Mechanics, unlocks, 0);

        for (int i = 0; i < extra; i++)
        {
            int level = unlocks.Count + 1;

            ResearchMechanicId mechanicId = AddMechanic(
                scenario.Mechanics,
                MechanicIdPrefix + "MachineLevel" + level,
                "@extra-build-layers.machine-level.mechanic.title",
                "@extra-build-layers.machine-level.mechanic.description",
                iconId);

            unlocks.Add(mechanicId);
            added.Add(new AddedLayer(LayerKind.Machine, level, mechanicId));
        }

        logger.Info?.Log("Extra Build Layers: machine levels " + (unlocks.Count - extra) + " -> " + unlocks.Count + ".");
        return added;
    }

    /// <summary>
    /// Island layers carry an explicit <c>IslandLayersUnlockOrder</c> beside the unlock list,
    /// because vanilla hands out layers above and below the starting one interleaved rather
    /// than bottom to top. New levels therefore continue from the **highest order in use**,
    /// not from the list count - the two differ in every scenario that has a layer below zero.
    /// </summary>
    private static IReadOnlyList<AddedLayer> AddSpaceLevels(GameScenario scenario, int extra, ILogger logger)
    {
        if (extra <= 0)
        {
            return Array.Empty<AddedLayer>();
        }

        ResearchMechanics mechanics = scenario.Mechanics;
        List<ResearchMechanicId> unlocks = mechanics.IslandLayerUnlocks;
        List<int> order = mechanics.IslandLayersUnlockOrder;

        // The two lists are read by a shared index - BaseMapInteractionMode indexes
        // IslandLayersUnlockOrder with the position it found in IslandLayerUnlocks - so a
        // mismatch is not something to paper over. It would be an IndexOutOfRange in the
        // player's layer ceiling, on every frame that asks.
        if (order.Count != unlocks.Count)
        {
            logger.Error?.Log(
                "Extra Build Layers: IslandLayersUnlockOrder has " + order.Count + " entries for " +
                unlocks.Count + " unlocks; skipping space levels rather than guessing.");
            return Array.Empty<AddedLayer>();
        }

        int highestOrder = HighestSpaceOrder(mechanics);

        // A safety margin rather than a coordinate limit - see BuildLayers.MaxSpaceLevel for what
        // grows per layer. Clamped and logged rather than thrown, so an over-eager request from
        // another mod costs that mod some layers instead of costing the player their session.
        int room = BuildLayers.MaxSpaceLevel - highestOrder;
        if (extra > room)
        {
            logger.Error?.Log(
                "Extra Build Layers: " + extra + " extra space levels requested but only " +
                Math.Max(0, room) + " fit under the ceiling of " + BuildLayers.MaxSpaceLevel +
                " (see BuildLayers.MaxSpaceLevel). Clamping.");
            extra = Math.Max(0, room);
            if (extra == 0)
            {
                return Array.Empty<AddedLayer>();
            }
        }

        string iconId = BorrowIconId(mechanics, unlocks, 0);
        List<AddedLayer> added = new List<AddedLayer>(extra);

        for (int i = 0; i < extra; i++)
        {
            int level = highestOrder + i + 1;

            ResearchMechanicId mechanicId = AddMechanic(
                mechanics,
                MechanicIdPrefix + "SpaceLevel" + level,
                "@extra-build-layers.space-level.mechanic.title",
                "@extra-build-layers.space-level.mechanic.description",
                iconId);

            unlocks.Add(mechanicId);
            order.Add(level);
            added.Add(new AddedLayer(LayerKind.Space, level, mechanicId));
        }

        logger.Info?.Log(
            "Extra Build Layers: space levels " + highestOrder + " -> " + (highestOrder + extra) +
            " (" + unlocks.Count + " unlock entries, MaxIslandLayer now reports " + unlocks.Count + ").");
        return added;
    }

    private static void Present(
        GameScenario scenario,
        LayerKind kind,
        IReadOnlyList<AddedLayer> layers,
        ResearchMechanicId? ceilingGate,
        string iconId,
        ILayerUnlockPolicy policy,
        List<ResearchUpgradeId> pendingGrants,
        ILogger logger)
    {
        if (layers.Count == 0 || policy == null)
        {
            return;
        }

        LayerUnlockContext context = new LayerUnlockContext(
            scenario, kind, layers, ceilingGate, iconId, pendingGrants, logger);

        try
        {
            policy.Present(context);
        }
        catch (Exception exception)
        {
            // The layers are already registered and the ceilings already raised. Losing the
            // presentation leaves them unreachable, which is recoverable; letting this out of
            // the scenario rewirer would take the save.
            logger.Exception?.LogException(exception);
            logger.Error?.Log(
                "Extra Build Layers: unlock policy " + policy.GetType().Name + " threw for " + kind +
                " levels. They are registered but nothing will make them reachable.");
        }
    }

    /// <summary>
    /// The mechanic that granted the scenario's own topmost machine level - the last entry
    /// before this mod appended to the list.
    /// </summary>
    private static ResearchMechanicId? MachineCeilingGate(ResearchMechanics mechanics, int addedCount)
    {
        int originalCount = mechanics.BuildingLayerUnlocks.Count - addedCount;
        return originalCount > 0 ? mechanics.BuildingLayerUnlocks[originalCount - 1] : (ResearchMechanicId?)null;
    }

    /// <summary>
    /// The mechanic that granted the scenario's own topmost space level. Found by order, not
    /// by position: with interleaved layers the last pre-existing entry is as likely to be the
    /// bottom of the stack as the top.
    /// </summary>
    private static ResearchMechanicId? SpaceCeilingGate(ResearchMechanics mechanics, int addedCount)
    {
        List<ResearchMechanicId> unlocks = mechanics.IslandLayerUnlocks;
        List<int> order = mechanics.IslandLayersUnlockOrder;
        int originalCount = unlocks.Count - addedCount;

        int bestIndex = -1;
        for (int i = 0; i < originalCount && i < order.Count; i++)
        {
            if (bestIndex < 0 || order[i] > order[bestIndex])
            {
                bestIndex = i;
            }
        }

        return bestIndex >= 0 ? unlocks[bestIndex] : (ResearchMechanicId?)null;
    }

    private static int HighestSpaceOrder(ResearchMechanics mechanics)
    {
        List<int> order = mechanics.IslandLayersUnlockOrder;
        int highest = 0;
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] > highest)
            {
                highest = order[i];
            }
        }

        return highest;
    }

    private static ResearchMechanicId AddMechanic(
        ResearchMechanics mechanics, string id, string titleKey, string descriptionKey, string iconId)
    {
        ResearchMechanicId mechanicId = new ResearchMechanicId(id);

        // ResearchMechanic only builds from serialized data, and SerializedResearchMechanic is
        // a plain field bag - so this is the constructor the game itself uses, not a bypass.
        mechanics.Mechanics.Add(new ResearchMechanic(new SerializedResearchMechanic
        {
            Id = id,
            Title = titleKey,
            Description = descriptionKey,
            IconId = iconId,
            HideReward = false,
        }));

        return mechanicId;
    }

    /// <summary>
    /// Reuses the icon of a layer unlock the scenario already had, which is both guaranteed to
    /// resolve and already means "another layer" to anyone reading the research screen.
    /// <paramref name="skipFromEnd"/> steps over entries this mod appended, whose icon would
    /// be the answer to the question rather than a source for it.
    /// </summary>
    private static string BorrowIconId(ResearchMechanics mechanics, List<ResearchMechanicId> unlocks, int skipFromEnd)
    {
        for (int i = unlocks.Count - 1 - skipFromEnd; i >= 0; i--)
        {
            if (mechanics.TryGetMechanic(unlocks[i], out ResearchMechanic mechanic))
            {
                return mechanic.IconId.Id;
            }
        }

        return string.Empty;
    }
}
