using System;
using System.Collections.Generic;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtendedResearch;

/// <summary>
/// Appends tiers to an existing linear upgrade.
///
/// A <see cref="ResearchLinearUpgrade"/> stores its tiers as a list of
/// <see cref="ResearchLinearUpgrade.LevelData"/>, and each entry carries the value at that
/// level plus the cost and value of the <em>next</em> one. That forward reference is why
/// tiers cannot simply be appended: the entry that used to be last has to stop being last.
/// The list is therefore rebuilt from a flat (value, cost) pair per level, which is the
/// shape the game's own constructor works from anyway.
///
/// The new tiers continue vanilla's final increment rather than inventing a curve. If the
/// last vanilla step was +25, so is every step this adds - the upgrade reads as though the
/// designers had simply written more rows.
/// </summary>
public static class LinearUpgradeExtender
{
    /// <returns>How many tiers were actually added.</returns>
    public static int Extend(ResearchLinearUpgrade upgrade, int extraTiers, ExtendedResearchConfig config, ILogger logger)
    {
        if (upgrade == null || extraTiers <= 0)
        {
            return 0;
        }

        List<ResearchLinearUpgrade.LevelData> levels = upgrade._Levels;
        if (levels == null || levels.Count == 0)
        {
            logger.Error?.Log("Extended Research: linear upgrade '" + upgrade.Id.Id + "' has no levels to extend from.");
            return 0;
        }

        // Flatten to one value and one cost per level. Level 0 is free by construction, so
        // costs[i] is what it takes to reach level i.
        List<int> values = new List<int>(levels.Count + extraTiers);
        List<IResearchCost> costs = new List<IResearchCost>(levels.Count + extraTiers) { null };

        for (int i = 0; i < levels.Count; i++)
        {
            values.Add(levels[i].Value);
            if (i + 1 < levels.Count)
            {
                costs.Add(levels[i].NextCost);
            }
        }

        int step = ComputeStep(values, config);
        IResearchCost previousCost = costs.Count > 1 ? costs[costs.Count - 1] : null;

        for (int i = 0; i < extraTiers; i++)
        {
            values.Add(values[values.Count - 1] + step);
            previousCost = GrowCost(previousCost, config);
            costs.Add(previousCost);
        }

        levels.Clear();
        for (int i = 0; i < values.Count; i++)
        {
            bool isLast = i == values.Count - 1;
            levels.Add(new ResearchLinearUpgrade.LevelData(
                i,
                values[i],
                isLast ? null : costs[i + 1],
                isLast ? values[i] : values[i + 1]));
        }

        logger.Info?.Log(
            "Extended Research: '" + upgrade.Id.Id + "' +" + extraTiers + " tiers" +
            " (now " + upgrade.MaxLevel + " max, value up to " + values[values.Count - 1] + ").");

        return extraTiers;
    }

    /// <summary>
    /// The increment vanilla used for its own last step, which keeps the extension in the
    /// same units as the upgrade - percent for speeds, chunks for the chunk limit. With only
    /// one tier to look at there is no step to copy, so a tenth of the base value stands in.
    /// </summary>
    private static int ComputeStep(List<int> values, ExtendedResearchConfig config)
    {
        int step = values.Count >= 2
            ? values[values.Count - 1] - values[values.Count - 2]
            : Math.Max(1, values[values.Count - 1] / 10);

        return Math.Max(1, (int)Math.Round(step * config.ValueStepMultiplier));
    }

    /// <summary>
    /// Costs compound. Shape costs keep asking for the same shape in larger amounts, which
    /// is what vanilla does across a linear upgrade's tiers; point costs just grow.
    /// </summary>
    private static IResearchCost GrowCost(IResearchCost previous, ExtendedResearchConfig config)
    {
        if (previous is ResearchCostPoints points)
        {
            return new ResearchCostPoints(new ResearchPointCurrency(Grow(points.Amount.Amount, config.CostGrowthFactor)));
        }

        if (previous is ResearchCostShapes shapes)
        {
            return new ResearchCostShapes(shapes.ShapeHash, (ulong)Grow((long)shapes.Amount, config.CostGrowthFactor));
        }

        // Free, absent, or something added by another mod - nothing to extrapolate from.
        return new ResearchCostPoints(new ResearchPointCurrency(config.FallbackTierCost));
    }

    /// <summary>
    /// Always strictly increasing. Rounding a small amount by a factor near 1.0 can otherwise
    /// land back on the same number and produce two tiers at the same price.
    /// </summary>
    private static int Grow(long amount, float factor)
    {
        long grown = (long)Math.Round(amount * (double)factor);
        long floor = amount + 1;
        return (int)Math.Min(int.MaxValue, Math.Max(floor, grown));
    }
}
