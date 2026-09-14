using System;
using System.Collections.Generic;
using System.Text;
using Game.Content.Features.Fluids;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// Saturates a sandbox and counts what crosses its boundary, which is the whole quantitative
/// half of a recipe.
///
/// A copied platform has nothing on the far side of its ports, and the game models that
/// honestly: a sender with no receiver becomes a <c>BeltPortSenderBlockedSimulation</c>, a
/// receiver with no sender a <c>BeltPortReceiverDisabledSimulation</c>, and fluid ports leave
/// their containers dangling the same way. Those are exactly the handles a measurement needs -
/// feed every input as fast as it will take anything, drain every output so nothing backs up,
/// and count what goes each way.
///
/// The numbers produced are the ceiling, not the rate the factory happened to be running at. A
/// blackbox standing in for a factory has to be able to do what the factory could do.
///
/// Counting is per item, not just in total, because a recipe is a ratio. A painter consumes one
/// shape and some paint to make one painted shape, and the only way to know that is to watch how
/// much of each thing went in against how much came out - over the plateau, once the belts have
/// filled, since a factory still filling consumes without producing and would read as consuming
/// far more per product than it really does.
/// </summary>
public class SandboxMeter
{
    /// <summary>
    /// Accepts everything and remembers what. Standing in for the belt that would be on the
    /// other side of the port, with infinite appetite so the factory never backs up.
    /// </summary>
    private class Drain : IItemReceiver
    {
        public int Count;

        /// What arrived and how much of it. The output half of a recipe, observed rather than
        /// predicted - which matters, because an isolated blueprint has nothing to predict from.
        public readonly Dictionary<IBeltItem, int> Counts = new Dictionary<IBeltItem, int>();

        /// A full slot's worth of room, always - the same answer an empty lane gives.
        public Steps MaxStep_S
        {
            get { return LaneConstants.ItemSpacing; }
        }

        public bool CanAcceptItem(IBeltItem itemToTransfer)
        {
            return true;
        }

        public void HandOverItem(IBeltItem itemToTransfer, Ticks remainingTicks)
        {
            Count++;

            if (itemToTransfer == null)
            {
                return;
            }

            int seen;
            Counts.TryGetValue(itemToTransfer, out seen);
            Counts[itemToTransfer] = seen + 1;
        }
    }

    /// An item input being held at saturation.
    private class Source
    {
        public IItemReceiver Lane;
        public IBeltItem Item;

        /// Items the factory actually took. Not the same as items offered: an input that is
        /// never drawn on accepts nothing, and counting offers would invent consumption.
        public int Accepted;
    }

    /// A fluid input being kept topped up.
    private class FluidSource
    {
        public ProvidingFluidContainer Container;
        public IFluid Fluid;

        /// Volume the factory actually drew, measured as the room that had to be refilled.
        public FluidUnit Added;
    }

    private readonly List<Source> Sources = new List<Source>();
    private readonly List<FluidSource> FluidSources = new List<FluidSource>();
    private readonly List<Drain> Taps = new List<Drain>();
    private readonly List<ConsumingFluidContainer> FluidTaps = new List<ConsumingFluidContainer>();

    /// Rate in each window, so the ramp is visible rather than only its average.
    private readonly List<float> WindowRates = new List<float>();

    /// Where the run has got to, so it can be stopped after any step and picked up again.
    private MeasurementLimits Limits;
    private int Window;
    private int StepInWindow;
    private int WindowStartOut;
    private float PlateauTotal;
    private int PlateauCount;
    private int SteadyRun;
    private Ticks SettleUntil;

    /// <summary>True once this meter has nothing left to do.</summary>
    public bool Finished { get; private set; }

    /// Totals at the moment the plateau began, so the steady-state figures are a difference
    /// rather than a lifetime average that still carries the fill in it.
    private Dictionary<IItem, int> ConsumedAtPlateau;
    private Dictionary<IItem, int> ProducedAtPlateau;
    private Dictionary<IFluid, FluidUnit> FluidAtPlateau;
    private Ticks PlateauBegan;

