using System;
using System.Collections.Generic;
using System.Text;
using Game.Core.Blueprint;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// What a blueprint looks like from the outside: which notches it uses, which way each faces,
/// and therefore how big a platform has to be to stand in for it.
///
/// Derived without running anything. A blueprint is expanded into a private layout, the
/// simulation systems wire it up, and the ports they leave dangling are the boundary - the
/// same classification the live map gets, minus the ownership question, because everything in
/// a sandbox belongs to the blueprint by construction.
///
/// This is the half of a blackbox that does *not* depend on what the factory is fed. The
/// shapes and rates do, so they are measured later, once a placed box knows what is arriving.
/// </summary>
public class BlueprintBoundary
{
    public readonly NotchGrouping Notches;

    public int Islands;
    public int Buildings;

    public int ItemInputs;
    public int ItemOutputs;
    public int FluidInputs;
    public int FluidOutputs;

    private BlueprintBoundary(NotchGrouping notches)
    {
        Notches = notches;
    }

    /// <summary>
    /// Expands the blueprint far enough to see its ports, and no further. No ticking, so this
    /// is cheap enough to run on a keypress.
    /// </summary>
    public static bool TryOf(GameSessionOrchestrator orchestrator, IslandBlueprint blueprint,
        ILogger logger, out BlueprintBoundary boundary, out string failure)
    {
        boundary = null;

        BlackboxSandbox sandbox;
        if (!BlackboxSandbox.TryBuild(orchestrator, blueprint, logger, out sandbox, out failure))
        {
            return false;
        }

        using (sandbox)
        {
            try
            {
                List<SelectionAnalysis.Port> ports = new List<SelectionAnalysis.Port>();

                foreach (ILocalizedSimulation localized in sandbox.Simulations.Simulations)
                {
                    SelectionAnalysis.Port port;
                    if (Classify(localized, out port))
                    {
                        ports.Add(port);
                    }
                }

                boundary = new BlueprintBoundary(NotchGrouping.Of(ports))
                {
                    Islands = sandbox.IslandCount,
                    Buildings = sandbox.BuildingCount
                };

                boundary.Count(ports);
                return true;
            }
            catch (Exception exception)
            {
                logger.Exception?.LogException(exception);
                failure = "Could not read the blueprint's boundary: " + exception.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// A port of the blueprint, or not a port at all.
    ///
    /// Everything here is external: a sandbox holds only the blueprint, so a port with no
    /// counterpart is a port that reached outside. A transfer simulation - two platforms of
    /// the blueprint docked to each other - is internal detail and skipped.
    /// </summary>
    private static bool Classify(ILocalizedSimulation localized, out SelectionAnalysis.Port port)
    {
        port = default(SelectionAnalysis.Port);

        SelectionAnalysis.PortKind kind;
        switch (localized.Simulation)
        {
            case BeltPortReceiverDisabledSimulation _:
            case SpaceBeltPortReceiverSimulation _:
                kind = SelectionAnalysis.PortKind.ItemIn;
                break;

            case BeltPortSenderBlockedSimulation _:
            case SpaceBeltPortSenderSimulation _:
                kind = SelectionAnalysis.PortKind.ItemOut;
                break;

            case FluidPortReceiverDisabledSimulation _:
            case SpaceFluidPortReceiverSimulation _:
                kind = SelectionAnalysis.PortKind.FluidIn;
                break;

            case FluidPortBlockedSimulation _:
            case SpaceFluidPortSenderSimulation _:
                kind = SelectionAnalysis.PortKind.FluidOut;
                break;

            default:
                return false;
        }

        if (localized.NumOccupiedChunks == 0)
        {
            return false;
        }

        port.Chunk = localized.GetOccupiedChunk(0);
        port.Kind = kind;
        port.Connected = false;

        if (localized is ILocalizedTileSimulation located && located.NumOccupiedTiles > 0)
        {
            port.Tile = located.GetOccupiedTile(0);
        }

        return true;
    }

    private void Count(List<SelectionAnalysis.Port> ports)
    {
        foreach (SelectionAnalysis.Port port in ports)
        {
            switch (port.Kind)
            {
                case SelectionAnalysis.PortKind.ItemIn:
                    ItemInputs++;
                    break;
                case SelectionAnalysis.PortKind.ItemOut:
                    ItemOutputs++;
                    break;
                case SelectionAnalysis.PortKind.FluidIn:
                    FluidInputs++;
                    break;
                default:
                    FluidOutputs++;
                    break;
            }
        }
    }

    /// <summary>
    /// The notches a box would need, grouped by which way they face - because that, not the
    /// port count, is what a stand-in has to reproduce.
    /// </summary>
    public string Describe()
    {
        StringBuilder text = new StringBuilder();

        int width, height;
        Notches.SmallestPlatform(out width, out height);

        text.Append(Islands).Append(Islands == 1 ? " platform, " : " platforms, ")
            .Append(Buildings).Append(" buildings");

        text.Append("\nboundary: ").Append(ItemInputs).Append(" item in, ")
            .Append(ItemOutputs).Append(" item out");

        if (FluidInputs > 0 || FluidOutputs > 0)
        {
            text.Append(", ").Append(FluidInputs).Append(" fluid in, ")
                .Append(FluidOutputs).Append(" fluid out");
        }

        text.Append("\nacross ").Append(Notches.NotchCount)
            .Append(Notches.NotchCount == 1 ? " notch" : " notches")
            .Append(" - a ").Append(width).Append('x').Append(height)
            .Append(" platform has room for that");

        foreach (NotchGrouping.Notch notch in Notches.Notches)
        {
            text.Append("\n  ").Append(notch.Direction).Append("  ")
                .Append(Notches.CountIn(notch)).Append(" ports");
        }

        return text.ToString();
    }
}
