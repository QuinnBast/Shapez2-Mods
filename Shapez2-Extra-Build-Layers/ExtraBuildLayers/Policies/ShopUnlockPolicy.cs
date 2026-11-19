using System;
using System.Collections.Generic;
using Core.Localization;
using Game.Core.Research;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Sells the layers in the research shop, <see cref="LayersPerEntry"/> at a time.
///
/// One entry per layer is unreadable past about ten - fifty chained rows in one shop tab is a
/// wall, not a progression - so the default groups them. Grouping costs nothing structurally:
/// a side upgrade takes an array of rewards, and a layer only needs *some* unlocked mechanic
/// to exist, so one purchase granting five mechanics raises the ceiling by five in one step.
/// Set <see cref="LayersPerEntry"/> to 1 to get the one-per-layer chain back.
///
/// Entries are chained: the first requires the scenario's own topmost layer of that kind, each
/// later one requires its predecessor. Without the chain the player could reach the last entry
/// first, and since the ceiling is "highest unlocked", that would hand over every layer at once.
/// </summary>
public sealed class ShopUnlockPolicy : ILayerUnlockPolicy
{
    /// How many layers one purchase grants. Clamped to at least 1.
    public int LayersPerEntry = 100;

    /// <summary>
    /// The level each machine-level purchase ends at, in order - so {3, 10, 19} sells level 3,
    /// then 4-10, then 11-19. Levels past the last end go in one final purchase. Empty falls back
    /// to <see cref="LayersPerEntry"/>.
    /// </summary>
    public int[] MachineTiers = { 3, 10, 19 };

    /// <summary>The same for space levels: {25, 250, 999} sells 3-25, 26-250, then 251-999.</summary>
    public int[] SpaceTiers = { 25, 250, 999 };

    /// Raw points for the first entry. The shop renders this times 100.
    public int MachineBaseCost = 2500;

    public int SpaceBaseCost = 4000;

    /// Each entry costs this much more than the one before it.
    public float CostGrowthFactor = 1.6f;

    /// <summary>
    /// Shop tab the entries appear under. A category name is a translation key
    /// (<c>research.category.&lt;name&gt;</c>), so a custom one needs its own translations.json
    /// entry or the research screen logs "Unknown/non-configured upgrade category" and the
    /// node has nowhere to render.
    /// </summary>
    public string Category = "ExtraBuildLayers";

    public void Present(LayerUnlockContext context)
    {
        if (context.Layers.Count == 0)
        {
            return;
        }

        if (!context.TryBorrowPreviewImage(out GameImageId imageId))
        {
            context.Logger.Error?.Log(
                "Extra Build Layers: no existing side upgrade to borrow a preview image from, so " +
                "no shop entries were added for " + context.Kind + " levels. They exist but cannot " +
                "be bought. Adding them anyway would take the research screen down.");
            return;
        }

        bool machine = context.Kind == LayerKind.Machine;
        string idPrefix = "ExtraBuildLayers.Unlock" + (machine ? "MachineLevel" : "SpaceLevel");
        string titleKey = machine ? "extra-build-layers.machine-level.title" : "extra-build-layers.space-level.title";
        string descriptionKey = machine ? "extra-build-layers.machine-level.description" : "extra-build-layers.space-level.description";
        string rangeTitleKey = machine ? "extra-build-layers.machine-level.title-range" : "extra-build-layers.space-level.title-range";
        string rangeDescriptionKey = machine ? "extra-build-layers.machine-level.description-range" : "extra-build-layers.space-level.description-range";
        int baseCost = machine ? MachineBaseCost : SpaceBaseCost;

        List<(int Start, int End)> entries = Group(context.Layers, machine ? MachineTiers : SpaceTiers, Math.Max(1, LayersPerEntry));
        ResearchMechanicId? gateMechanic = context.CeilingGate;
        ResearchUpgradeId? gateUpgrade = null;
        int entryIndex = 0;

        foreach ((int start, int end) in entries)
        {
            int count = end - start;

            ResearchMechanicId[] mechanics = new ResearchMechanicId[count];
            for (int i = 0; i < count; i++)
            {
                mechanics[i] = context.Layers[start + i].MechanicId;
            }

            int firstLevel = context.Layers[start].Level;
            int lastLevel = context.Layers[end - 1].Level;

            // RawText, not .T(), for the numbers: .T() means "this string is a translation id",
            // so a bare number bound through it renders as ?1 rather than as the number.
            IText title;
            IText description;
            if (count == 1)
            {
                title = titleKey.T().Bind("level", new RawText(lastLevel.ToString()));
                description = descriptionKey.T().Bind("level", new RawText(lastLevel.ToString()));
            }
            else
            {
                title = rangeTitleKey.T()
                    .Bind("from", new RawText(firstLevel.ToString()))
                    .Bind("to", new RawText(lastLevel.ToString()));
                description = rangeDescriptionKey.T()
                    .Bind("from", new RawText(firstLevel.ToString()))
                    .Bind("to", new RawText(lastLevel.ToString()));
            }

            gateUpgrade = context.AddShopUpgrade(
                upgradeId: idPrefix + lastLevel,
                title: title,
                description: description,
                mechanics: mechanics,
                imageId: imageId,
                category: Category,
                cost: ScaledCost(baseCost, CostGrowthFactor, entryIndex),
                // Only the first entry hangs off the scenario's own ceiling; after that each
                // one hangs off its predecessor, so they are bought in order.
                requiredMechanic: entryIndex == 0 ? gateMechanic : null,
                requiredUpgrade: entryIndex == 0 ? null : gateUpgrade);

            entryIndex++;
        }

        context.Logger.Info?.Log(
            "Extra Build Layers: " + entryIndex + " shop entrie(s) for " + context.Layers.Count +
            " " + context.Kind + " level(s), up to level " + context.Layers[context.Layers.Count - 1].Level + ".");
    }