    /// Inputs with nothing to feed them - the measurement is only as good as these.
    public int UnfedInputs { get; private set; }
    public int UnfedFluidInputs { get; private set; }

    public Ticks Elapsed { get; private set; }

    /// When the first item reached an output: the block's latency, measured rather than guessed.
    public Ticks Latency { get; private set; }
    public bool Produced { get; private set; }

    /// True when the rate stopped improving, so the figure below is a plateau rather than a
    /// point on the way up.
    public bool Converged { get; private set; }
    public float SteadyRatePerMinute { get; private set; }

    /// The best window seen. If this is well above the steady figure, the plateau call was
    /// premature and the block was still climbing.
    public float PeakRatePerMinute { get; private set; }

    /// <summary>How long the steady stretch lasted, which is the window the ratios below were
    /// counted over. Zero when the block never settled.</summary>
    public float PlateauSeconds { get; private set; }

    /// <summary>Shapes consumed and produced over the plateau, per item.</summary>
    public Dictionary<IItem, int> ConsumedShapes = new Dictionary<IItem, int>();
    public Dictionary<IItem, int> ProducedShapes = new Dictionary<IItem, int>();

    /// <summary>Fluid drawn over the plateau, per fluid.</summary>
    public Dictionary<IFluid, FluidUnit> ConsumedFluid = new Dictionary<IFluid, FluidUnit>();

    /// <summary>How many dangling fluid outputs the copy had. A box cannot reproduce fluid
    /// output yet, so this is reported rather than modelled.</summary>
    public int FluidOutputs
    {
        get { return FluidTaps.Count; }
    }

    /// <summary>The distinct items that left, across every output.</summary>
    public List<IBeltItem> Outputs
    {
        get
        {
            List<IBeltItem> produced = new List<IBeltItem>();

            foreach (Drain tap in Taps)
            {
                foreach (IBeltItem item in tap.Counts.Keys)
                {
                    if (!produced.Contains(item))
                    {
                        produced.Add(item);
                    }
                }
            }

            return produced;
        }
    }

    /// <summary>
    /// Finds the sandbox's dangling ports, puts a drain on every output and a feed on every
    /// input there is a shape or a fluid for.
    /// </summary>
    public static SandboxMeter Attach(BlackboxSandbox sandbox, SandboxFeed feed)
    {
        SandboxMeter meter = new SandboxMeter();

        foreach (ILocalizedSimulation localized in sandbox.Simulations.Simulations)
        {
            switch (localized.Simulation)
            {
                // A belt port whose neighbour was not copied: items pile up in its lane with
                // nowhere to go, so give the lane somewhere to go.
                case BeltPortSenderBlockedSimulation blocked:
                    meter.AddDrain(blocked.InputLane);
                    break;

                case SpaceBeltPortSenderSimulation sender:
                    // Its hook refuses items once the space-side buffer fills, and in a
                    // sandbox nobody ever empties that buffer.
                    sender.PathLane.PreAcceptHook = _ => true;
                    meter.AddDrain(sender.PathLane);
                    break;

                case BeltPortReceiverDisabledSimulation disabled:
                    meter.AddSource(localized, disabled.OutputLane, feed);
                    break;

                case SpaceBeltPortReceiverSimulation receiver:
                    meter.AddSource(localized, receiver.InputLane, feed);
                    break;

                case FluidPortBlockedSimulation fluidOut:
                    meter.FluidTaps.Add(fluidOut.FluidPortSender);
                    break;

                case FluidPortReceiverDisabledSimulation fluidIn:
                    meter.AddFluidSource(localized, fluidIn.FluidPortReceiver, feed);
                    break;
            }
        }

        return meter;
    }

