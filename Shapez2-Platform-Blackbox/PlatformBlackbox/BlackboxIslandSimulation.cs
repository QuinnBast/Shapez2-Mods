using System;
using System.Collections.Generic;
using System.Text;
using Core.Factory;
using Game.Content.Features.SpacePaths;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// The simulation behind a blackbox platform, of whatever size.
///
/// Items arriving on any notch go into one shared pool. A cycle of the measured recipe is paid
/// for out of that pool and its products are handed to whichever output notch has room. That is
/// the whole mechanism, and it is what lets a box stand in for a factory that combines
/// ingredients: a painter's shape and its paint arrive on different notches, and only something
/// that pools them can spend them together.
///
/// It also means side and order do not matter. An earlier version wired each input notch
/// straight through to the notch opposite and transformed items one at a time in the lane, which
/// forced the player to discover an arrangement the box never should have had an opinion about -
/// and could not paint anything regardless, because the paint left by the far side untouched
/// instead of being consumed.
///
/// With no recipe the box hands back whatever it is given, which is the right behaviour for one
/// placed from the toolbar with no blueprint behind it.
/// </summary>
public class BlackboxIslandSimulation : Simulation<BlackboxIslandState>, IItemBundleSimulation,
    IUpdatableSimulation
{
    /// <summary>
    /// Everything that outlives a session. Held here as well as passed to the base so the box can
    /// write its own answers back into it - a measured recipe is not the simulation's private
    /// business, it is what the save has to remember.
    /// </summary>
    public readonly BlackboxIslandState Saved;

    /// <summary>
    /// Stock, and the record of what has been fed in. Public because measuring a box means asking
    /// it what it has received; owned by the state because it is the box's contents.
    /// </summary>
    public BlackboxPool Pool
    {
        get { return Saved.Pool; }
    }

    private readonly ItemLaneBundle<BlackboxIntake>[] Inputs;
    private readonly ItemLaneBundle<BlackboxLane>[] Outputs;

    /// <summary>
    /// Output lanes split by what they can carry, because a lane cannot carry both.
    ///
    /// The definition declares belt-out and pipe-out on every notch, in that order, so the game
    /// hands out bundles alternating belt, pipe, belt, pipe. Which is how a lane knows what it
    /// is - and it has to know. A paint package dealt to a belt is a package the belt will never
    /// accept, so the lane holds it forever and is never free again: one output silently stops
    /// working while the rest carry on.
    /// </summary>
    private readonly List<BlackboxLane> BeltLanes = new List<BlackboxLane>();
    private readonly List<BlackboxLane> PipeLanes = new List<BlackboxLane>();

    /// Every output lane, for reporting.
    private readonly List<BlackboxLane> OutputLanes = new List<BlackboxLane>();

    /// <summary>
    /// What each notch is called, so a report can say *which* connection the game did not wire
    /// rather than only how many. Bundle j belongs to notch j/2.
    /// </summary>
    private readonly string[] NotchNames;

    private BlackboxRecipe Recipe;

    /// <summary>
    /// Fractional cycles owed. Capped at a second's worth, so a box whose output has been backed
    /// up for an hour does not discharge an hour of production the moment a belt frees up - but
    /// a box that merely missed a few ticks catches all of them back up.
    ///
    /// The cap used to be four cycles flat, which for anything running thousands a minute is a
    /// fraction of a tick: every hesitation was rounded away and never made up, so the box
    /// settled at a stable fraction of the rate it had measured.
    /// </summary>
    private float Progress;

    private const float BankSeconds = 1f;

    /// <summary>
    /// Why the box is not going faster, counted rather than guessed.
    ///
    /// A box falling short of its measured rate is short for exactly one of three reasons: it
    /// has no ingredients, it has nowhere to put the result, or it is not being asked to run
    /// often enough. Only counting them tells the three apart.
    /// </summary>
    public long CyclesRun { get; private set; }
    public long StarvedOfStock { get; private set; }
    public long OutOfRoom { get; private set; }
    public float CyclesDiscarded { get; private set; }

    /// Where the next product goes, kept between updates so the deal keeps moving round.
    private int BeltCursor;
    private int PipeCursor;

    public int NumItemReceiverBundles
    {
        get { return Inputs.Length; }
    }

    public int NumItemProviderBundles
    {
        get { return Outputs.Length; }
    }

    /// <summary>True once a recipe has been applied.</summary>
    public bool Configured
    {
        get { return Recipe != null; }
    }

    public BlackboxIslandSimulation(BlackboxIslandState state, int bundles, string[] notchNames)
        : base(state)
    {
        Saved = state;
        NotchNames = notchNames ?? new string[0];

        Inputs = new ItemLaneBundle<BlackboxIntake>[bundles];
        Outputs = new ItemLaneBundle<BlackboxLane>[bundles];

        for (int i = 0; i < bundles; i++)
        {
            Inputs[i] = ItemLaneBundle.Create<BlackboxIntake>();
            Outputs[i] = ItemLaneBundle.Create<BlackboxLane>();

            // Even bundles are belts, odd are pipes - the order the connectors are declared in,
            // which is also how the game indexes them: ConnectorsOfType asks for the *interface*
            // an output connector implements, so belts and pipes come back interleaved exactly
            // as declared rather than grouped by concrete type.
            bool pipe = i % 2 != 0;

            foreach (BlackboxIntake intake in Inputs[i])
            {
                intake.Pool = Pool;
                intake.Fluid = pipe;
            }

            List<BlackboxLane> kind = pipe ? PipeLanes : BeltLanes;

            foreach (BlackboxLane lane in Outputs[i])
            {
                kind.Add(lane);
                OutputLanes.Add(lane);
            }
        }

        // Deliberately nothing read out of the state here. A simulation is built around a state
        // object that the save has not been read into yet, so anything copied out of it at this
        // point is whatever a fresh one happens to hold - which is how a saved recipe came back
        // as null. References into the state are fine, because deserialisation fills those in
        // place; values are not. Adopt() picks the recipe up on the first update instead.
    }

    /// <summary>Applies a measured recipe. One recipe per box, because the factory it stands
    /// in for was one factory.</summary>
    public void Configure(BlackboxRecipe recipe)
    {
        Saved.Recipe = recipe != null && recipe.Runnable ? recipe : null;
        Adopt();
    }

    /// <summary>
    /// Takes up whatever recipe the state is holding.
    ///
    /// Called on every update, because the state can change underneath the simulation in two
    /// ways: a measurement finishing, and a save being read in after the simulation was built.
    /// The comparison is a reference check, so the ordinary case costs nothing.
    /// </summary>
    private void Adopt()
    {
        BlackboxRecipe wanted = Saved.Recipe != null && Saved.Recipe.Runnable ? Saved.Recipe : null;

        if (ReferenceEquals(Recipe, wanted))
        {
            return;
        }

        Recipe = wanted;
        Progress = 0f;

        // A box loaded from a save made before output lanes were typed can come back with paint
        // sitting in a belt lane, which nothing will ever take. Adopting a recipe is the moment
        // that happens, so it is the moment to put it back.
        Reclaim();

        // The pool's caps are sized in seconds of consumption, so it needs the rate too.
        Pool.Reserve(Recipe);

        CyclesRun = 0;
        StarvedOfStock = 0;
        OutOfRoom = 0;
        CyclesDiscarded = 0f;
    }

    /// <summary>Forgets the recipe, so the box passes items through until measured again.</summary>
    public void Unconfigure()
    {
        Configure(null);
    }

    public IItemReceiverBundle GetItemReceiverBundle(int inputIndex)
    {
        return Inputs[inputIndex];
    }

    public IItemProviderBundle GetItemProviderBundle(int outputIndex)
    {
        return Outputs[outputIndex];
    }

    public void Update(Ticks startTicks, Ticks deltaTicks)
    {
        // A save read in after this simulation was built leaves a recipe sitting in the state
        // that nothing has applied yet. Checking here rather than in the constructor is what
        // makes a reloaded box start working again.
        Adopt();

        // Outputs first, so a lane that emptied onto its belt this tick can be refilled in the
        // same tick rather than the next one.
        foreach (ItemLaneBundle<BlackboxLane> bundle in Outputs)
        {
            bundle.Update(deltaTicks);
        }

        if (Recipe == null)
        {
            PassThrough();
            return;
        }

        Progress += Recipe.CyclesPerMinute * Ticks.Ratio(deltaTicks, Ticks.OneSecond * 60);

        // Run first, cap afterwards. Capping first would throw away progress the box was about
        // to spend, and would make every stall look like an update-rate problem.
        bool blocked = false;

        while (Progress >= 1f)
        {
            if (!Pool.CanPay(Recipe))
            {
                StarvedOfStock++;
                blocked = true;
                break;
            }

            if (FreeSlots(fluid: false) < Recipe.OutputCount)
            {
                OutOfRoom++;
                blocked = true;
                break;
            }

            RunCycle();
            Progress -= 1f;
        }

        float bank = Math.Max(2f, Recipe.CyclesPerMinute / 60f * BankSeconds);
        if (Progress <= bank)
        {
            return;
        }

        // Progress left over after a stall is production the factory could not have made
        // either, so dropping it is right and is not worth reporting. Progress left over when
        // nothing was in the way means the box was simply not asked to run often enough.
        if (!blocked)
        {
            CyclesDiscarded += Progress - bank;
        }

        Progress = bank;
    }

    /// <summary>
    /// Pays for one cycle and deals out its products.
    ///
    /// Stock and room are both checked by the caller before anything is spent. A cycle that
    /// consumed its ingredients and then found nowhere to put the result would destroy them,
    /// which is the one thing a stand-in must never do - the factory it replaced would simply
    /// have stalled.
    /// </summary>
    private void RunCycle()
    {
        Pool.Pay(Recipe);
        CyclesRun++;

        foreach (KeyValuePair<IItem, int> product in Recipe.ShapesOut)
        {
            for (int i = 0; i < product.Value; i++)
            {
                Deal(product.Key as IBeltItem);
            }
        }
    }

    /// <summary>
    /// A box with nothing to do: whatever came in goes back out.
    ///
    /// Three cases, and only the middle one holds. A box with no blueprint is a conduit placed
    /// from the toolbar and passes everything. A box with a blueprint that has never been
    /// measured holds its ingredients, because handing them straight out would put raw shapes on
    /// the output belt as though they were the product - backing up is visible, and it is what
    /// the factory would have done while filling. But a box that has *been* measured and came
    /// back with no recipe has been asked and answered: nothing is coming to rescue it, so
    /// holding would be a deadlock rather than patience.
    /// </summary>
    private void PassThrough()
    {
        bool standsInForSomething = !string.IsNullOrEmpty(Saved.Blueprint);
        bool alreadyAsked = !string.IsNullOrEmpty(Saved.MeasuredSignature);

        if (standsInForSomething && !alreadyAsked)
        {
            return;
        }

        while (FreeSlots(fluid: false) > 0 || FreeSlots(fluid: true) > 0)
        {
            IBeltItem item;
            if (!Pool.TryTakeAnything(out item) || !Deal(item))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Hands one item to the next attached lane of the right kind, starting where the last deal
    /// left off. False when there was nowhere to put it.
    /// </summary>
    private bool Deal(IBeltItem item)
    {
        if (item == null)
        {
            return false;
        }

        bool fluid = item is FluidPackageItem;
        List<BlackboxLane> lanes = fluid ? PipeLanes : BeltLanes;
        int cursor = fluid ? PipeCursor : BeltCursor;

        for (int step = 0; step < lanes.Count; step++)
        {
            int at = (cursor + step) % lanes.Count;
            BlackboxLane lane = lanes[at];

            if (!lane.Attached || !lane.CanAcceptItem(item))
            {
                continue;
            }

            lane.HandOverItem(item, Ticks.Zero);

            if (fluid)
            {
                PipeCursor = (at + 1) % lanes.Count;
            }
            else
            {
                BeltCursor = (at + 1) % lanes.Count;
            }

            return true;
        }

        return false;
    }

    /// Room across the attached lanes that can carry this kind of thing. A box declares an output
    /// connector on every notch, so attachment is the only thing that knows which are outputs.
    private int FreeSlots(bool fluid)
    {
        int free = 0;

        foreach (BlackboxLane lane in fluid ? PipeLanes : BeltLanes)
        {
            if (lane.Attached)
            {
                free += lane.Room;
            }
        }

        return free;
    }

    /// <summary>
    /// Takes back anything sitting in a lane that cannot carry it, so a lane jammed by an older
    /// version starts working again rather than staying dead for the life of the save.
    /// </summary>
    private void Reclaim()
    {
        Rescue(BeltLanes, wantFluid: false);
        Rescue(PipeLanes, wantFluid: true);
    }

    private void Rescue(List<BlackboxLane> lanes, bool wantFluid)
    {
        foreach (BlackboxLane lane in lanes)
        {
            IBeltItem held = lane.GetItem(0);

            if (held == null || held is FluidPackageItem == wantFluid)
            {
                continue;
            }

            lane.Clear();
            Pool.Deposit(held);
        }
    }

    /// <summary>
    /// Whether the game has wired anything to an output at all.
    ///
    /// A box that stands in for a factory and has nowhere to send anything is broken rather than
    /// idle, and after a reload that is the shape the fault takes.
    /// </summary>
    public bool AnyOutputAttached
    {
        get
        {
            foreach (BlackboxLane lane in OutputLanes)
            {
                if (lane.Attached)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>How many output lanes the game has wired, for reporting a re-link's effect.</summary>
    public int AttachedOutputLanes
    {
        get { return AttachedLanes(); }
    }

    private int AttachedLanes()
    {
        int attached = 0;

        foreach (BlackboxLane lane in OutputLanes)
        {
            if (lane.Attached)
            {
                attached++;
            }
        }

        return attached;
    }

    /// <summary>
    /// Which notches the game actually wired, by name.
    ///
    /// A count alone cannot tell "you connected two belts" from "you connected four and two of
    /// them were not wired up", and those need completely different answers.
    /// </summary>
    private string Wiring()
    {
        StringBuilder wired = new StringBuilder();
        StringBuilder idle = new StringBuilder();

        for (int bundle = 0; bundle < Outputs.Length; bundle++)
        {
            bool attached = false;
            foreach (BlackboxLane lane in Outputs[bundle])
            {
                attached |= lane.Attached;
            }

            StringBuilder into = attached ? wired : idle;
            int notch = bundle / 2;

            into.Append(into.Length == 0 ? string.Empty : ", ")
                .Append(notch < NotchNames.Length ? NotchNames[notch] : "notch " + notch)
                .Append(bundle % 2 == 0 ? " belt" : " pipe");
        }

        return "\n  out wired  " + (wired.Length == 0 ? "nothing" : wired.ToString())
            + "\n  out unused " + (idle.Length == 0 ? "nothing" : idle.ToString())
            + Feeding();
    }

    /// <summary>
    /// Which notches have actually delivered anything, and how much.
    ///
    /// Inputs cannot be reported the way outputs are: an output lane holds the connection the
    /// game gave it, so it knows whether it is wired, but a receiver is wired from the other end
    /// and has no way to look back. Counting arrivals answers the question that matters anyway -
    /// a notch that has never delivered an item is a notch nothing is coming in on, whether that
    /// is because nothing is connected or because what is connected is empty.
    /// </summary>
    private string Feeding()
    {
        StringBuilder feeding = new StringBuilder();
        StringBuilder silent = new StringBuilder();

        for (int bundle = 0; bundle < Inputs.Length; bundle++)
        {
            long delivered = 0;
            foreach (BlackboxIntake intake in Inputs[bundle])
            {
                delivered += intake.Delivered;
            }

            int notch = bundle / 2;
            string name = (notch < NotchNames.Length ? NotchNames[notch] : "notch " + notch)
                + (bundle % 2 == 0 ? " belt" : " pipe");

            if (delivered > 0)
            {
                feeding.Append(feeding.Length == 0 ? string.Empty : ", ")
                    .Append(name).Append(" (").Append(delivered).Append(')');
            }
            else
            {
                silent.Append(silent.Length == 0 ? string.Empty : ", ").Append(name);
            }
        }

        return "\n  in fed     " + (feeding.Length == 0 ? "nothing" : feeding.ToString())
            + "\n  in silent  " + (silent.Length == 0 ? "nothing" : silent.ToString());
    }

    /// <summary>
    /// The box's live state, for working out why it is not running as fast as it measured.
    /// </summary>
    public string Describe()
    {
        StringBuilder text = new StringBuilder();

        text.Append(Recipe == null ? "no recipe - passing items through" : Recipe.Describe());

        text.Append("\n  fed        ").Append(Pool.Deposits).Append(" items in, holding ")
            .Append(Pool.Stock());

        text.Append("\n  outputs    ").Append(AttachedLanes()).Append(" of ")
            .Append(OutputLanes.Count).Append(" lanes attached, ")
            .Append(FreeSlots(fluid: false)).Append(" belt and ")
            .Append(FreeSlots(fluid: true)).Append(" pipe slots free");

        text.Append(Wiring());

        if (Recipe == null)
        {
            return text.ToString();
        }

        text.Append("\n  ran        ").Append(CyclesRun).Append(" cycles");
        text.Append("\n  stalled    ").Append(StarvedOfStock).Append("x with no stock, ")
            .Append(OutOfRoom).Append("x with nowhere to put the result");

        if (CyclesDiscarded > 0.5f)
        {
            text.Append("\n  LOST       ").Append(CyclesDiscarded.ToString("0"))
                .Append(" cycles of progress that were never run - the box is being updated"
                    + "\n             too rarely to hold this rate");
        }

        return text.ToString();
    }

    public void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser
    {
        foreach (ItemLaneBundle<BlackboxIntake> bundle in Inputs)
        {
            bundle.TraverseLanes(traverser);
        }

        foreach (ItemLaneBundle<BlackboxLane> bundle in Outputs)
        {
            bundle.TraverseLanes(traverser);
        }
    }

    public void ClearContent()
    {
        foreach (ItemLaneBundle<BlackboxIntake> bundle in Inputs)
        {
            bundle.Clear();
        }

        foreach (ItemLaneBundle<BlackboxLane> bundle in Outputs)
        {
            bundle.Clear();
        }

        Pool.Clear();
    }
}

/// <summary>
/// Builds the simulation for one platform size.
///
/// A separate instance per size, because the number of bundles is fixed by the definition and the
/// simulation has to match it.
///
/// This is the stateful form of the builder, which is what makes a box survive a reload: the game
/// creates the state, fills it from the save, and hands it here to be built around. The stateless
/// form this used to use had nowhere to put anything, so a reloaded box came back empty and
/// reverted to passing items through.
/// </summary>
public class BlackboxIslandSimulationFactory
    : IIslandSimulationFactoryBuilder<BlackboxIslandSimulation, BlackboxIslandState,
        BlackboxIslandSimulationFactory.Configuration>
{
    /// <summary>Nothing to configure, but the builder takes one.</summary>
    public class Configuration
    {
    }

    private class Factory : IFactory<BlackboxIslandState, IslandInstance, BlackboxIslandSimulation>
    {
        private readonly int Bundles;
        private readonly string[] NotchNames;

        public Factory(int bundles, string[] notchNames)
        {
            Bundles = bundles;
            NotchNames = notchNames;
        }

        public BlackboxIslandSimulation Produce(BlackboxIslandState state, IslandInstance island)
        {
            return new BlackboxIslandSimulation(state, Bundles, NotchNames);
        }
    }

    private readonly int Bundles;
    private readonly string[] NotchNames;

    public BlackboxIslandSimulationFactory(int bundles, string[] notchNames)
    {
        Bundles = bundles;
        NotchNames = notchNames;
    }

    public IFactory<BlackboxIslandState, IslandInstance, BlackboxIslandSimulation> BuildFactory(
        SimulationSystemsDependencies dependencies, out Configuration config)
    {
        config = new Configuration();
        return new Factory(Bundles, NotchNames);
    }
}
