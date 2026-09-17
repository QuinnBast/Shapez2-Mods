using System;
using System.Collections.Generic;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using ShapezShifter.Kit;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// The redstone simulation: every component on the map, and the power field over them.
///
/// ## Why this is not a shapez simulation
///
/// Shapez simulations are built out of lanes and connectors - a machine takes items in at one
/// pivot and hands them out at another - and redstone is not that shape. It is a field over a
/// grid, recomputed from its sources, where a component's output depends on neighbours it has no
/// connector to. So the components are registered as ordinary buildings with an inert
/// simulation, exactly like the decoration blocks, and this owns the behaviour instead.
///
/// That also keeps the whole thing on the **main thread**. `Simulator.StartAsynchronousUpdate`
/// returns a Task, so registered simulations run on pool threads; this is driven from
/// `ITickRewirer`, which postfixes `GameSessionOrchestrator.Tick`, and the main thread is where
/// reading the map and touching Unity objects is safe.
///
/// ## The tick
///
/// One redstone tick is 0.1 seconds, as in Minecraft, accumulated from frame delta rather than
/// counted in frames so the rate does not follow the frame rate.
///
/// Each tick recomputes the entire field from scratch rather than propagating changes. Minecraft
/// propagates, and its famous quirks - quasi-connectivity, zero-tick pulses, update order
/// dependence - are artefacts of *how* it propagates. Recomputing gives the same answer for
/// every circuit whose behaviour a player would call intended, is trivially cheap at the
/// hundreds of components a decorated platform will hold, and cannot desynchronise. What it does
/// not reproduce is the artefacts; see DESIGN.md.
///
/// Delay is modelled explicitly rather than falling out of propagation order: a torch reads the
/// *previous* tick's block power, and a repeater shifts its input through a queue. Those are the
/// two places Minecraft's own delays come from, so the circuits that depend on them - clocks,
/// pulse shapers, latches - still work.
internal sealed class RedstoneWorld
{
    /// Minecraft's tick. Two game ticks at 20Hz.
    public const float TickSeconds = 0.1f;

    public const int MaxPower = 15;

    /// How long a button stays pressed. Stone buttons are ten redstone ticks in Minecraft.
    private const int ButtonTicks = 10;

    private readonly Dictionary<GlobalTileCoordinate, RedstoneComponent> Components =
        new Dictionary<GlobalTileCoordinate, RedstoneComponent>();

    /// Solid blocks - this mod's decoration blocks - and whether each is powered. A block is
    /// what a wall torch inverts and what a lever is mounted on, so the field has to include
    /// them even though they are not components.
    private readonly Dictionary<GlobalTileCoordinate, bool> PoweredBlocks =
        new Dictionary<GlobalTileCoordinate, bool>();

    /// Blocks of redstone, which are sources in their own right and are never cleared by the
    /// solve. Kept as a set rather than looked up through the map each tick: the solve touches
    /// every block every tick, and a map query per block per tick is the one place this could
    /// get expensive.
    private readonly HashSet<GlobalTileCoordinate> RedstoneBlocks =
        new HashSet<GlobalTileCoordinate>();

    /// Blocks under *strong* power, which is the distinction that stops redstone latching itself
    /// on forever.
    ///
    /// Minecraft splits block power in two and it is not a detail. A block **strongly** powered -
    /// by a repeater facing it, a torch beneath it, a lever attached to it - powers dust beside
    /// it. A block **weakly** powered, which is what dust lying on it does, does not. Collapse
    /// the two and you get a loop with no source in it: dust powers the block, the block powers
    /// the dust, and the circuit stays lit after the lever is switched off, the torch is taken
    /// away, and the dust's last actual source is deleted.
    ///
    /// Weak power still exists - it is what turns a torch off - so both sets are kept.
    private readonly HashSet<GlobalTileCoordinate> StronglyPowered =
        new HashSet<GlobalTileCoordinate>();

    private float Accumulated;

    public static RedstoneWorld Instance { get; } = new RedstoneWorld();

    public int ComponentCount => Components.Count;

    public bool TryGet(GlobalTileCoordinate tile, out RedstoneComponent component)
    {
        return Components.TryGetValue(tile, out component);
    }

