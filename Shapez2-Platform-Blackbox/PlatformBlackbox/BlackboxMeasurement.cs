using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Game.Content.Features.Fluids;
using Game.Core.Blueprint;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// One measurement, run a few milliseconds at a time.
///
/// Measuring a factory means running a copy of it until its output rate settles, and that is
/// minutes of simulated time - five or more for a Make Anything Machine, which takes that long
/// just to saturate. Done in a single call it freezes the game for as long as it takes, and the
/// bigger the blueprint the worse it gets: three thousand buildings advanced nine thousand times
/// is tens of seconds of work.
///
/// So it is a job rather than a function. The caller gives it a slice of each frame and it
/// remembers where it was. Nothing about the result changes - the same steps happen in the same
/// order - but the game keeps running, and the box being measured carries on with whatever recipe
/// it already had.
///
/// That the wall-clock cost is spread rather than removed is fine, because of what measurements
/// wait on. A box re-measures when its ingredients change or, for a MAM, when the global signal
/// changes - and a signal follows the operator shape, which turns over every five to twenty
/// minutes. A minute of spread-out work against that is nothing.
/// </summary>
public class BlackboxMeasurement
{
    private enum Stage
    {
        /// Trying candidate assignments of ingredients to input sides, one at a time.
        Sifting,

        /// Running the winner properly.
        Measuring,

        Done
    }

    private readonly GameSessionOrchestrator Orchestrator;
    private readonly IslandBlueprint Blueprint;
    private readonly ILogger Logger;
    private readonly MeasurementLimits Limits;

    /// What the box has been fed, which is what the copy gets fed.
    private readonly List<IBeltItem> Shapes = new List<IBeltItem>();
    private readonly IFluid Fluid;
    private readonly bool HadNoFluid;

    /// The assignments still to try, and what the ones already tried produced.
    private readonly List<Dictionary<NotchGrouping.Notch, IBeltItem>> Candidates =
        new List<Dictionary<NotchGrouping.Notch, IBeltItem>>();

    private readonly List<int> Worked = new List<int>();
    private readonly List<string> Products = new List<string>();
    private readonly StringBuilder Tried = new StringBuilder();

    private int Candidate;
    private Stage Where;

    private BlackboxSandbox Sandbox;
    private SandboxMeter Meter;
    private int InputSides;

    /// <summary>Set once <see cref="Finished"/> is true. Null means no recipe could be had.</summary>
    public BlackboxRecipe Recipe { get; private set; }
    public string Report { get; private set; }

    public bool Finished
    {
        get { return Where == Stage.Done; }
    }

    /// <summary>The ingredient set this was started for, so the caller can file the answer.</summary>
    public string Signature { get; private set; }

    private BlackboxMeasurement(GameSessionOrchestrator orchestrator, IslandBlueprint blueprint,
        BlackboxPool observed, ILogger logger, MeasurementLimits limits)
    {
        Orchestrator = orchestrator;
        Blueprint = blueprint;
        Logger = logger;
        Limits = limits;
        Signature = observed.Signature;

        foreach (IItem shape in observed.SeenShapes)
        {
            if (shape is IBeltItem belt)
            {
                Shapes.Add(belt);
            }
        }

        Fluid = observed.SeenFluids.Count == 1 ? observed.SeenFluids[0] : null;
        HadNoFluid = observed.SeenFluids.Count == 0;
    }

    /// <summary>
    /// Starts a measurement of a blueprint against what a box has actually been handed.
    ///
    /// The only expensive thing done here is reading the copy's input sides, which needs one
    /// sandbox stood up and thrown away. Everything after this happens in slices.
    /// </summary>
    public static BlackboxMeasurement Start(GameSessionOrchestrator orchestrator,
        IslandBlueprint blueprint, BlackboxPool observed, ILogger logger,
        MeasurementLimits limits)
    {
        BlackboxMeasurement job = new BlackboxMeasurement(orchestrator, blueprint, observed,
            logger, limits);

        job.Plan();
        return job;
    }

    /// <summary>
    /// Works out what has to be tried. One ingredient goes to every port and there is nothing to
    /// decide; several means the copy has to be told which side wants which, and nothing records
    /// that - so it is found by trying.
    /// </summary>
    private void Plan()
    {
        if (Shapes.Count <= 1)
        {
            Candidates.Add(new Dictionary<NotchGrouping.Notch, IBeltItem>());
            Where = Stage.Measuring;
            return;
        }

        List<NotchGrouping.Notch> sides;
        string failure;
        if (!TryReadInputSides(out sides, out failure))
        {
            Fail(failure);
            return;
        }

        InputSides = sides.Count;

        if (sides.Count < Shapes.Count)
        {
            Fail("this box is fed " + Shapes.Count + " different shapes but the factory has only "
                + sides.Count + " input\nside" + (sides.Count == 1 ? string.Empty : "s")
                + " - so there is no way to give each ingredient its own belt.");
            return;
        }

        List<Dictionary<NotchGrouping.Notch, IBeltItem>> ways =
            Assignments(sides, Shapes, Limits.MaxAssignments);

        if (ways == null)
        {
            Fail("this box is fed " + Shapes.Count + " different shapes across " + sides.Count
                + " input sides,\nwhich is more combinations than are worth trying. Stating which "
                + "belt wants which\nwould settle it without any searching.");
            return;
        }

        Candidates.AddRange(ways);
        Where = Stage.Sifting;
    }

