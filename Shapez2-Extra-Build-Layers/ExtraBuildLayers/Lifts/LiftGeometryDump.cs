using System;
using System.Text;
using Game.Core.Content.Buildings;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Prints the connector geometry of the stock lifts and of the stretched ones beside them.
///
/// The tall lifts are registered, simulated and in front of the placer - the log confirms all
/// three - and a three-layer drag still finds nothing. That narrows it to the geometry:
/// <c>MatchingDefinitionFinder.MatchesRequirements</c> asks
/// <c>connectorData.TryGetConnector&lt;TInput&gt;(pivot, out _)</c>, and a
/// <c>LocalTilePivot</c> is a position **and** a direction, so a connector that is one tile or
/// one facing away from what the path asked for simply does not exist as far as the match is
/// concerned.
///
/// What the stock lift's connectors actually are is authored ScriptableObject data, which is
/// the one thing the decompiled assemblies do not contain. Guessing it a second time would be
/// guessing twice; this reads it out of the running game instead.
/// </summary>
internal sealed class LiftGeometryDump : IConsoleRewirer
{
    private readonly ILogger Logger;

    /// <summary>
    /// Captured by <see cref="ExtraLiftBuildings"/>, which Shifter hands the buildings. There is
    /// no static route to <c>GameBuildings</c>.
    /// </summary>
    internal static GameBuildings Buildings;

    public LiftGeometryDump(ILogger logger)
    {
        Logger = logger;
    }

    public void RegisterCommands(IDebugConsole console)
    {
        try
        {
            console.Register("extrabuildlayers.dump-lifts", context =>
            {
                string report = Dump();
                context.Output?.Invoke(report);
                Logger.Info?.Log(report);
            });
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    private string Dump()
    {
        StringBuilder report = new StringBuilder();

        try
        {
            GameBuildings buildings = Buildings;
            if (buildings == null)
            {
                return "Extra Build Layers: no buildings captured - load into a save first.";
            }

            report.AppendLine("Extra Build Layers: lift geometry");

            DescribeGroup(report, buildings, buildings.Lift1LayerDefinitionGroupId, "Lift1Layer");
            DescribeGroup(report, buildings, buildings.Lift2LayerDefinitionGroupId, "Lift2Layer");

            // Two of ours is enough to compare against; eighty would bury the interesting part.
            int shown = 0;
            foreach (IBuildingDefinition definition in ExtraLiftBuildings.Created)
            {
                Describe(report, definition);
                if (++shown >= 2)
                {
                    break;
                }
            }

            report.AppendLine("(" + ExtraLiftBuildings.Created.Count + " stretched definitions in total)");
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.AppendLine("dump failed: " + exception.Message);
        }

        return report.ToString();
    }

    private static void DescribeGroup(
        StringBuilder report, GameBuildings buildings, BuildingDefinitionGroupId groupId, string label)
    {
        IBuildingDefinitionGroup group = buildings.GetDefinitionGroup(groupId);
        if (group == null)
        {
            report.AppendLine(label + ": <missing>");
            return;
        }

        report.AppendLine(label + ": " + group.Definitions.Count + " definition(s)");
        foreach (IBuildingDefinition definition in group.Definitions)
        {
            Describe(report, definition);
        }
    }

    private static void Describe(StringBuilder report, IBuildingDefinition definition)
    {
        report.AppendLine("  " + definition.Id);

        // Deliberately both: the constructor argument and the CustomData copy the placer reads.
        // If they ever disagree the placer believes the second one, which is precisely the kind
        // of divergence this is here to catch.
#pragma warning disable 0618   // Obsolete on purpose: comparing it with the attached copy is the point.
        Describe(report, "    ConnectorData", definition.ConnectorData);
#pragma warning restore 0618

        if (definition.CustomData.TryGet(out IBuildingConnectorData attached))
        {
            Describe(report, "    CustomData   ", attached);
        }
        else
        {
            report.AppendLine("    CustomData    <no IBuildingConnectorData attached>");
        }
    }

    private static void Describe(StringBuilder report, string label, IBuildingConnectorData data)
    {
        if (data == null)
        {
            report.AppendLine(label + " <null>");
            return;
        }

        report.AppendLine(
            label + " tiles=" + data.Tiles.Length +
            " bounds=" + data.TileBounds.Min + ".." + data.TileBounds.Max +
            " dims=" + data.TileDimensions.x + "," + data.TileDimensions.y + "," + data.TileDimensions.z);

        foreach (IBuildingIO io in data.AllBuildingConnectors)
        {
            string kind = io.GetType().Name;
            if (io is BuildingBaseIO baseIO)
            {
                report.AppendLine(
                    label + "   " + kind + " pos=" + baseIO.Position_L +
                    " dir=" + baseIO.TileDirection + " pivot=" + io.Pivot());
            }
            else
            {
                report.AppendLine(label + "   " + kind + " pivot=" + io.Pivot());
            }
        }
    }
}