    public void Register(GlobalTileCoordinate tile, RedstoneDefinition definition, GridRotation rotation)
    {
        // The indexer, not Add. A building can be registered twice if the map replays its
        // contents - re-entering a session does exactly that - and a duplicate key would take
        // the tick down rather than the placement.
        Components[tile] = new RedstoneComponent(definition, rotation);
    }

    public void Unregister(GlobalTileCoordinate tile)
    {
        Components.Remove(tile);
    }

    public void RegisterBlock(GlobalTileCoordinate tile, bool isRedstoneBlock)
    {
        PoweredBlocks[tile] = isRedstoneBlock;

        if (isRedstoneBlock)
        {
            RedstoneBlocks.Add(tile);
        }
    }

    public void UnregisterBlock(GlobalTileCoordinate tile)
    {
        PoweredBlocks.Remove(tile);
        RedstoneBlocks.Remove(tile);
    }

    public void Clear()
    {
        Components.Clear();
        PoweredBlocks.Clear();
        RedstoneBlocks.Clear();
        StronglyPowered.Clear();
        Accumulated = 0.0f;
    }

    /// Driven from ITickRewirer. Runs whole ticks only, and catches up at most a few of them, so
    /// that a frame hitch does not turn into a burst of clock edges.
    public void Advance(float deltaTime)
    {
        if (Components.Count == 0)
        {
            Accumulated = 0.0f;
            return;
        }

        Accumulated += deltaTime;

        int budget = 4;
        while (Accumulated >= TickSeconds && budget-- > 0)
        {
            Accumulated -= TickSeconds;
            Tick();
        }

        if (Accumulated > TickSeconds)
        {
            Accumulated = 0.0f;
        }
    }

    private void Tick()
    {
        AdvanceTimers();
        SolvePower();
        SampleDelays();
    }

    /// Buttons letting go, and repeaters shifting their queue along, both happen before the
    /// field is solved so that this tick sees the new outputs.
    private void AdvanceTimers()
    {
        foreach (RedstoneComponent component in Components.Values)
        {
            if (component.Definition.Kind == RedstoneKind.Button && component.ButtonTicksLeft > 0)
            {
                component.ButtonTicksLeft--;
            }

            if (component.PulseTicksLeft > 0)
            {
                component.PulseTicksLeft--;
            }

            if (component.Definition.Kind == RedstoneKind.Repeater
                || component.Definition.Kind == RedstoneKind.Comparator)
            {
                component.Output = component.Queue.Shift();
            }
        }
    }

    /// Which torches have a block behind them.
    ///
    /// Settled at the top of the tick because both SolveBlocks and DustNeighbours branch on it,
    /// and it depends only on whether a block exists there - not on any power. Computing it at the
    /// end, as the first version did, left both of them reading the previous tick's answer for one
    /// tick after a torch was placed.
    private void SolveMounts()
    {
        foreach (KeyValuePair<GlobalTileCoordinate, RedstoneComponent> entry in Components)
        {
            if (entry.Value.Definition.Kind == RedstoneKind.Torch)
            {
                entry.Value.Mounted = PoweredBlocks.ContainsKey(Behind(entry.Key, entry.Value));
            }
        }
    }

    /// The whole field, from sources outward.
    private void SolvePower()
    {
        SolveMounts();

        // Torches read the block power computed on the *previous* tick. That one line is where a
        // torch's one-tick delay comes from, and with it every clock a player builds out of an
        // odd loop of torches.
        foreach (RedstoneComponent component in Components.Values)
        {
            component.Power = 0;
        }

        Queue<GlobalTileCoordinate> frontier = new Queue<GlobalTileCoordinate>();

        foreach (KeyValuePair<GlobalTileCoordinate, RedstoneComponent> entry in Components)
        {
            int emitted = SourceStrength(entry.Key, entry.Value);

            if (emitted <= 0)
            {
                continue;
            }

            entry.Value.Power = emitted;

            if (entry.Value.Definition.Kind == RedstoneKind.Dust)
            {
                frontier.Enqueue(entry.Key);
            }
            else
            {
                // A source that is not itself dust seeds the dust beside it at full strength.
                // Dust adjacent to a lever reads fifteen, not fourteen.
                foreach (GlobalTileCoordinate neighbour in DustNeighbours(entry.Key, entry.Value))
                {
                    if (Components.TryGetValue(neighbour, out RedstoneComponent next)
                        && next.Definition.Kind == RedstoneKind.Dust
                        && next.Power < emitted)
                    {
                        next.Power = emitted;
                        frontier.Enqueue(neighbour);
                    }
                }
            }
        }

        // Breadth first, losing one strength per tile. Because the frontier is ordered by
        // strength - everything at fifteen is enqueued before anything at fourteen can be - the
        // first value written to a tile is its final one, which is what makes one pass enough.
        while (frontier.Count > 0)
        {
            GlobalTileCoordinate tile = frontier.Dequeue();
            RedstoneComponent component = Components[tile];
            int next = component.Power - 1;

            if (next <= 0)
            {
                continue;
            }

            foreach (GlobalTileCoordinate neighbour in DustNeighbours(tile, component))
            {
                if (Components.TryGetValue(neighbour, out RedstoneComponent other)
                    && other.Definition.Kind == RedstoneKind.Dust
                    && other.Power < next)
                {
                    other.Power = next;
                    frontier.Enqueue(neighbour);
                }
            }
        }

        SolveBlocks();
        SolveLamps();
        ExchangeWithConverters();
        SolveTorches();
    }