    /// <summary>
    /// The distinct notches a copy takes items in on, which is the set an ingredient assignment
    /// has to be guessed over.
    ///
    /// Ports are grouped by notch rather than listed individually because a notch is one belt -
    /// its twelve ports always carry the same thing - so guessing per port would multiply the
    /// search by twelve for no gain.
    /// </summary>
    public static List<NotchGrouping.Notch> InputNotches(BlackboxSandbox sandbox)
    {
        List<NotchGrouping.Notch> notches = new List<NotchGrouping.Notch>();

        foreach (ILocalizedSimulation localized in sandbox.Simulations.Simulations)
        {
            if (!(localized.Simulation is BeltPortReceiverDisabledSimulation)
                && !(localized.Simulation is SpaceBeltPortReceiverSimulation))
            {
                continue;
            }

            foreach (GlobalTileCoordinate tile in SandboxFeed.TilesOf(localized))
            {
                NotchGrouping.Notch notch;
                int index;
                if (!NotchGrouping.TryNotchOf(tile, out notch, out index))
                {
                    continue;
                }

                if (!notches.Contains(notch))
                {
                    notches.Add(notch);
                }

                break;
            }
        }

        return notches;
    }

    /// <summary>
    /// Runs just long enough to find out whether anything comes out at all.
    ///
    /// Used to sift candidate ingredient assignments. Only one of them is the factory's actual
    /// recipe, and the wrong ones mostly produce nothing - so asking the cheap question first
    /// keeps the expensive full measurement to a single run.
    /// </summary>
    public bool PumpProbe(BlackboxSandbox sandbox, int steps)
    {
        if (Finished)
        {
            return true;
        }

        Ticks step = Ticks.OneSecond / Limits.StepsPerSecond;

        for (int i = 0; i < steps; i++)
        {
            Offer();
            sandbox.Advance(step);
            Elapsed += step;

            if (!Produced)
            {
                if (TotalOut() > 0)
                {
                    Produced = true;
                    Latency = Elapsed;
                    SettleUntil = Elapsed + Ticks.OneSecond * Limits.SettleSeconds;
                }
                else if (Seconds(Elapsed) >= Limits.ProbeSeconds)
                {
                    Finished = true;
                    return true;
                }

                continue;
            }

            // Keep going a little. One item is enough to prove the factory runs, but not to say
            // what it makes: a block can emit two products and which arrives first is an accident
            // of belt length.
            if (Elapsed >= SettleUntil)
            {
                Finished = true;
                return true;
            }
        }

        return false;
    }

    private void AddDrain(IItemProvider lane)
    {
        Drain drain = new Drain();
        lane.NextLane = drain;
        Taps.Add(drain);
    }

    private void AddSource(ILocalizedSimulation localized, IItemReceiver lane, SandboxFeed feed)
    {
        IBeltItem item = feed?.ItemFor(localized);
        if (item == null)
        {
            UnfedInputs++;
            return;
        }

        Sources.Add(new Source { Lane = lane, Item = item });
    }

    private void AddFluidSource(ILocalizedSimulation localized, ProvidingFluidContainer container,
        SandboxFeed feed)
    {
        IFluid fluid = feed?.FluidFor(localized);
        if (fluid == null)
        {
            UnfedFluidInputs++;
            return;
        }

        FluidSources.Add(new FluidSource { Container = container, Fluid = fluid });
    }

    /// <summary>
    /// Prepares a measurement. Nothing runs until <see cref="PumpMeasure"/> is called.
    ///
    /// Split from the running because a measurement can take minutes of simulated time, and
    /// doing it in one call freezes the game for as long as it takes. It is a loop over small
    /// steps, so it can equally be a loop the caller drives a slice at a time.
    /// </summary>
    public void Begin(MeasurementLimits limits)
    {
        Limits = limits;
        Window = 0;
        StepInWindow = 0;
        WindowStartOut = TotalOut();
        PlateauTotal = 0f;
        PlateauCount = 0;
        SteadyRun = 0;
        Finished = false;
    }

