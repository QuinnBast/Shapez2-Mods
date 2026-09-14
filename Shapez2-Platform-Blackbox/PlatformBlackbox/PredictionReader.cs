using System;
using System.Collections.Generic;
using System.Text;
using Game.Content.BuildingPath.Prediction;
using Game.Content.Features.Fluids;
using Game.Content.Features.Predictions;
using Game.Content.SpacePorts.Prediction;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// Reads the game's own shape predictions at the ports of a selection.
///
/// Alongside the real simulation the game runs a second one whose only job is to work out
/// which shapes can appear where. Every processing building appears in it as its
/// <c>IItemOperation</c> applied to a set of at most four possible items, and the result is
/// pushed downstream. That is the shape half of a blackbox recipe - already computed, with
/// merges and splits already resolved - so this class reads it rather than deriving it.
/// Nothing here composes operations by hand.
///
/// When the set of possibilities overflows four the game marks it
/// <c>PredictedItem.Degenerated</c>, and that is exactly the signal for a selection whose
/// output cannot be written down as a recipe. The refusal criterion comes from the game
/// rather than from a guess this mod makes.
/// </summary>
public class PredictionReader
{
    /// How a port leaves the platform. The two kinds live in different places in the
    /// prediction graph and have to be read differently, so the distinction is kept.
    public enum PortKind
    {
        /// Across space, on a space belt or pipe.
        Space,

        /// Straight into a docked neighbour, with no space belt in between.
        Docked
    }

    /// One port, and what the game thinks will come through it.
    public struct PortPrediction
    {
        public GlobalChunkCoordinate Chunk;
        public PortKind Kind;
        public bool IsInput;

        /// Whether the port crosses the selection boundary.
        public bool External;

        /// False when the prediction graph had no readable value at this port.
        public bool Readable;

        public PredictedItem Predicted;
    }

    public readonly List<PortPrediction> Inputs = new List<PortPrediction>();
    public readonly List<PortPrediction> Outputs = new List<PortPrediction>();

    public bool AnyDegenerated;
    public bool AnyUnreadable;
    public bool AnyPredicted;

    /// <summary>
    /// Boundary ports the real graph found that the prediction graph has no simulation for.
    /// An unconnected port is the ordinary reason: the port systems return null rather than
    /// a simulation when there is nothing on the other side, so it simply is not there.
    /// </summary>
    public int MissingFromPredictionGraph;

    public int PortCount
    {
        get { return Inputs.Count + Outputs.Count; }
    }

    /// <summary>
    /// Identifies the assembly this report came out of. A hot reload can silently leave the
    /// previous build in charge, and an unchanged report is then indistinguishable from a
    /// change that did nothing - so every report says which build produced it.
    /// </summary>
    private static string Build
    {
        get
        {
            System.Guid id = typeof(PredictionReader).Module.ModuleVersionId;
            return "pbx.predict build " + id.ToString("N").Substring(0, 8);
        }
    }

    private int ExternalCount
    {
        get { return CountExternal(Inputs) + CountExternal(Outputs); }
    }

    /// Ports with both ends inside the selection - hidden detail, not part of the recipe.
    private int InternalCount
    {
        get { return PortCount - ExternalCount; }
    }