    /// Which solid blocks are powered, for the torches to invert next tick.
    ///
    /// Simplified deliberately. Minecraft distinguishes *strong* power (a block hard-powered by a
    /// repeater or torch beneath, which can then power dust beside it) from *weak* power (a block
    /// lit by dust, which cannot). Modelling both doubles the rules for behaviour that only shows
    /// up in compact circuits. Here a block is powered when something adjacent is driving it, and
    /// that is enough for the case this exists to serve: lever into block, torch on block,
    /// inverted output.
    private void SolveBlocks()
    {
        List<GlobalTileCoordinate> blocks = new List<GlobalTileCoordinate>(PoweredBlocks.Keys);
        StronglyPowered.Clear();

        foreach (GlobalTileCoordinate block in blocks)
        {
            if (RedstoneBlocks.Contains(block))
            {
                PoweredBlocks[block] = true;
                StronglyPowered.Add(block);
                continue;
            }

            bool weak = false;
            bool strong = false;

            foreach (GlobalTileCoordinate neighbour in Around(block))
            {
                if (!Components.TryGetValue(neighbour, out RedstoneComponent component)
                    || component.Power <= 0)
                {
                    continue;
                }

                switch (component.Definition.Kind)
                {
                    case RedstoneKind.Dust:
                        // Weak only. This is the whole fix: dust lights the block it lies on and
                        // the blocks it touches, and that light cannot come back out into dust.
                        weak = true;
                        break;

                    case RedstoneKind.Converter:
                        // Strong, like a lever: a converter driven by a wire is a source in its
                        // own right, not something merely lit by dust.
                        strong = true;
                        weak = true;
                        break;

                    case RedstoneKind.Lever:
                    case RedstoneKind.Button:
                        // Strong, but only into what it is mounted on - the block behind it or
                        // the one beneath it - rather than into all six neighbours.
                        strong |= Behind(neighbour, component) == block || Below(neighbour) == block;
                        weak = true;
                        break;

                    case RedstoneKind.Repeater:
                    case RedstoneKind.Comparator:
                        // Strong, and only into what it faces. That is what makes it a diode.
                        strong |= Facing(neighbour, component) == block;
                        weak |= Facing(neighbour, component) == block;
                        break;

                    case RedstoneKind.Observer:
                        // Out of the back, which is the end without the face on it.
                        strong |= Behind(neighbour, component) == block;
                        weak |= Behind(neighbour, component) == block;
                        break;

                    case RedstoneKind.Torch:
                        // Only a free-standing torch powers the block above it.
                        //
                        // A wall torch does not, and that is not a simplification - it is what
                        // stops it oscillating. In Minecraft a wall torch shares a block space
                        // with the block it is attached to; here it occupies a whole tile of its
                        // own, so "the block above the torch" is a tile Minecraft has no
                        // equivalent of. Powering it lets a wall torch light a block, that block
                        // light dust, that dust run back down a layer and weakly power the very
                        // block the torch is mounted on - which switches the torch off, which
                        // unpowers everything, which switches it back on. A flicker with no clock
                        // in it and nothing on screen to explain it.
                        //
                        // Never into the block it is mounted on either, or every wall torch would
                        // hold itself off directly.
                        strong |= !component.Mounted && Above(neighbour) == block;
                        weak |= !component.Mounted && Above(neighbour) == block;
                        break;
                }
            }

            PoweredBlocks[block] = weak || strong;

            if (strong)
            {
                StronglyPowered.Add(block);
            }
        }
    }