    /// <summary>
    /// Advances the measurement by at most <paramref name="steps"/> steps. True once it has
    /// finished, for whatever reason - settled, capped, or given up on.
    ///
    /// Time moves in small steps rather than one jump, because an input can only take an item
    /// once the last one has moved far enough along; a single large delta would let the belts
    /// advance a long way with nothing offered to them, and measure a starved factory.
    /// </summary>
    public bool PumpMeasure(BlackboxSandbox sandbox, int steps)
    {
        if (Finished)
        {
            return true;
        }

        Ticks step = Ticks.OneSecond / Limits.StepsPerSecond;
        int windows = Math.Max(1, Limits.MaxSeconds / Limits.WindowSeconds);

        for (int i = 0; i < steps && !Finished; i++)
        {
            Offer();
            sandbox.Advance(step);
            Elapsed += step;

            if (!Produced && TotalOut() > 0)
            {
                Produced = true;
                Latency = Elapsed;
            }

            // A factory that cannot run at all - because something it needs is not being fed -
            // would otherwise sit here for the whole cap. Belt travel through a big block is
            // slow and a MAM can take minutes to fill, so how long to wait is a setting.
            if (!Produced && Seconds(Elapsed) >= Limits.GiveUpSeconds)
            {
                Stop();
                return true;
            }

            if (++StepInWindow < Limits.StepsPerWindow)
            {
                continue;
            }

            StepInWindow = 0;
            EndWindow();

            if (++Window >= windows)
            {
                Stop();
            }
        }

        return Finished;
    }

    /// <summary>
    /// Closes off a rate window and decides whether the block has levelled off.
    ///
    /// A filling factory climbs towards its ceiling and flattens out, so two adjacent windows can
    /// sit within a few percent of each other while the rate is still going up. Settling
    /// therefore means "stopped improving on the best seen", held for several windows - not
    /// "changed little since last time", which a slow climb satisfies all the way up.
    /// </summary>
    private void EndWindow()
    {
        float rate = (TotalOut() - WindowStartOut) * 60f / Limits.WindowSeconds;
        WindowRates.Add(rate);
        WindowStartOut = TotalOut();

        // The window the first item appeared in is part fill, not throughput, and averaging it in
        // would understate the block. Skip it and anything before it.
        if (!Produced || Latency + Ticks.OneSecond * Limits.WindowSeconds > Elapsed)
        {
            return;
        }

        if (rate > PeakRatePerMinute * (1f + Limits.Tolerance))
        {
            // Still meaningfully better than anything so far: it has not levelled off, and
            // whatever plateau was accumulating was a false one. The climbing window itself is
            // not part of any plateau either - it is a point on the way up, and averaging it in
            // drags the answer below the rate the block actually holds.
            PeakRatePerMinute = rate;
            PlateauTotal = 0f;
            PlateauCount = 0;
            SteadyRun = 0;
            ConsumedAtPlateau = null;
            return;
        }

        PeakRatePerMinute = Math.Max(PeakRatePerMinute, rate);
        PlateauTotal += rate;
        PlateauCount++;
        SteadyRun++;

        // The ratios are counted from here on. Everything before this point includes the fill,
        // where the factory swallowed ingredients and produced nothing.
        if (SteadyRun == 1)
        {
            ConsumedAtPlateau = ShapesIn();
            ProducedAtPlateau = ShapesOut();
            FluidAtPlateau = FluidIn();
            PlateauBegan = Elapsed;
        }

        if (SteadyRun >= Limits.StableWindows)
        {
            Converged = true;
            Stop();
        }
    }

    private void Stop()
    {
        Finished = true;

        // Hitting the cap mid-climb leaves no plateau at all; the best window is then the most
        // honest floor available.
        SteadyRatePerMinute = PlateauCount > 0 ? PlateauTotal / PlateauCount : PeakRatePerMinute;
        Settle();
    }

    /// <summary>How far through its allotted simulated time this run is, for reporting.</summary>
    public float ElapsedSeconds
    {
        get { return Seconds(Elapsed); }
    }

    private static float Seconds(Ticks ticks)
    {
        return Ticks.Ratio(ticks, Ticks.OneSecond);
    }