    private static int CountExternal(List<PortPrediction> ports)
    {
        int count = 0;
        foreach (PortPrediction port in ports)
        {
            if (port.External)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Walks the prediction graph over the selected platforms and pairs every port with its
    /// predicted shapes.
    ///
    /// <paramref name="analysis"/> supplies the boundary for space ports. The prediction
    /// graph splits one of those in two - the building half faces inward, and a separate
    /// buffer simulation two tiles out carries the link across space - so which platform sits
    /// on the far end is easier to read on the real side. Docked ports need no such help:
    /// their simulation spans both platforms, so the selection either owns an end or does not.
    /// </summary>
    public static PredictionReader Of(IMapModel map, ISimulator predictions,
        IEnumerable<IslandModel> selection, SelectionAnalysis analysis)
    {
        PredictionReader reader = new PredictionReader();

        HashSet<IslandId> selected = new HashSet<IslandId>();
        foreach (IslandModel island in selection)
        {
            selected.Add(island.Id);
        }

        HashSet<GlobalChunkCoordinate> boundary = new HashSet<GlobalChunkCoordinate>();
        foreach (SelectionAnalysis.Port port in analysis.ExternalPorts)
        {
            boundary.Add(port.Chunk);
        }

        // A provider knows what it pushes, but a receiver keeps no readable copy - reading it
        // would consume the value and corrupt the graph. So index the selection's providers
        // by what they feed, and reach a space output's shapes through whatever feeds it.
        Dictionary<IItemPredictionReceiver, IItemPredictionProvider> feeders =
            new Dictionary<IItemPredictionReceiver, IItemPredictionProvider>();

        List<ILocalizedSimulation> ports = new List<ILocalizedSimulation>();

        foreach (ILocalizedSimulation localized in predictions.Simulations)
        {
            if (!TouchesSelection(map, selected, localized))
            {
                continue;
            }

            if (!(localized.Simulation is IItemPredictionSimulation prediction))
            {
                continue;
            }

            for (int i = 0; i < prediction.NumItemProviders; i++)
            {
                IItemPredictionProvider provider = ProviderAt(prediction, i);
                if (provider?.Next != null)
                {
                    feeders[provider.Next] = provider;
                }
            }

            // Docked ports are found by their wrapper, not their simulation: every conveyor
            // on the platform is a ForwardingPredictionSimulation too, so testing for that
            // matches the entire belt network rather than the handful of ports.
            if (prediction is SpacePortReceiverPredictionSimulation
                || prediction is SpacePortSenderPredictionSimulation
                || localized is IConnectablePort
                || localized is ConnectableBeltPortSender)
            {
                ports.Add(localized);
            }
        }

        foreach (ILocalizedSimulation localized in ports)
        {
            IItemPredictionSimulation prediction = (IItemPredictionSimulation)localized.Simulation;

            if (localized is IConnectablePort)
            {
                reader.AddDocked(map, selected, localized, prediction);
            }
            else if (localized is ConnectableBeltPortSender)
            {
                reader.AddUnconnected(localized, prediction);
            }
            else
            {
                reader.AddSpace(boundary, localized, prediction, feeders);
            }
        }

        reader.Inputs.Sort(ByChunk);
        reader.Outputs.Sort(ByChunk);

        reader.MissingFromPredictionGraph = Math.Max(0, analysis.ExternalPorts.Count - reader.ExternalCount);

        return reader;
    }

    /// <summary>
    /// A space port's own simulation sits on one platform, so the boundary has to come from
    /// the real graph. A sender has no provider of its own - the shapes it will send belong
    /// to whatever feeds it.
    /// </summary>
    private void AddSpace(HashSet<GlobalChunkCoordinate> boundary, ILocalizedSimulation localized,
        IItemPredictionSimulation prediction,
        Dictionary<IItemPredictionReceiver, IItemPredictionProvider> feeders)
    {
        // A receiver simulation takes items off a space belt and hands them to the platform,
        // so it is an input to the selection; a sender is an output.
        bool isInput = prediction is SpacePortReceiverPredictionSimulation;

        PortPrediction port = new PortPrediction
        {
            Chunk = localized.GetOccupiedChunk(0),
            Kind = PortKind.Space,
            IsInput = isInput
        };

        port.External = boundary.Contains(port.Chunk);

        if (isInput)
        {
            IItemPredictionProvider provider = ProviderAt(prediction, 0);
            if (provider != null)
            {
                port.Predicted = provider.PredictedItem;
                port.Readable = true;
            }
        }
        else
        {
            IItemPredictionReceiver receiver = ReceiverAt(prediction, 0);
            if (receiver != null && feeders.TryGetValue(receiver, out IItemPredictionProvider feeder))
            {
                port.Predicted = feeder.PredictedItem;
                port.Readable = true;
            }
        }

        Add(port);
    }

    /// <summary>
    /// A docked port is one simulation spanning both platforms - chunk 0 on the sending side,
    /// chunk 1 on the receiving side - so which end the selection owns settles both the
    /// direction and whether it crosses the boundary, with nothing inferred.
    ///
    /// Its provider and receiver are the same converter object, so the prediction reads
    /// straight off it.
    /// </summary>
    private void AddDocked(IMapModel map, HashSet<IslandId> selected,
        ILocalizedSimulation localized, IItemPredictionSimulation prediction)
    {
        bool ownsSender = Owns(map, selected, localized, 0);
        bool ownsReceiver = localized.NumOccupiedChunks > 1
            ? Owns(map, selected, localized, 1)
            : ownsSender;

        // Owning the sending end means items leave the selection here.
        bool isInput = !ownsSender;

        PortPrediction port = new PortPrediction
        {
            Chunk = localized.GetOccupiedChunk(isInput && localized.NumOccupiedChunks > 1 ? 1 : 0),
            Kind = PortKind.Docked,
            IsInput = isInput,
            External = !(ownsSender && ownsReceiver)
        };

        IItemPredictionProvider provider = ProviderAt(prediction, 0);
        if (provider != null)
        {
            port.Predicted = provider.PredictedItem;
            port.Readable = true;
        }

        Add(port);
    }

    /// <summary>
    /// A belt port building with nothing docked on the far side. The game still simulates
    /// what it would send, so the shapes are readable - and a port with no counterpart is on
    /// the boundary by definition.
    /// </summary>
    private void AddUnconnected(ILocalizedSimulation localized, IItemPredictionSimulation prediction)
    {
        PortPrediction port = new PortPrediction
        {
            Chunk = localized.GetOccupiedChunk(0),
            Kind = PortKind.Docked,
            IsInput = false,
            External = true
        };

        IItemPredictionProvider provider = ProviderAt(prediction, 0);
        if (provider != null)
        {
            port.Predicted = provider.PredictedItem;
            port.Readable = true;
        }

        Add(port);
    }

    private void Add(PortPrediction port)
    {
        // Only the boundary decides whether a recipe can be written down. An internal
        // transfer that cannot be predicted does not matter as long as the ports either
        // side of the selection can.
        if (port.External)
        {
            if (!port.Readable)
            {
                AnyUnreadable = true;
            }
            else if (port.Predicted.IsDegenerated())
            {
                AnyDegenerated = true;
            }
            else if (!port.Predicted.IsEmpty())
            {
                AnyPredicted = true;
            }
        }

        if (port.IsInput)
        {
            Inputs.Add(port);
        }
        else
        {
            Outputs.Add(port);
        }
    }

    /// <summary>
    /// A simulation can straddle two platforms, so every chunk it occupies has to be checked
    /// rather than just the first - a port feeding into the selection is anchored on the
    /// platform outside it.
    /// </summary>
    private static bool TouchesSelection(IMapModel map, HashSet<IslandId> selected,
        ILocalizedSimulation localized)
    {
        for (int i = 0; i < localized.NumOccupiedChunks; i++)
        {
            if (Owns(map, selected, localized, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Owns(IMapModel map, HashSet<IslandId> selected,
        ILocalizedSimulation localized, int index)
    {
        if (index >= localized.NumOccupiedChunks)
        {
            return false;
        }

        GlobalChunkCoordinate chunk = localized.GetOccupiedChunk(index);
        return map.TryGetIsland(chunk, out IslandModel owner) && selected.Contains(owner.Id);
    }

    /// <summary>
    /// What to feed each input of a sandbox copy of this selection.
    ///
    /// A copy sits at the same coordinates as the original, so the tile a port building
    /// occupies is what lines the two up - and the prediction graph already knows what
    /// arrives there. Every prediction simulation that touches the selection is indexed under
    /// all of its tiles, because a port spanning two platforms has an end on each and the
    /// sandbox only holds one of them.
    /// </summary>
    /// <summary>
    /// The same, over a whole prediction graph with no filtering. A sandbox holds only the
    /// thing being measured, so everything in it counts and there is no selection to test
    /// against - which also means the tiles cannot fail to line up.
    /// </summary>
    public static SandboxFeed FeedFor(ISimulator predictions)
    {
        return FeedFor(null, predictions, null);
    }

    public static SandboxFeed FeedFor(IMapModel map, ISimulator predictions,
        IEnumerable<IslandModel> selection)
    {
        SandboxFeed feed = new SandboxFeed();

        HashSet<IslandId> selected = null;
        if (selection != null && map != null)
        {
            selected = new HashSet<IslandId>();
            foreach (IslandModel island in selection)
            {
                selected.Add(island.Id);
            }
        }

        foreach (ILocalizedSimulation localized in predictions.Simulations)
        {
            if (!(localized.Simulation is IItemPredictionSimulation prediction))
            {
                continue;
            }

            if (selected != null && !Touches(map, selected, localized))
            {
                continue;
            }

            IItemPredictionProvider provider = ProviderAt(prediction, 0);
            if (provider == null)
            {
                continue;
            }

            PredictedItem predicted = provider.PredictedItem;
            if (predicted.IsEmpty() || predicted.IsDegenerated())
            {
                continue;
            }

            // A port may be predicted to carry several things; the first is enough to find a
            // ceiling, and a block whose rate depends on which of them arrives is not one
            // that reduces to a single number anyway.
            IItem first = predicted[0];

            foreach (GlobalTileCoordinate tile in SandboxFeed.TilesOf(localized))
            {
                if (first is IBeltItem item)
                {
                    feed.Items[tile] = item;
                }

                IFluid fluid = AsFluid(first);
                if (fluid != null)
                {
                    feed.Fluids[tile] = fluid;
                }
            }
        }

        feed.ResolveProbes();
        return feed;
    }

    /// A fluid reaches a port either bare or wrapped in a package, depending on the port.
    private static IFluid AsFluid(IItem item)
    {
        switch (item)
        {
            case IFluid fluid:
                return fluid;
            case FluidPackageItem package:
                return package.Fluid;
            default:
                return null;
        }
    }

    private static bool Touches(IMapModel map, HashSet<IslandId> selected,
        ILocalizedSimulation localized)
    {
        for (int i = 0; i < localized.NumOccupiedChunks; i++)
        {
            if (map.TryGetIsland(localized.GetOccupiedChunk(i), out IslandModel owner)
                && selected.Contains(owner.Id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The prediction interface's default members throw rather than return null, and a
    /// simulation only overrides the side it actually has, so both are asked defensively.
    /// </summary>
    private static IItemPredictionProvider ProviderAt(IItemPredictionSimulation prediction, int index)
    {
        if (index >= prediction.NumItemProviders)
        {
            return null;
        }

        try
        {
            return prediction.GetItemProvider(index);
        }
        catch (NotImplementedException)
        {
            return null;
        }
    }

    private static IItemPredictionReceiver ReceiverAt(IItemPredictionSimulation prediction, int index)
    {
        if (index >= prediction.NumItemReceivers)
        {
            return null;
        }

        try
        {
            return prediction.GetItemReceiver(index);
        }
        catch (NotImplementedException)
        {
            return null;
        }
    }

    /// Simulation order is not stable between runs, and this command is meant to be run
    /// twice and compared.
    private static int ByChunk(PortPrediction left, PortPrediction right)
    {
        return string.CompareOrdinal(left.Chunk.ToString(), right.Chunk.ToString());
    }

    public string Describe()
    {
        StringBuilder text = new StringBuilder();

        if (PortCount == 0)
        {
            text.Append("No ports found in the prediction graph for this selection.");
            AppendMissing(text);
            return text.ToString();
        }

        text.Append(Build).Append('\n');

        text.Append("crossing the boundary: ")
            .Append(CountExternal(Inputs)).Append(" in, ")
            .Append(CountExternal(Outputs)).Append(" out");

        if (InternalCount > 0)
        {
            text.Append(" (").Append(InternalCount).Append(" internal transfers hidden)");
        }

        Append(text, "in", Inputs);
        Append(text, "out", Outputs);

        AppendMissing(text);

        text.Append('\n').Append(Verdict());

        return text.ToString();
    }

    private void AppendMissing(StringBuilder text)
    {
        if (MissingFromPredictionGraph <= 0)
        {
            return;
        }

        text.Append('\n').Append(MissingFromPredictionGraph)
            .Append(MissingFromPredictionGraph == 1
                ? " boundary port has no prediction simulation"
                : " boundary ports have no prediction simulation")
            .Append(" - an unconnected port never gets one.");
    }

    /// <summary>
    /// Boundary ports of one direction, collapsed to one line per distinct thing carried.
    /// A twelve-painter platform has twelve identical inputs, and twelve identical lines say
    /// nothing that "12 x" does not - what is wanted here is the recipe, not an inventory.
    /// Per-port detail is what <c>pbx.ports</c> is for.
    /// </summary>
    private static void Append(StringBuilder text, string label, List<PortPrediction> ports)
    {
        List<string> order = new List<string>();
        Dictionary<string, int> counts = new Dictionary<string, int>();

        foreach (PortPrediction port in ports)
        {
            if (!port.External)
            {
                continue;
            }

            string line = (port.Kind == PortKind.Docked ? "docked  " : "space   ") + Describe(port);
            if (!counts.ContainsKey(line))
            {
                counts.Add(line, 0);
                order.Add(line);
            }

            counts[line]++;
        }

        if (order.Count == 0)
        {
            return;
        }

        text.Append("\n\n").Append(label).Append(':');

        foreach (string line in order)
        {
            text.Append("\n  ").Append(counts[line]).Append(" x  ").Append(line);
        }
    }

    private string Verdict()
    {
        if (AnyDegenerated)
        {
            return "At least one port is degenerated - the game itself cannot say which shapes\n"
                + "come out, so this selection does not reduce to a shape recipe.";
        }

        if (!AnyPredicted)
        {
            return "Nothing is predicted anywhere. Check that shape predictions are on in the\n"
                + "game's settings, and that something is actually feeding this factory.";
        }

        if (AnyUnreadable)
        {
            return "Every port that could be read has a definite shape set, but some ports had\n"
                + "no reachable prediction - worth tracing before trusting the recipe.";
        }

        return "Every port has a definite shape set - the shape half of a recipe is readable\n"
            + "straight off the prediction graph, with no operations composed by hand.";
    }

    private static string Describe(PortPrediction port)
    {
        if (!port.Readable)
        {
            return "(no prediction reachable)";
        }

        if (port.Predicted.IsEmpty())
        {
            return "(nothing predicted)";
        }

        if (port.Predicted.IsDegenerated())
        {
            return "(degenerated - too many possibilities to track)";
        }

        StringBuilder text = new StringBuilder();
        for (int i = 0; i < port.Predicted.Count; i++)
        {
            if (i != 0)
            {
                text.Append(" | ");
            }

            text.Append(Describe(port.Predicted[i]));
        }

        return text.ToString();
    }

    /// <summary>
    /// Items carry no common name, so the kinds that reach a port are named directly and
    /// anything else falls back to whatever it prints as.
    /// </summary>
    private static string Describe(IItem item)
    {
        switch (item)
        {
            case null:
                return "?";
            case ShapeItem shape:
                return shape.ToString();
            case FluidPackageItem package:
                return "fluid " + (package.Fluid?.ToString() ?? "?");
            case IFluid fluid:
                return "fluid " + fluid;
            default:
                return item.ToString();
        }
    }
}