    /// Lamps, which read the field but never feed it.
    ///
    /// Run after the dust has settled and after the blocks, so a lamp sees this tick's answer
    /// rather than last tick's - a lamp has no delay in Minecraft and should not gain one here.
    private void SolveLamps()
    {
        foreach (KeyValuePair<GlobalTileCoordinate, RedstoneComponent> entry in Components)
        {
            if (entry.Value.Definition.Kind != RedstoneKind.Lamp)
            {
                continue;
            }

            bool lit = false;

            foreach (GlobalTileCoordinate neighbour in Around(entry.Key))
            {
                if (StronglyPowered.Contains(neighbour))
                {
                    lit = true;
                    break;
                }

                if (!Components.TryGetValue(neighbour, out RedstoneComponent other)
                    || other.Power <= 0)
                {
                    continue;
                }

                switch (other.Definition.Kind)
                {
                    case RedstoneKind.Repeater:
                    case RedstoneKind.Comparator:
                        lit |= Facing(neighbour, other) == entry.Key;
                        break;

                    case RedstoneKind.Observer:
                        lit |= Behind(neighbour, other) == entry.Key;
                        break;

                    case RedstoneKind.Lamp:
                        break;

                    default:
                        lit = true;
                        break;
                }

                if (lit)
                {
                    break;
                }
            }

            entry.Value.Lit = lit;
        }
    }

    /// Reads redstone into the converters and writes their wire reading back out.
    ///
    /// Both halves happen here, on the main thread, because this is the side that knows where
    /// everything is - a shapez simulation is handed no position, so it could never find its own
    /// tile in this dictionary. `TryFindTileSimulation` resolves the other way round, from a tile
    /// to the simulation sitting on it, which is why the traffic runs in this direction.
    ///
    /// The two fields exchanged are `volatile bool`s and nothing else crosses: the simulation's
    /// own Update runs on a pool thread.
    private void ExchangeWithConverters()
    {
        IMapModel map = GameHelper.Core?.LocalPlayer?.CurrentMap;

        if (map == null)
        {
            return;
        }

        foreach (KeyValuePair<GlobalTileCoordinate, RedstoneComponent> entry in Components)
        {
            if (entry.Value.Definition.Kind != RedstoneKind.Converter)
            {
                continue;
            }

            if (!map.Simulator.TryFindTileSimulation(entry.Key, out ILocalizedTileSimulation located)
                || !(located.Simulation is RedstoneConverterSimulation converter))
            {
                continue;
            }

            // Redstone reaching the converter's tile, by the same rule a lamp lights by.
            bool powered = false;

            foreach (GlobalTileCoordinate neighbour in Around(entry.Key))
            {
                if (StronglyPowered.Contains(neighbour))
                {
                    powered = true;
                    break;
                }

                if (!Components.TryGetValue(neighbour, out RedstoneComponent other)
                    || other.Power <= 0)
                {
                    continue;
                }

                switch (other.Definition.Kind)
                {
                    case RedstoneKind.Repeater:
                    case RedstoneKind.Comparator:
                        powered |= Facing(neighbour, other) == entry.Key;
                        break;

                    case RedstoneKind.Observer:
                        powered |= Behind(neighbour, other) == entry.Key;
                        break;

                    case RedstoneKind.Lamp:
                        break;

                    default:
                        powered = true;
                        break;
                }

                if (powered)
                {
                    break;
                }
            }

            converter.RedstoneIsOn = powered;
            entry.Value.Lit = powered;
            entry.Value.WireIsOn = converter.WireIsOn;
        }
    }

    /// A comparator: compare mode passes the back through unless a side beats it, subtract mode
    /// takes the strongest side off the back. Unlike a repeater it is **strength preserving** -
    /// its whole purpose is arithmetic on the number, so its output is the computed value rather
    /// than a flat fifteen.
    ///
    /// Sampled at the end of the tick and released at the start of the next, exactly like a
    /// repeater, which is where its one-tick delay comes from.
    private void SampleComparator(GlobalTileCoordinate tile, RedstoneComponent component)
    {
        int back = StrengthInto(Behind(tile, component), tile);
        int sides = SideStrength(tile, component);

        int value = component.Subtracting
            ? Math.Max(0, back - sides)
            : sides > back ? 0 : back;

        component.Queue.Push(value, 1);
    }

