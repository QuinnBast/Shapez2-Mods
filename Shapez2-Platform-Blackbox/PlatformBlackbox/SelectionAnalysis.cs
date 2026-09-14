using System.Collections.Generic;
using System.Text;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// Works out what a selection of platforms looks like from the outside: how big it is, and
/// which of its ports cross the boundary.
///
/// The boundary is the whole point. Anything wired to another platform in the same selection
/// is internal detail a stand-in can hide; anything reaching outward - or left unconnected -
/// has to survive as a port on whatever replaces the selection.
///
/// Ports are classified by what kind of simulation the game gave them rather than by walking
/// the connection graph, because the graph does not answer the question. A space belt port's
/// neighbours are all on its own platform - the hop across space is carried by a separate
/// path simulation - so asking "does anything it connects to lie outside the selection" says
/// no, and the port looks internal when it is the most external thing there is.
/// </summary>
public class SelectionAnalysis
{
    /// One port that crosses out of the selection.
    public struct Port
    {
        public GlobalChunkCoordinate Chunk;

        /// The tile the port building sits on, which is what places it within a notch.
        public GlobalTileCoordinate Tile;

        public PortKind Kind;
        public bool Connected;

        public override string ToString()
        {
            return Kind + " at " + Chunk + (Connected ? "" : " (unconnected)");
        }
    }

    public enum PortKind
    {
        ItemIn,
        ItemOut,
        FluidIn,
        FluidOut
    }

    public int IslandCount;
    public int ChunkCount;
    public int BuildingCount;

    public readonly List<Port> ExternalPorts = new List<Port>();

    /// Ports joining two platforms that are both selected - hideable detail.
    public int InternalPortCount;

    public int ExternalInputs;
    public int ExternalOutputs;

    /// <summary>
    /// How the boundary groups into notches, which is what a box has to reproduce. Computed
    /// on demand, because it walks every port.
    /// </summary>
    public NotchGrouping Notches
    {
        get { return Grouping ?? (Grouping = NotchGrouping.Of(ExternalPorts)); }
    }

    private NotchGrouping Grouping;

    /// <summary>
    /// The smallest platform that could stand in for this selection.
    ///
    /// Driven by notches rather than ports: a notch is four tiles wide on each building
    /// layer, so it holds up to twelve ports, and a platform of w by h chunks offers 2(w+h)
    /// of them. A wide boundary therefore costs a longer platform, never an impossible one.
    /// </summary>
    public string SuggestedPlatform
    {
        get
        {
            int width, height;
            Notches.SmallestPlatform(out width, out height);
            return width + "x" + height;
        }
    }

    public static SelectionAnalysis Of(IMapModel map, IEnumerable<IslandModel> selection)
    {
        SelectionAnalysis analysis = new SelectionAnalysis();

        HashSet<IslandId> selected = new HashSet<IslandId>();
        foreach (IslandModel island in selection)
        {
            selected.Add(island.Id);
            analysis.IslandCount++;
            analysis.BuildingCount += island.BuildingsCount;

            foreach (GlobalChunkCoordinate chunk in island.Chunks)
            {
                analysis.ChunkCount++;
            }
        }

        if (analysis.IslandCount == 0)
        {
            return analysis;
        }

        foreach (ILocalizedSimulation localized in map.Simulator.Simulations)
        {
            // The simulator holds every port on the map, so ownership has to be established
            // before anything else. Every occupied chunk is checked rather than only the
            // first, because a docked transfer is anchored on the sending platform and would
            // otherwise be invisible from the receiving side.
            if (!Touches(localized, map, selected))
            {
                continue;
            }

            Port port;
            bool isInternal;
            if (!TryClassify(localized, map, selected, out port, out isInternal))
            {
                continue;
            }

            if (isInternal)
            {
                analysis.InternalPortCount++;
                continue;
            }

            analysis.ExternalPorts.Add(port);

            if (port.Kind == PortKind.ItemIn || port.Kind == PortKind.FluidIn)
            {
                analysis.ExternalInputs++;
            }
            else
            {
                analysis.ExternalOutputs++;
            }
        }

        return analysis;
    }