    /// <summary>
    /// Index ranges (end exclusive) into <paramref name="layers"/>, one per purchase: cut at each
    /// tier's end level, with whatever lies past the last end as one more. With no tiers, fixed
    /// runs of <paramref name="perEntry"/>. A tier that would hold no layers - the counts were
    /// lowered, or a scenario already ships some of those levels - is simply skipped.
    /// </summary>
    private static List<(int Start, int End)> Group(IReadOnlyList<AddedLayer> layers, int[] tiers, int perEntry)
    {
        List<(int Start, int End)> entries = new List<(int Start, int End)>();
        int start = 0;
        if (tiers != null && tiers.Length > 0)
        {
            foreach (int tierEnd in tiers)
            {
                int end = start;
                while (end < layers.Count && layers[end].Level <= tierEnd)
                {
                    end++;
                }

                if (end > start)
                {
                    entries.Add((start, end));
                    start = end;
                }
            }

            if (start < layers.Count)
            {
                entries.Add((start, layers.Count));
            }

            return entries;
        }

        for (; start < layers.Count; start += perEntry)
        {
            entries.Add((start, Math.Min(start + perEntry, layers.Count)));
        }

        return entries;
    }

    private static int ScaledCost(int baseCost, float growth, int step)
    {
        double scaled = baseCost * Math.Pow(Math.Max(1.0, growth), step);
        return (int)Math.Min(int.MaxValue, Math.Max(1, Math.Round(scaled)));
    }
}

/// <summary>
/// Hands over every added layer at once, with no purchase and no research.
///
/// For sandbox and creative setups, and for testing a large layer count without grinding to
/// it. One hidden zero-cost upgrade carries all the mechanics and is unlocked on the first
/// tick of a session - see <see cref="LayerUnlockContext.GrantWithoutResearch"/> for why it
/// cannot simply be written into the unlocked set.
/// </summary>
public sealed class GrantedUnlockPolicy : ILayerUnlockPolicy
{
    /// <summary>
    /// Whether the granting upgrade shows up in the shop. Hidden by default: it costs nothing
    /// and is already unlocked, so a visible row is just a confusing freebie.
    /// </summary>
    public bool Visible;

    public string Category = "ExtraBuildLayers";

    public void Present(LayerUnlockContext context)
    {
        if (context.Layers.Count == 0)
        {
            return;
        }

        if (!context.TryBorrowPreviewImage(out GameImageId imageId))
        {
            // A hidden upgrade is never rendered, so GetImage is never called on it and the
            // empty id cannot take the research screen down. Visible is the dangerous case.
            if (Visible)
            {
                context.Logger.Error?.Log(
                    "Extra Build Layers: no preview image to borrow, so the visible granting upgrade " +
                    "for " + context.Kind + " levels was skipped rather than risk the research screen.");
                return;
            }

            imageId = GameImageId.Empty;
        }

        bool machine = context.Kind == LayerKind.Machine;
        string upgradeId = "ExtraBuildLayers.GrantAll" + (machine ? "MachineLevels" : "SpaceLevels");
        string titleKey = machine ? "extra-build-layers.machine-level.grant-all" : "extra-build-layers.space-level.grant-all";

        ResearchMechanicId[] mechanics = new ResearchMechanicId[context.Layers.Count];
        for (int i = 0; i < context.Layers.Count; i++)
        {
            mechanics[i] = context.Layers[i].MechanicId;
        }

        int top = context.Layers[context.Layers.Count - 1].Level;
        IText title = titleKey.T().Bind("level", new RawText(top.ToString()));

        ResearchUpgradeId id = context.AddShopUpgrade(
            upgradeId: upgradeId,
            title: title,
            description: title,
            mechanics: mechanics,
            imageId: imageId,
            category: Category,
            cost: 0,
            hidden: !Visible);

        context.GrantWithoutResearch(id);

        context.Logger.Info?.Log(
            "Extra Build Layers: all " + context.Layers.Count + " " + context.Kind +
            " level(s) will be granted outright, up to level " + top + ".");
    }
}

/// <summary>
/// Registers nothing. The layers exist and stay unreachable.
///
/// The right choice when another mod drives the progression itself: it can subscribe to
/// <see cref="BuildLayers.Registered"/>, read the mechanic ids off the
/// <see cref="LayerRegistration"/> and attach them to its own research wherever it likes.
/// </summary>
public sealed class NoUnlockPolicy : ILayerUnlockPolicy
{
    public void Present(LayerUnlockContext context)
    {
        context.Logger.Info?.Log(
            "Extra Build Layers: " + context.Layers.Count + " " + context.Kind +
            " level(s) registered with no unlock policy; nothing will make them reachable.");
    }
}