    /// An observer: pulse out of the back for one tick whenever the tile it faces changes.
    ///
    /// "Changes" is a signature rather than an event, because the things worth noticing are not
    /// all map events - a block being placed or broken is, but dust changing strength and a lamp
    /// lighting are not. Hashing what is there each tick catches all of them with one rule.
    private void SampleObserver(GlobalTileCoordinate tile, RedstoneComponent component)
    {
        int signature = Signature(Facing(tile, component));

        if (component.LastSignature != int.MinValue && signature != component.LastSignature)
        {
            component.PulseTicksLeft = 2;
        }

        component.LastSignature = signature;
    }

    private int Signature(GlobalTileCoordinate tile)
    {
        if (Components.TryGetValue(tile, out RedstoneComponent component))
        {
            return ((int)component.Definition.Kind + 1) * 1000
                   + component.Power * 4
                   + (component.Lit ? 2 : 0)
                   + (component.IsActive ? 1 : 0);
        }

        if (PoweredBlocks.TryGetValue(tile, out bool powered))
        {
            return powered ? 2 : 1;
        }

        return 0;
    }

    /// How much signal the thing at `from` delivers into `to`. Zero unless it is actually aimed
    /// that way: a repeater beside a comparator is not an input to it.
    /// The strongest of a component's two side inputs, which is what both comparator modes use.
    private int SideStrength(GlobalTileCoordinate tile, RedstoneComponent component)
    {
        int strongest = 0;

        foreach (GlobalTileCoordinate side in Sides(tile, component))
        {
            strongest = Math.Max(strongest, StrengthInto(side, tile));
        }

        return strongest;
    }

    private int StrengthInto(GlobalTileCoordinate from, GlobalTileCoordinate to)
    {
        if (Components.TryGetValue(from, out RedstoneComponent component))
        {
            switch (component.Definition.Kind)
            {
                case RedstoneKind.Repeater:
                case RedstoneKind.Comparator:
                    return Facing(from, component) == to ? component.Power : 0;

                case RedstoneKind.Observer:
                    return Behind(from, component) == to ? component.Power : 0;

                case RedstoneKind.Lamp:
                    return 0;

                default:
                    return component.Power;
            }
        }

        return StronglyPowered.Contains(from) ? MaxPower
            : PoweredBlocks.TryGetValue(from, out bool powered) && powered ? MaxPower
            : 0;
    }

    /// A torch's state for the *next* tick, which is what gives it its delay.
    private void SolveTorches()
    {
        foreach (KeyValuePair<GlobalTileCoordinate, RedstoneComponent> entry in Components)
        {
            if (entry.Value.Definition.Kind != RedstoneKind.Torch)
            {
                continue;
            }

            GlobalTileCoordinate mount = Behind(entry.Key, entry.Value);

            // The rule this mod uses, which is a simplification of Minecraft's: a torch with a
            // block behind it is mounted on that block and inverts it; a torch standing free is
            // a source. Minecraft has no free-standing torch - a floor torch is mounted on the
            // floor and inverts *that* - so this differs, and it differs in the direction that
            // makes a torch usable as a battery.
            entry.Value.TorchLitNextTick =
                !entry.Value.Mounted || !PoweredBlocks[mount];
        }
    }

    /// Repeaters sample their input at the end of the tick, after the field has settled.
    private void SampleDelays()
    {
        foreach (KeyValuePair<GlobalTileCoordinate, RedstoneComponent> entry in Components)
        {
            RedstoneComponent component = entry.Value;

            if (component.Definition.Kind == RedstoneKind.Comparator)
            {
                SampleComparator(entry.Key, component);
                continue;
            }

            if (component.Definition.Kind == RedstoneKind.Observer)
            {
                SampleObserver(entry.Key, component);
                continue;
            }

            if (component.Definition.Kind != RedstoneKind.Repeater)
            {
                continue;
            }

            // Locked by a powered repeater pointing into its side, exactly as in Minecraft: a
            // locked repeater holds whatever it was last outputting.
            if (IsLocked(entry.Key, component))
            {
                component.Queue.Hold();
                continue;
            }

            GlobalTileCoordinate input = Behind(entry.Key, component);
            bool powered = false;

            if (Components.TryGetValue(input, out RedstoneComponent source))
            {
                powered = source.Power > 0
                          && (source.Definition.Kind != RedstoneKind.Repeater
                              || Facing(input, source) == entry.Key);
            }
            else if (PoweredBlocks.TryGetValue(input, out bool blockPowered))
            {
                powered = blockPowered;
            }

            component.Queue.Push(powered ? MaxPower : 0, component.RepeaterDelay);
        }
    }