    /// <summary>
    /// Decides whether a simulation is a port of this selection, and if so which way it
    /// faces. There is no shared interface over the port simulations, so each shape the game
    /// can leave a port in is named.
    /// </summary>
    private static bool TryClassify(ILocalizedSimulation localized, IMapModel map,
        HashSet<IslandId> selected, out Port port, out bool isInternal)
    {
        port = default(Port);
        isInternal = false;

        switch (localized.Simulation)
        {
            // Space ports reach across space by construction, so they always leave the
            // platform. A space belt joining two selected platforms would be counted here as
            // external too - following the path to find out is not worth it, and keeping a
            // port that turns out to be unnecessary is the safe direction to be wrong in.
            case SpaceBeltPortReceiverSimulation _:
                return Describe(localized, PortKind.ItemIn, true, 0, out port);
            case SpaceBeltPortSenderSimulation _:
                return Describe(localized, PortKind.ItemOut, true, 0, out port);
            case SpaceFluidPortReceiverSimulation _:
                return Describe(localized, PortKind.FluidIn, true, 0, out port);
            case SpaceFluidPortSenderSimulation _:
                return Describe(localized, PortKind.FluidOut, true, 0, out port);

            // A port building whose counterpart is missing. The game still simulates what it
            // would carry, and a port with nothing on the far side is on the boundary by
            // definition.
            case BeltPortSenderBlockedSimulation _:
                return Describe(localized, PortKind.ItemOut, false, 0, out port);
            case BeltPortReceiverDisabledSimulation _:
                return Describe(localized, PortKind.ItemIn, false, 0, out port);
            case FluidPortBlockedSimulation _:
                return Describe(localized, PortKind.FluidOut, false, 0, out port);
            case FluidPortReceiverDisabledSimulation _:
                return Describe(localized, PortKind.FluidIn, false, 0, out port);

            // A docked transfer is one simulation spanning both platforms, so which end the
            // selection owns settles both the direction and whether it crosses the boundary.
            case BeltPortTransferSimulation _:
                return Docked(localized, map, selected, false, out port, out isInternal);
            case FluidPortTransferSimulation _:
                return Docked(localized, map, selected, true, out port, out isInternal);

            default:
                return false;
        }
    }

    private static bool Docked(ILocalizedSimulation localized, IMapModel map,
        HashSet<IslandId> selected, bool fluid, out Port port, out bool isInternal)
    {
        port = default(Port);
        isInternal = false;

        bool ownsSender = Owns(localized, map, selected, 0);
        bool ownsReceiver = localized.NumOccupiedChunks > 1
            ? Owns(localized, map, selected, 1)
            : ownsSender;

        if (!ownsSender && !ownsReceiver)
        {
            return false;
        }

        if (ownsSender && ownsReceiver)
        {
            isInternal = true;
            return true;
        }

        // Owning the sending end means items leave the selection here; the tile that matters
        // is the one on the side the selection owns.
        PortKind kind = ownsSender
            ? (fluid ? PortKind.FluidOut : PortKind.ItemOut)
            : (fluid ? PortKind.FluidIn : PortKind.ItemIn);

        return Describe(localized, kind, true, ownsSender ? 0 : 1, out port);
    }

    private static bool Touches(ILocalizedSimulation localized, IMapModel map,
        HashSet<IslandId> selected)
    {
        for (int i = 0; i < localized.NumOccupiedChunks; i++)
        {
            if (Owns(localized, map, selected, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Owns(ILocalizedSimulation localized, IMapModel map,
        HashSet<IslandId> selected, int index)
    {
        if (index >= localized.NumOccupiedChunks)
        {
            return false;
        }

        return map.TryGetIsland(localized.GetOccupiedChunk(index), out IslandModel owner)
            && selected.Contains(owner.Id);
    }

    private static bool Describe(ILocalizedSimulation localized, PortKind kind, bool connected,
        int end, out Port port)
    {
        port = default(Port);

        if (localized.NumOccupiedChunks <= end)
        {
            return false;
        }

        port.Chunk = localized.GetOccupiedChunk(end);
        port.Kind = kind;
        port.Connected = connected;

        if (localized is ILocalizedTileSimulation located && located.NumOccupiedTiles > end)
        {
            port.Tile = located.GetOccupiedTile(end);
        }

        return true;
    }

    public string Describe()
    {
        if (IslandCount == 0)
        {
            return "Nothing selected. Select platforms in space view first.";
        }

        StringBuilder text = new StringBuilder();
        text.Append(IslandCount).Append(IslandCount == 1 ? " platform, " : " platforms, ");
        text.Append(ChunkCount).Append(" chunks, ");
        text.Append(BuildingCount).Append(" buildings");

        text.Append("\ncrossing the boundary: ")
            .Append(ExternalInputs).Append(" in, ")
            .Append(ExternalOutputs).Append(" out");

        if (InternalPortCount > 0)
        {
            text.Append(" (").Append(InternalPortCount).Append(" internal ports would be hidden)");
        }

        text.Append("\nacross ").Append(Notches.NotchCount)
            .Append(Notches.NotchCount == 1 ? " notch" : " notches");

        if (Notches.Unplaced > 0)
        {
            text.Append(" (").Append(Notches.Unplaced).Append(" not on a notch)");
        }

        text.Append("\nsmallest platform with room for those notches: ").Append(SuggestedPlatform);

        return text.ToString();
    }
}
