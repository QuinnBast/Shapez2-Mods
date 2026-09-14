using System.Text;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// How long a measurement is allowed to take, and how hard it is allowed to work.
///
/// These were constants until it became clear that one set of numbers cannot cover the range of
/// things people compact. A painter produces its first shape in fourteen seconds; a Make Anything
/// Machine can take five minutes to saturate, and the old give-up threshold abandoned it after
/// one - not because anything was wrong, but because "nothing has come out yet" reads as starved
/// on a small block and as still filling on a large one, and only the player knows which.
///
/// So they are settings. Set with pbx.limits.
/// </summary>
public class MeasurementLimits
{
    /// <summary>The limits every new measurement starts with.</summary>
    public static MeasurementLimits Current = new MeasurementLimits();

    /// A cap on simulated time, not a target: measuring stops as soon as the rate levels off.
    public int MaxSeconds = 240;

    /// <summary>
    /// How long to wait for a first item before giving up.
    ///
    /// Its own setting rather than a fraction of the cap, because it answers a different
    /// question. A factory being fed the wrong thing produces nothing ever and should be
    /// abandoned quickly; a MAM produces nothing for minutes and then works perfectly.
    /// </summary>
    public int GiveUpSeconds = 60;

    /// Long enough that one item either way is not a large fraction of the window.
    public int WindowSeconds = 10;

    /// <summary>
    /// Inputs are offered items between steps, so the step has to be small enough that a belt
    /// cannot run dry waiting for the next offer. It is also the main cost: this many advances
    /// of the whole graph per simulated second.
    /// </summary>
    public int StepsPerSecond = 30;

    /// How much better than the best so far a window has to be to count as still climbing.
    public float Tolerance = 0.02f;

    /// Windows that have to fail to improve before the rate is called a plateau.
    public int StableWindows = 3;

    /// How long a candidate ingredient assignment gets to produce anything at all.
    public int ProbeSeconds = 120;

    /// How long a candidate keeps running after its first item, so what it makes is read from a
    /// handful of products rather than whichever one happened to arrive first.
    public int SettleSeconds = 10;

    /// <summary>
    /// Milliseconds of simulation to run per frame.
    ///
    /// This is what keeps a measurement from freezing the game. Five minutes of simulated time
    /// across three thousand buildings is tens of seconds of work; spread at a few milliseconds
    /// a frame it finishes in about a minute of wall time and nobody notices. That is fast
    /// enough, because the thing measurements wait on - a global signal changing, or a box being
    /// fed something new - happens on the order of minutes.
    /// </summary>
    public int BudgetMillis = 5;

    /// <summary>How many assignments of ingredients to input sides are worth trying. Each one
    /// runs the factory, so this is a budget on patience rather than a limit on what could be
    /// worked out - a stated mapping needs no search at all.</summary>
    public int MaxAssignments = 12;

    public MeasurementLimits Copy()
    {
        return (MeasurementLimits)MemberwiseClone();
    }

    public int StepsPerWindow
    {
        get { return WindowSeconds * StepsPerSecond; }
    }

    /// <summary>
    /// Applies one named setting. Returns false with a reason rather than throwing, because this
    /// is driven from the console.
    /// </summary>
    public bool TrySet(string name, int value, out string problem)
    {
        if (value < 0)
        {
            problem = "must not be negative";
            return false;
        }

        switch (name?.ToLowerInvariant())
        {
            case "max": MaxSeconds = Positive(value); break;
            case "giveup": GiveUpSeconds = Positive(value); break;
            case "window": WindowSeconds = Positive(value); break;
            case "steps": StepsPerSecond = Positive(value); break;
            case "stable": StableWindows = Positive(value); break;
            case "probe": ProbeSeconds = Positive(value); break;
            case "settle": SettleSeconds = value; break;
            case "budget": BudgetMillis = Positive(value); break;
            case "assignments": MaxAssignments = Positive(value); break;

            default:
                problem = "no such setting";
                return false;
        }

        problem = null;
        return true;
    }

    private static int Positive(int value)
    {
        return value < 1 ? 1 : value;
    }

    public string Describe()
    {
        StringBuilder text = new StringBuilder();

        text.Append("max         ").Append(MaxSeconds)
            .Append("s of simulated time before measuring stops\n");
        text.Append("giveup      ").Append(GiveUpSeconds)
            .Append("s to produce a first item before a run is abandoned\n");
        text.Append("window      ").Append(WindowSeconds).Append("s per rate window\n");
        text.Append("stable      ").Append(StableWindows)
            .Append(" windows without improving before the rate is called steady\n");
        text.Append("steps       ").Append(StepsPerSecond)
            .Append(" simulation steps per simulated second\n");
        text.Append("probe       ").Append(ProbeSeconds)
            .Append("s for a candidate ingredient assignment to produce anything\n");
        text.Append("settle      ").Append(SettleSeconds)
            .Append("s a candidate keeps running after its first item\n");
        text.Append("budget      ").Append(BudgetMillis)
            .Append("ms of simulation per frame\n");
        text.Append("assignments ").Append(MaxAssignments)
            .Append(" ways of assigning ingredients worth trying");

        return text.ToString();
    }
}