    private bool IsLocked(GlobalTileCoordinate tile, RedstoneComponent component)
    {
        foreach (GlobalTileCoordinate side in Sides(tile, component))
        {
            if (Components.TryGetValue(side, out RedstoneComponent other)
                && other.Definition.Kind == RedstoneKind.Repeater
                && other.Power > 0
                && Facing(side, other) == tile)
            {
                return true;
            }
        }

        return false;
    }

    /// What a component puts out this tick, before dust spreads it.
    private int SourceStrength(GlobalTileCoordinate tile, RedstoneComponent component)
    {
        switch (component.Definition.Kind)
        {
            case RedstoneKind.Lever:
                return component.LeverOn ? MaxPower : 0;

            case RedstoneKind.Button:
                return component.ButtonTicksLeft > 0 ? MaxPower : 0;

            case RedstoneKind.Torch:
                return component.TorchLitNextTick ? MaxPower : 0;

            case RedstoneKind.Repeater:
            case RedstoneKind.Comparator:
                return component.Output;

            case RedstoneKind.Observer:
                return component.PulseTicksLeft > 0 ? MaxPower : 0;

            case RedstoneKind.Lamp:
                // A dead end. It lights and hands nothing on.
                return 0;

            case RedstoneKind.Converter:
                // Full strength whenever the wire feeding it is anything but Off, which is the
                // rule the building exists to implement.
                return component.WireIsOn ? MaxPower : 0;

            default:
                // Dust is not a source of its own. It is lit by a **strongly** powered block it
                // touches - a lever's block, a repeater's target, a torch's ceiling - which is
                // how a lever-into-block-into-dust circuit carries. Reading PoweredBlocks here
                // instead would include the weak power that dust itself applies, and the dust
                // would hold itself on forever.
                foreach (GlobalTileCoordinate neighbour in Around(tile))
                {
                    if (StronglyPowered.Contains(neighbour))
                    {
                        return MaxPower;
                    }
                }

                return 0;
        }
    }

    /// What `db.redstone` prints: the world's size, and everything about the tile under the
    /// cursor.
    ///
    /// Added after a bug that took far longer to find than it should have - dust latching itself
    /// on through the block beside it - because from outside the game there was no way to see
    /// whether the dust thought it had a source, and if so which one.
    public string Describe(GlobalTileCoordinate tile)
    {
        System.Text.StringBuilder report = new System.Text.StringBuilder();
        report.AppendLine(
            "components: " + Components.Count + ", blocks: " + PoweredBlocks.Count
            + " (" + RedstoneBlocks.Count + " of redstone), strongly powered: " + StronglyPowered.Count);
        report.AppendLine("tile " + tile + ":");

        if (Components.TryGetValue(tile, out RedstoneComponent component))
        {
            report.AppendLine(
                "  " + component.Definition.DisplayName + "  power " + component.Power
                + (component.Definition.Kind == RedstoneKind.Dust
                    ? "  mask " + ConnectionMask(tile)
                    : string.Empty)
                + (component.Definition.Kind == RedstoneKind.Lever
                    ? "  lever " + (component.LeverOn ? "on" : "off")
                    : string.Empty)
                + (component.Definition.Kind == RedstoneKind.Torch
                    ? "  lit " + component.TorchLitNextTick + "  mounted on " + Behind(tile, component)
                    : string.Empty)
                + (component.Definition.Kind == RedstoneKind.Repeater
                    ? "  delay " + component.RepeaterDelay + "  out " + component.Output
                    : string.Empty)
                + (component.Definition.Kind == RedstoneKind.Comparator
                    ? "  " + (component.Subtracting ? "subtract" : "compare")
                      + "  back " + StrengthInto(Behind(tile, component), tile)
                      + " (from " + Behind(tile, component) + ")"
                      + "  sides " + SideStrength(tile, component)
                      + "  out " + component.Output
                      + " -> " + Facing(tile, component)
                    : string.Empty)
                + (component.Definition.Kind == RedstoneKind.Lamp
                    ? "  lit " + component.Lit
                    : string.Empty)
                + (component.Definition.Kind == RedstoneKind.Observer
                    ? "  watching " + Facing(tile, component)
                      + " (signature " + component.LastSignature + ")"
                      + "  pulse " + component.PulseTicksLeft
                      + "  out to " + Behind(tile, component)
                    : string.Empty));
        }
        else if (PoweredBlocks.TryGetValue(tile, out bool powered))
        {
            report.AppendLine(
                "  block  powered " + powered + "  strongly " + StronglyPowered.Contains(tile));
        }
        else
        {
            report.AppendLine("  nothing of ours here");
        }

        report.Append("neighbours:");
        foreach (GlobalTileCoordinate neighbour in Around(tile))
        {
            if (Components.TryGetValue(neighbour, out RedstoneComponent other))
            {
                report.Append("  " + other.Definition.Kind + "(" + other.Power + ")");
            }
            else if (PoweredBlocks.TryGetValue(neighbour, out bool blockPowered))
            {
                report.Append("  block(" + (StronglyPowered.Contains(neighbour) ? "strong" : blockPowered ? "weak" : "off") + ")");
            }
        }

        return report.ToString();
    }

