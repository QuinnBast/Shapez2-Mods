using System.Collections.Generic;
using System.Text;
using Game.Core.Coordinates;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// Groups a selection's boundary ports into the notches they sit in.
///
/// A notch is one side of one chunk: four tiles wide, and repeated on each building layer,
/// so it carries up to twelve ports. That makes it - not the individual port - the unit a
/// blackbox has to preserve. One notch on the original becomes one notch on the box, with
/// each port keeping its slot and layer, so the box looks identical from the outside and the
/// belts already running into it still line up.
///
/// Mapping at any finer grain would mean inventing a correspondence the player never gave:
/// splitting one connection across two notches, or deciding which of four slots a belt ought
/// to land in. At notch granularity the player's own boundary is the specification.
///
/// The count matters for a second reason: it is what decides the size of the box. A platform
/// of w by h chunks has 2(w+h) notches, so the notch count sets a floor on the platform, and
/// the port count does not come into it at all.
/// </summary>
public class NotchGrouping
{
    /// One side of one chunk, which is what a single connection to the outside occupies.
    public struct Notch
    {
        public GlobalChunkCoordinate Chunk;
        public ChunkDirection Direction;

        public override string ToString()
        {
            return Direction + " of " + Chunk;
        }
    }

    /// A boundary port, placed within its notch.
    public struct Slot
    {
        public Notch Notch;

        /// Which of the four tiles across the notch, or -1 when the port is not on one.
        public int Index;

        /// Which building layer, so a port keeps its height as well as its position.
        public int Layer;

        public SelectionAnalysis.PortKind Kind;
        public bool Connected;
    }

    private static readonly ChunkDirection[] Sides =
    {
        ChunkDirection.East,
        ChunkDirection.South,
        ChunkDirection.West,
        ChunkDirection.North
    };

    public readonly List<Slot> Slots = new List<Slot>();

    /// The distinct notches used, in the order they were first seen.
    public readonly List<Notch> Notches = new List<Notch>();

    /// Ports that could not be placed in a notch. Worth reporting rather than hiding: it
    /// means the boundary is not shaped the way a box could reproduce.
    public int Unplaced;

    public int NotchCount
    {
        get { return Notches.Count; }
    }

    /// <summary>
    /// The smallest platform with room for these notches.
    ///
    /// A w by h platform of chunks has 2(w+h) outward-facing chunk sides, so notch capacity
    /// grows twice as fast as area does - which is why a wide boundary is no obstacle to
    /// compacting, it only makes the box a little longer.
    /// </summary>
    public void SmallestPlatform(out int width, out int height)
    {
        width = 1;
        height = 1;

        if (NotchCount <= 0)
        {
            return;
        }

        // A square reads better on the map than a long sliver, so grow the short side first
        // and only stretch once that stops being enough.
        while (2 * (width + height) < NotchCount)
        {
            if (height < width)
            {
                height++;
            }
            else
            {
                width++;
            }
        }
    }

    public static NotchGrouping Of(IEnumerable<SelectionAnalysis.Port> ports)
    {
        NotchGrouping grouping = new NotchGrouping();

        foreach (SelectionAnalysis.Port port in ports)
        {
            grouping.Add(port);
        }

        return grouping;
    }

    /// <summary>
    /// The notch a tile sits in, if it sits in one.
    ///
    /// Split out from the boundary walk because measurement needs it too: telling a factory's
    /// two ingredients apart means knowing which side of the platform each input port is on, and
    /// a notch is exactly that.
    /// </summary>
    public static bool TryNotchOf(GlobalTileCoordinate tile, out Notch notch, out int index)
    {
        GlobalChunkCoordinate chunk = tile.ToChunkCoordinate();
        GlobalChunkTransform unrotated = new GlobalChunkTransform(chunk, GridRotation.NoRotate);
        ChunkTileCoordinate local = tile.ToChunkCoordinate(in unrotated);

        foreach (ChunkDirection side in Sides)
        {
            if (!NotchDefinition.TryGetIndexOfNotchLocation_L(local, side, out index))
            {
                continue;
            }

            notch = new Notch { Chunk = chunk, Direction = side };
            return true;
        }

        notch = default(Notch);
        index = -1;
        return false;
    }

    private void Add(SelectionAnalysis.Port port)
    {
        Slot slot = new Slot
        {
            Index = -1,

            // A tile's height is measured from the bottom of the map, not the bottom of the
            // platform, so a platform on island layer 1 reports 20, 21, 22. What a notch
            // cares about is the building layer within the platform.
            Layer = ((port.Tile.z % CoordinateConstants.TilesPerIslandLayer)
                + CoordinateConstants.TilesPerIslandLayer) % CoordinateConstants.TilesPerIslandLayer,

            Kind = port.Kind,
            Connected = port.Connected
        };

        // The tile is tested against each side of its own chunk; whichever side owns a notch
        // tile at that position is the side the port faces.
        Notch found;
        int index;
        if (!TryNotchOf(port.Tile, out found, out index))
        {
            Unplaced++;
            return;
        }

        slot.Notch = found;
        slot.Index = index;

        Slots.Add(slot);

        if (!Notches.Contains(slot.Notch))
        {
            Notches.Add(slot.Notch);
        }
    }

    /// <summary>How many ports sit in a given notch, for reporting how full the boundary is.</summary>
    public int CountIn(Notch notch)
    {
        int count = 0;
        foreach (Slot slot in Slots)
        {
            if (slot.Notch.Equals(notch))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The boundary as a box would have to rebuild it: one block per notch, and within each,
    /// the slot and layer every port occupies. This is the mapping, not a summary of it.
    /// </summary>
    public string Describe()
    {
        if (NotchCount == 0)
        {
            return "No boundary ports sit in a notch.";
        }

        StringBuilder text = new StringBuilder();

        int width, height;
        SmallestPlatform(out width, out height);

        text.Append(NotchCount).Append(NotchCount == 1 ? " notch" : " notches")
            .Append(" carrying ").Append(Slots.Count)
            .Append(Slots.Count == 1 ? " port" : " ports")
            .Append(" - fits a ").Append(width).Append('x').Append(height).Append(" platform");

        foreach (Notch notch in Notches)
        {
            text.Append('\n').Append(notch);

            foreach (Slot slot in Slots)
            {
                if (!slot.Notch.Equals(notch))
                {
                    continue;
                }

                text.Append("\n    slot ").Append(slot.Index)
                    .Append(" layer ").Append(slot.Layer)
                    .Append("  ").Append(slot.Kind);

                if (!slot.Connected)
                {
                    text.Append(" (unconnected)");
                }
            }
        }

        return text.ToString();
    }
}