    /// <summary>
    /// Reduces the plateau to the differences that make up a recipe. Nothing is reported when
    /// the block never settled, because ratios taken across a fill are not ratios.
    /// </summary>
    private void Settle()
    {
        if (ConsumedAtPlateau == null)
        {
            return;
        }

        PlateauSeconds = Ticks.Ratio(Elapsed - PlateauBegan, Ticks.OneSecond);
        ConsumedShapes = Difference(ShapesIn(), ConsumedAtPlateau);
        ProducedShapes = Difference(ShapesOut(), ProducedAtPlateau);
        ConsumedFluid = Difference(FluidIn(), FluidAtPlateau);
    }

    private static Dictionary<IItem, int> Difference(Dictionary<IItem, int> now,
        Dictionary<IItem, int> then)
    {
        Dictionary<IItem, int> delta = new Dictionary<IItem, int>();

        foreach (KeyValuePair<IItem, int> entry in now)
        {
            int before;
            then.TryGetValue(entry.Key, out before);

            if (entry.Value > before)
            {
                delta[entry.Key] = entry.Value - before;
            }
        }

        return delta;
    }

    private static Dictionary<IFluid, FluidUnit> Difference(Dictionary<IFluid, FluidUnit> now,
        Dictionary<IFluid, FluidUnit> then)
    {
        Dictionary<IFluid, FluidUnit> delta = new Dictionary<IFluid, FluidUnit>();

        foreach (KeyValuePair<IFluid, FluidUnit> entry in now)
        {
            FluidUnit before;
            then.TryGetValue(entry.Key, out before);

            if (entry.Value > before)
            {
                delta[entry.Key] = entry.Value - before;
            }
        }

        return delta;
    }

    /// Everything the factory has taken so far, per shape.
    private Dictionary<IItem, int> ShapesIn()
    {
        Dictionary<IItem, int> totals = new Dictionary<IItem, int>();

        foreach (Source source in Sources)
        {
            // A fluid arriving on a belt is stock measured by volume, not by crate, and the
            // sandbox feeds fluid through the fluid ports anyway - so a package here would be
            // double counting.
            if (source.Accepted <= 0 || !(source.Item is IItem shape)
                || source.Item is FluidPackageItem)
            {
                continue;
            }

            int seen;
            totals.TryGetValue(shape, out seen);
            totals[shape] = seen + source.Accepted;
        }

        return totals;
    }

    /// Everything that has left, per shape.
    private Dictionary<IItem, int> ShapesOut()
    {
        Dictionary<IItem, int> totals = new Dictionary<IItem, int>();

        foreach (Drain tap in Taps)
        {
            foreach (KeyValuePair<IBeltItem, int> entry in tap.Counts)
            {
                if (!(entry.Key is IItem shape) || entry.Key is FluidPackageItem)
                {
                    continue;
                }

                int seen;
                totals.TryGetValue(shape, out seen);
                totals[shape] = seen + entry.Value;
            }
        }

        return totals;
    }

    /// Volume drawn so far, per fluid.
    private Dictionary<IFluid, FluidUnit> FluidIn()
    {
        Dictionary<IFluid, FluidUnit> totals = new Dictionary<IFluid, FluidUnit>();

        foreach (FluidSource source in FluidSources)
        {
            FluidUnit seen;
            totals.TryGetValue(source.Fluid, out seen);
            totals[source.Fluid] = seen + source.Added;
        }

        return totals;
    }

    private void Offer()
    {
        foreach (Source source in Sources)
        {
            if (source.Lane.CanAcceptItem(source.Item))
            {
                source.Lane.HandOverItem(source.Item, Ticks.Zero);
                source.Accepted++;
            }
        }

        // A fluid input is a tank the platform draws from rather than a lane, so saturating it
        // means keeping it full rather than handing it things - and the room that has to be
        // refilled each time is exactly what the factory drew.
        foreach (FluidSource source in FluidSources)
        {
            FluidUnit room = source.Container.RemainingCapacity;
            if (room.IsZero())
            {
                continue;
            }

            source.Container.TryAdd(room, source.Fluid);
            source.Added += room - source.Container.RemainingCapacity;
        }

        // And a dangling fluid output is a tank that would otherwise fill and stall the block.
        foreach (ConsumingFluidContainer tap in FluidTaps)
        {
            tap.Flush();
        }
    }