    /// The tile an observer is watching, for the side panel to name.
    ///
    /// Worth surfacing because the observer is the one component whose input side cannot be
    /// deduced from its own behaviour when it is quiet - and a player who has pointed it the
    /// wrong way sees exactly the same thing as one whose observer is broken.
    public bool TryGetWatchedTile(GlobalTileCoordinate tile, out GlobalTileCoordinate watched)
    {
        if (Components.TryGetValue(tile, out RedstoneComponent component)
            && component.Definition.Kind == RedstoneKind.Observer)
        {
            watched = Facing(tile, component);
            return true;
        }

        watched = tile;
        return false;
    }

    public void Press(GlobalTileCoordinate tile)
    {
        if (!Components.TryGetValue(tile, out RedstoneComponent component))
        {
            return;
        }

        switch (component.Definition.Kind)
        {
            case RedstoneKind.Lever:
                component.LeverOn = !component.LeverOn;
                break;

            case RedstoneKind.Button:
                component.ButtonTicksLeft = ButtonTicks;
                break;

            case RedstoneKind.Repeater:
                component.RepeaterDelay = component.RepeaterDelay % 4 + 1;
                break;

            case RedstoneKind.Comparator:
                component.Subtracting = !component.Subtracting;
                break;
        }
    }

    /// Which neighbours dust spreads to: the four around it, and the same four a layer up and a
    /// layer down, so a wire can climb. Minecraft lets dust run up the side of a block; with one
    /// unit per building layer and three layers to a platform, the diagonal neighbours are the
    /// equivalent.
    private IEnumerable<GlobalTileCoordinate> DustNeighbours(
        GlobalTileCoordinate tile, RedstoneComponent component)
    {
        // A repeater or comparator feeds only what it faces, and an observer only what is
        // behind it. A lamp feeds nothing at all.
        if (component.Definition.Kind == RedstoneKind.Repeater
            || component.Definition.Kind == RedstoneKind.Comparator)
        {
            yield return Facing(tile, component);
            yield break;
        }

        if (component.Definition.Kind == RedstoneKind.Observer)
        {
            yield return Behind(tile, component);
            yield break;
        }

        if (component.Definition.Kind == RedstoneKind.Lamp)
        {
            yield break;
        }

        // A wall torch reaches only its own layer. Same reason as above: it is bracketed onto the
        // side of a block rather than standing on the floor of its tile, so reaching a layer up or
        // down would be reaching through the block it is bolted to.
        if (component.Definition.Kind == RedstoneKind.Torch && component.Mounted)
        {
            yield return tile + TileDirection.North;
            yield return tile + TileDirection.East;
            yield return tile + TileDirection.South;
            yield return tile + TileDirection.West;
            yield break;
        }

        foreach (GlobalTileCoordinate neighbour in Around(tile))
        {
            yield return neighbour;

            GlobalTileCoordinate up = neighbour;
            up.z++;
            yield return up;

            GlobalTileCoordinate down = neighbour;
            down.z--;
            yield return down;
        }
    }