    /// <summary>
    /// Runs for up to the configured slice of a frame. True once the job has finished.
    /// </summary>
    public bool Pump()
    {
        if (Where == Stage.Done)
        {
            return true;
        }

        Stopwatch clock = Stopwatch.StartNew();

        try
        {
            // A hundred steps between clock reads: enough that the check costs nothing, few
            // enough that a slow graph cannot overrun the slice by much.
            while (clock.ElapsedMilliseconds < Limits.BudgetMillis && Where != Stage.Done)
            {
                Advance(100);
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            Fail("Measuring the blueprint threw: " + exception.Message);
        }

        return Where == Stage.Done;
    }

    private void Advance(int steps)
    {
        if (Sandbox == null && !TryOpen())
        {
            return;
        }

        if (Where == Stage.Sifting)
        {
            if (Meter.PumpProbe(Sandbox, steps))
            {
                FinishSift();
            }

            return;
        }

        if (Meter.PumpMeasure(Sandbox, steps))
        {
            FinishMeasure();
        }
    }

    /// Stands up a fresh copy for whichever candidate is next. A copy cannot be reused between
    /// candidates: by the time one has run, its belts are full of the wrong shapes.
    private bool TryOpen()
    {
        BlackboxSandbox sandbox;
        string failure;
        if (!BlackboxSandbox.TryBuild(Orchestrator, Blueprint, Logger, out sandbox, out failure))
        {
            Fail(failure);
            return false;
        }

        Sandbox = sandbox;
        Meter = SandboxMeter.Attach(sandbox, Feed(Candidates[Candidate]));
        Meter.Begin(Limits);
        return true;
    }

    private void Close()
    {
        Sandbox?.Dispose();
        Sandbox = null;
        Meter = null;
    }

    /// <summary>
    /// Files what a candidate produced and moves to the next, or picks a winner once they have
    /// all been tried.
    ///
    /// Every candidate is probed before any is measured, because "produces something" does not
    /// single one out. A stacker run with its two ingredients swapped still produces something -
    /// it produces the other shape, base and side reversed - so stopping at the first success
    /// would pick an orientation by accident.
    /// </summary>
    private void FinishSift()
    {
        string made = Meter.Produced ? Name(Meter.Outputs) : null;
        Close();

        if (made == null)
        {
            Tried.Append("\n  ").Append(Describe(Candidates[Candidate])).Append(" -> nothing");
        }
        else
        {
            Worked.Add(Candidate);
            Products.Add(made);
            Tried.Append("\n  ").Append(Describe(Candidates[Candidate])).Append(" -> ").Append(made);
        }

        if (++Candidate < Candidates.Count)
        {
            return;
        }

        if (Worked.Count == 0)
        {
            Fail("none of the " + Candidates.Count + " ways of assigning these " + Shapes.Count
                + " shapes to the factory's\n" + InputSides + " input sides produced anything:"
                + Tried + "\n\nSo this blueprint is not one a box can stand in for yet.");
            return;
        }

        Candidate = Worked[0];
        Where = Stage.Measuring;
    }

    private void FinishMeasure()
    {
        BlackboxRecipe recipe;
        string why;
        bool reduced = BlackboxRecipe.TryReduce(Meter, out recipe, out why);

        StringBuilder text = new StringBuilder();

        if (Candidates.Count > 1)
        {
            text.Append("tried ").Append(Candidates.Count).Append(" ways of assigning ")
                .Append(Shapes.Count).Append(" shapes to ").Append(InputSides)
                .Append(" input sides:").Append(Tried);

            // Different assignments making different things is not a tie to be broken quietly. It
            // means the factory cares which belt gets which shape - a stacker puts one shape on
            // top of the other - and the blueprint does not record which way round it was built.
            bool ambiguous = false;
            foreach (string made in Products)
            {
                ambiguous |= made != Products[0];
            }

            if (ambiguous)
            {
                text.Append("\n\nTHIS FACTORY CARES WHICH BELT GETS WHICH SHAPE. The assignments "
                    + "above make\ndifferent things, and nothing in the blueprint says which one "
                    + "you built - that\nlived in how you wired it up outside. Taking the first:"
                    + "\n  ").Append(Describe(Candidates[Candidate]));
            }

            text.Append("\n\n");
        }

        text.Append(Sandbox.Describe()).Append("\n\n").Append(Meter.Describe());

        if (reduced)
        {
            recipe.Buildings = Sandbox.BuildingCount;
            text.Append("\n\nas a recipe: ").Append(recipe.Describe());
            Recipe = recipe;
        }
        else
        {
            text.Append("\n\nNo recipe: ").Append(why).Append('.');

            if (Meter.UnfedFluidInputs > 0 && HadNoFluid)
            {
                text.Append("\nThis factory has fluid inputs and no fluid has reached the box "
                    + "yet, so\nthere was nothing to feed them. Connect the pipes and it will "
                    + "measure again.");
            }
        }

        Close();
        Report = text.ToString();
        Where = Stage.Done;
    }

    /// <summary>Abandons the job. Called when the box it was for goes away.</summary>
    public void Cancel()
    {
        Close();
        Recipe = null;
        Report = "measurement cancelled";
        Where = Stage.Done;
    }

    /// <summary>Where this has got to, for the box panel and pbx.box.</summary>
    public string Progress()
    {
        switch (Where)
        {
            case Stage.Sifting:
                return "trying ingredient assignment " + (Candidate + 1) + " of "
                    + Candidates.Count + " - " + Seconds() + "s simulated";

            case Stage.Measuring:
                return "measuring - " + Seconds() + "s of at most " + Limits.MaxSeconds
                    + "s simulated";

            default:
                return "done";
        }
    }

    private string Seconds()
    {
        return Meter == null ? "0" : Meter.ElapsedSeconds.ToString("0");
    }

    private void Fail(string report)
    {
        Close();
        Recipe = null;
        Report = report;
        Where = Stage.Done;
    }

    private SandboxFeed Feed(Dictionary<NotchGrouping.Notch, IBeltItem> assignment)
    {
        SandboxFeed feed = new SandboxFeed
        {
            ProbeItem = Shapes.Count == 1 ? Shapes[0] : null,
            ProbeFluid = Fluid
        };

        foreach (KeyValuePair<NotchGrouping.Notch, IBeltItem> pair in assignment)
        {
            feed.ByNotch[pair.Key] = pair.Value;
        }

        return feed;
    }

    /// The input sides of a copy, read from a throwaway one so the plan knows what it is
    /// choosing between before it starts choosing.
    private bool TryReadInputSides(out List<NotchGrouping.Notch> sides, out string failure)
    {
        sides = null;

        BlackboxSandbox sandbox;
        if (!BlackboxSandbox.TryBuild(Orchestrator, Blueprint, Logger, out sandbox, out failure))
        {
            return false;
        }

        using (sandbox)
        {
            sides = SandboxMeter.InputNotches(sandbox);
            failure = null;
            return true;
        }
    }

    private static string Name(List<IBeltItem> outputs)
    {
        List<string> names = new List<string>();

        foreach (IBeltItem item in outputs)
        {
            names.Add(item.ToString());
        }

        names.Sort(StringComparer.Ordinal);
        return names.Count == 0 ? null : string.Join(" | ", names);
    }

    /// <summary>
    /// Every way of handing these shapes to these input sides, minus the ways that leave a shape
    /// unused. Null when there are more than <paramref name="cap"/> of them, because each one
    /// costs a run of the whole factory.
    /// </summary>
    private static List<Dictionary<NotchGrouping.Notch, IBeltItem>> Assignments(
        List<NotchGrouping.Notch> sides, List<IBeltItem> shapes, int cap)
    {
        long total = 1;
        for (int i = 0; i < sides.Count; i++)
        {
            total *= shapes.Count;

            if (total > 4096)
            {
                return null;
            }
        }

        List<Dictionary<NotchGrouping.Notch, IBeltItem>> found =
            new List<Dictionary<NotchGrouping.Notch, IBeltItem>>();

        int[] pick = new int[sides.Count];

        for (long n = 0; n < total; n++)
        {
            long digits = n;
            for (int i = 0; i < sides.Count; i++)
            {
                pick[i] = (int)(digits % shapes.Count);
                digits /= shapes.Count;
            }

            bool[] used = new bool[shapes.Count];
            foreach (int choice in pick)
            {
                used[choice] = true;
            }

            bool everyShape = true;
            foreach (bool wasUsed in used)
            {
                everyShape &= wasUsed;
            }

            if (!everyShape)
            {
                continue;
            }

            Dictionary<NotchGrouping.Notch, IBeltItem> map =
                new Dictionary<NotchGrouping.Notch, IBeltItem>();

            for (int i = 0; i < sides.Count; i++)
            {
                map[sides[i]] = shapes[pick[i]];
            }

            found.Add(map);

            if (found.Count > cap)
            {
                return null;
            }
        }

        return found;
    }

    private static string Describe(Dictionary<NotchGrouping.Notch, IBeltItem> assignment)
    {
        StringBuilder text = new StringBuilder();

        foreach (KeyValuePair<NotchGrouping.Notch, IBeltItem> pair in assignment)
        {
            text.Append(text.Length == 0 ? string.Empty : ", ")
                .Append(pair.Key.Direction).Append('=').Append(pair.Value);
        }

        return text.Length == 0 ? "one ingredient everywhere" : text.ToString();
    }
}