    private int TotalOut()
    {
        int total = 0;

        foreach (Drain tap in Taps)
        {
            total += tap.Count;
        }

        return total;
    }

    public string Describe()
    {
        StringBuilder text = new StringBuilder();

        text.Append("fed ").Append(Sources.Count).Append(" item ")
            .Append(Sources.Count == 1 ? "input" : "inputs");

        if (FluidSources.Count > 0)
        {
            text.Append(" and ").Append(FluidSources.Count).Append(" fluid ")
                .Append(FluidSources.Count == 1 ? "input" : "inputs");
        }

        text.Append(", drained ").Append(Taps.Count)
            .Append(Taps.Count == 1 ? " output" : " outputs");

        if (FluidTaps.Count > 0)
        {
            text.Append(" and ").Append(FluidTaps.Count).Append(" fluid ")
                .Append(FluidTaps.Count == 1 ? "output" : "outputs");
        }

        int unfed = UnfedInputs + UnfedFluidInputs;
        if (unfed > 0)
        {
            text.Append("\n").Append(unfed)
                .Append(unfed == 1 ? " input had" : " inputs had")
                .Append(" nothing to feed it, so ran starved");
        }

        float seconds = Ticks.Ratio(Elapsed, Ticks.OneSecond);
        if (seconds <= 0f)
        {
            return text.Append("\nNo time elapsed.").ToString();
        }

        text.Append("\n\nran ").Append(seconds.ToString("0.#")).Append(" seconds of simulation");

        if (!Produced)
        {
            text.Append("\n\nNothing came out. Either the block needs an input that is not being "
                + "fed,\nor it needs longer than the cap to fill.");
            return text.ToString();
        }

        text.Append("\n  latency  ").Append(Ticks.Ratio(Latency, Ticks.OneSecond).ToString("0.#"))
            .Append("s before the first item reached an output");

        text.Append("\n\nsteady output: ").Append(SteadyRatePerMinute.ToString("0"))
            .Append("/min across ").Append(Taps.Count)
            .Append(Taps.Count == 1 ? " port" : " ports");

        text.Append(Converged
            ? " (levelled off)"
            : " (STILL CLIMBING at the cap - treat this as a floor, not the ceiling)");

        if (PeakRatePerMinute > SteadyRatePerMinute)
        {
            text.Append("\n  best window ").Append(PeakRatePerMinute.ToString("0")).Append("/min");
        }

        text.Append("\n  per window: ");
        for (int i = 0; i < WindowRates.Count; i++)
        {
            if (i != 0)
            {
                text.Append(", ");
            }

            text.Append(WindowRates[i].ToString("0"));
        }

        if (PlateauSeconds <= 0f)
        {
            return text.ToString();
        }

        text.Append("\n\nover ").Append(PlateauSeconds.ToString("0.#"))
            .Append("s of steady running:");

        foreach (KeyValuePair<IItem, int> consumed in ConsumedShapes)
        {
            text.Append("\n  took     ").Append(consumed.Value).Append("x ").Append(consumed.Key);
        }

        foreach (KeyValuePair<IFluid, FluidUnit> consumed in ConsumedFluid)
        {
            text.Append("\n  drew     ").Append(consumed.Value.LitersApprox.ToString("0.#"))
                .Append("L ").Append(consumed.Key);
        }

        foreach (KeyValuePair<IItem, int> produced in ProducedShapes)
        {
            text.Append("\n  made     ").Append(produced.Value).Append("x ").Append(produced.Key);
        }

        if (FluidTaps.Count > 0)
        {
            text.Append("\n  and put out fluid, which a box cannot reproduce yet");
        }

        return text.ToString();
    }
}