    private static IEnumerable<GlobalTileCoordinate> Around(GlobalTileCoordinate tile)
    {
        yield return tile + TileDirection.North;
        yield return tile + TileDirection.East;
        yield return tile + TileDirection.South;
        yield return tile + TileDirection.West;

        yield return Above(tile);

        GlobalTileCoordinate below = tile;
        below.z--;
        yield return below;
    }

    private static GlobalTileCoordinate Below(GlobalTileCoordinate tile)
    {
        GlobalTileCoordinate below = tile;
        below.z--;
        return below;
    }

    private static GlobalTileCoordinate Above(GlobalTileCoordinate tile)
    {
        GlobalTileCoordinate above = tile;
        above.z++;
        return above;
    }

    /// The tile a component points at. Rotation zero faces East, which is the game's own
    /// convention - TileDirection.East.Rotate(rotation) is how the vanilla placement code walks
    /// a building's connectors.
    private static GlobalTileCoordinate Facing(GlobalTileCoordinate tile, RedstoneComponent component)
    {
        return tile + TileDirection.East.Rotate(component.Rotation);
    }

    /// The tile behind it: what a repeater reads, and what a torch is mounted on.
    private static GlobalTileCoordinate Behind(GlobalTileCoordinate tile, RedstoneComponent component)
    {
        return BehindOf(tile, component.Rotation);
    }

    /// `Behind` for something that is not placed yet, so has no component to ask.
    ///
    /// The placement ghost needs exactly this and only has a `GlobalTileTransform`. Splitting it
    /// out rather than copying the one line into the preview is the point: a torch's mount has to
    /// be predicted with the **same** arithmetic the simulation will later use, or the ghost shows
    /// one wall and the torch lands on another.
    public static GlobalTileCoordinate BehindOf(GlobalTileCoordinate tile, GridRotation rotation)
    {
        return tile + TileDirection.West.Rotate(rotation);
    }

    /// Whether a decoration block occupies this tile - the thing a torch mounts on.
    ///
    /// `PoweredBlocks` is keyed by every block as it is placed and its value says whether that
    /// block is a block of sparkstone, so membership is "a block is here" and the value is a
    /// separate question. `SolveMounts` tests membership, and so does this.
    public bool HasBlockAt(GlobalTileCoordinate tile)
    {
        return PoweredBlocks.ContainsKey(tile);
    }

    private static IEnumerable<GlobalTileCoordinate> Sides(
        GlobalTileCoordinate tile, RedstoneComponent component)
    {
        yield return tile + TileDirection.North.Rotate(component.Rotation);
        yield return tile + TileDirection.South.Rotate(component.Rotation);
    }

    /// Which of the four sides this dust has a connection on, as a bitmask matching the
    /// generated textures: N=1, E=2, S=4, W=8. Read by the renderer, so it is computed on demand
    /// rather than stored.
    public int ConnectionMask(GlobalTileCoordinate tile)
    {
        int mask = 0;
        mask |= Connects(tile, TileDirection.North) ? 1 : 0;
        mask |= Connects(tile, TileDirection.East) ? 2 : 0;
        mask |= Connects(tile, TileDirection.South) ? 4 : 0;
        mask |= Connects(tile, TileDirection.West) ? 8 : 0;
        return mask;
    }

    private bool Connects(GlobalTileCoordinate tile, TileDirection direction)
    {
        GlobalTileCoordinate neighbour = tile + direction;

        // Same layer: any component counts, because every one of them either feeds dust or is
        // fed by it.
        if (Components.ContainsKey(neighbour))
        {
            return true;
        }

        // A layer up or down: only dust, which is the wire running up or down a step. A repeater
        // on the floor above is not connected to this dust and should not draw as though it were.
        for (int layer = 1; layer >= -1; layer -= 2)
        {
            GlobalTileCoordinate candidate = neighbour;
            candidate.z = (short)(neighbour.z + layer);

            if (Components.TryGetValue(candidate, out RedstoneComponent stepped)
                && stepped.Definition.Kind == RedstoneKind.Dust)
            {
                return true;
            }
        }

        // Not a plain block. Minecraft's dust does not draw an arm at the stone beside it either -
        // it connects to wire and to components, and running up a block shows as the stepped case
        // above. Treating every block as a connection drew arms at every wall a wire ran along.
        return false;
    }

}
