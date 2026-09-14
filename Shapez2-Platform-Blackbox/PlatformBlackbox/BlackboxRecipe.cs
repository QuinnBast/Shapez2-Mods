using System;
using System.Collections.Generic;
using System.Text;
using Game.Content.Features.Fluids;
using Game.Core.Serialization;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// What a blackbox needs in order to stand in for a factory: what one cycle consumes, what it
/// produces, and how many cycles a minute holds.
///
/// Measured rather than declared. A copy of the blueprint is run alone in a private world and
/// saturated, so the figures are the ceiling the real thing could reach rather than whatever it
/// happened to be doing when it was copied.
///
/// The shape of this used to be a single input mapped to a single output, applied per lane. That
/// is enough for a cutter and wrong for everything that combines ingredients: a painter takes a
/// shape *and* paint, in quantities, one of them a fluid, arriving on different notches. So a
/// recipe is now a pair of multisets and a rate, which is the smallest form that can express
/// what a factory actually does.
/// </summary>
public class BlackboxRecipe
{
    /// One cycle's ingredients. Empty for a box that consumes nothing, which is a producer.
    public readonly Dictionary<IItem, int> ShapesIn = new Dictionary<IItem, int>();
    public readonly Dictionary<IFluid, FluidUnit> FluidIn = new Dictionary<IFluid, FluidUnit>();

    /// One cycle's products.
    public readonly Dictionary<IItem, int> ShapesOut = new Dictionary<IItem, int>();

    /// <summary>
    /// How often the box runs a cycle. This, not a per-lane delay, is what sets the rate now -
    /// a delay could only ever express "one item per lane per interval", which says nothing
    /// about a recipe that consumes two things and makes one.
    /// </summary>
    public float CyclesPerMinute;

    /// The measured output rate, kept for reporting.
    public float RatePerMinute;

    /// Time the real factory took to first produce. Not reproduced - a box that held its
    /// output back would need a delay line - but worth carrying so the gap stays visible.
    public Ticks MeasuredLatency;

    public int Buildings;

    /// <summary>Why this recipe is what it is, in one line, for the log.</summary>
    public string Note = "unmeasured";

    /// <summary>
    /// True when the box actually transforms something. A recipe that consumes and produces the
    /// same single shape is a pass-through, and saying so is more use than pretending.
    /// </summary>
    public bool Transforms;

    /// <summary>How many items one cycle emits, which is how much output room it needs.</summary>
    public int OutputCount
    {
        get
        {
            int total = 0;
            foreach (KeyValuePair<IItem, int> product in ShapesOut)
            {
                total += product.Value;
            }

            return total;
        }
    }

    public bool Runnable
    {
        get { return CyclesPerMinute > 0f && OutputCount > 0; }
    }

    /// <summary>
    /// Turns what a saturated copy consumed and produced into ratios and a cycle rate.
    ///
    /// Counts are taken over the plateau only, so they describe the factory running rather than
    /// filling. Dividing every count by the smallest of them gives the ratio per cycle and, at
    /// the same time, the number of cycles the plateau contained - which is where the rate comes
    /// from. Fluid is divided by the same number, because it was consumed by the same cycles.
    /// </summary>
    public static bool TryReduce(SandboxMeter meter, out BlackboxRecipe recipe, out string why)
    {
        recipe = null;

        if (meter.PlateauSeconds <= 0f)
        {
            why = "the copy never held a steady rate, so there are no ratios to read";
            return false;
        }

        int divisor = int.MaxValue;

        foreach (KeyValuePair<IItem, int> consumed in meter.ConsumedShapes)
        {
            if (consumed.Value > 0)
            {
                divisor = Math.Min(divisor, consumed.Value);
            }
        }

        foreach (KeyValuePair<IItem, int> produced in meter.ProducedShapes)
        {
            if (produced.Value > 0)
            {
                divisor = Math.Min(divisor, produced.Value);
            }
        }

        if (divisor == int.MaxValue || divisor <= 0)
        {
            why = "nothing crossed the boundary during the steady window";
            return false;
        }

        recipe = new BlackboxRecipe();

        foreach (KeyValuePair<IItem, int> consumed in meter.ConsumedShapes)
        {
            recipe.ShapesIn[consumed.Key] = PerCycle(consumed.Value, divisor);
        }

        foreach (KeyValuePair<IItem, int> produced in meter.ProducedShapes)
        {
            recipe.ShapesOut[produced.Key] = PerCycle(produced.Value, divisor);
        }

        foreach (KeyValuePair<IFluid, FluidUnit> consumed in meter.ConsumedFluid)
        {
            recipe.FluidIn[consumed.Key] = consumed.Value / divisor;
        }

        if (recipe.OutputCount == 0)
        {
            recipe = null;
            why = "the copy consumed but never produced";
            return false;
        }

        // The plateau contained one cycle per unit of the divisor, which turns a count into a
        // rate without having to guess at a cycle time.
        recipe.CyclesPerMinute = divisor * 60f / meter.PlateauSeconds;
        recipe.RatePerMinute = meter.SteadyRatePerMinute;
        recipe.MeasuredLatency = meter.Latency;
        recipe.Transforms = !SameSingleShape(recipe);
        recipe.Note = recipe.Describe();

        why = null;
        return true;
    }

    /// <summary>
    /// A count reduced to its share of one cycle. Anything that was consumed at all is at least
    /// one per cycle - a ratio rounding to zero would produce a recipe that gets something for
    /// nothing, which is worse than being slightly coarse.
    /// </summary>
    private static int PerCycle(int total, int divisor)
    {
        return total <= 0 ? 0 : Math.Max(1, (int)Math.Round(total / (double)divisor));
    }

    /// One shape in, the same shape out, nothing else: a pass-through wearing a recipe.
    private static bool SameSingleShape(BlackboxRecipe recipe)
    {
        if (recipe.FluidIn.Count > 0 || recipe.ShapesIn.Count != 1 || recipe.ShapesOut.Count != 1)
        {
            return false;
        }

        foreach (KeyValuePair<IItem, int> input in recipe.ShapesIn)
        {
            foreach (KeyValuePair<IItem, int> output in recipe.ShapesOut)
            {
                return ReferenceEquals(input.Key, output.Key) || Equals(input.Key, output.Key);
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the recipe into a save, or reads it back.
    ///
    /// Items go through the visitor's own serialiser rather than being written as shape codes.
    /// The game already knows how to write an <c>IBeltItem</c> - it does it for every item on
    /// every belt - so a recipe costs nothing to store and cannot drift out of step with how
    /// shapes are identified.
    ///
    /// Fluid is stored as one of its packages, because there is a serialiser for a package and
    /// the fluid is simply read back off it. The volume travels separately as a raw unit count.
    /// </summary>
    public void Sync(ISerializationVisitor visitor)
    {
        SyncShapes(visitor, ShapesIn);
        SyncShapes(visitor, ShapesOut);
        SyncFluid(visitor, FluidIn);

        visitor.SyncFloat_4(ref CyclesPerMinute);
        visitor.SyncFloat_4(ref RatePerMinute);
        visitor.SyncInt_4(ref Buildings);
        visitor.SyncBool_1(ref Transforms);

        // Ticks has no primitive of its own here, and seconds is the unit the number is reported
        // in anyway.
        float latency = Ticks.Ratio(MeasuredLatency, Ticks.OneSecond);
        visitor.SyncFloat_4(ref latency);

        if (!visitor.Writing)
        {
            MeasuredLatency = Ticks.FromSeconds(latency);
            Note = Describe();
        }
    }

    private static void SyncShapes(ISerializationVisitor visitor, Dictionary<IItem, int> shapes)
    {
        if (visitor.Writing)
        {
            visitor.WriteInt_4(shapes.Count);

            foreach (KeyValuePair<IItem, int> shape in shapes)
            {
                visitor.Serialize(shape.Key as IBeltItem);
                visitor.WriteInt_4(shape.Value);
            }

            return;
        }

        shapes.Clear();
        int count = visitor.ReadInt_4();

        for (int i = 0; i < count; i++)
        {
            IBeltItem item = visitor.Deserialize<IBeltItem>();
            int howMany = visitor.ReadInt_4();

            if (item is IItem shape)
            {
                shapes[shape] = howMany;
            }
        }
    }

    private static void SyncFluid(ISerializationVisitor visitor, Dictionary<IFluid, FluidUnit> fluids)
    {
        if (visitor.Writing)
        {
            visitor.WriteInt_4(fluids.Count);

            foreach (KeyValuePair<IFluid, FluidUnit> fluid in fluids)
            {
                visitor.Serialize<IBeltItem>(new FluidPackageItem(fluid.Key, fluid.Value));
                visitor.WriteLong_8(fluid.Value.Units);
            }

            return;
        }

        fluids.Clear();
        int count = visitor.ReadInt_4();

        for (int i = 0; i < count; i++)
        {
            IBeltItem item = visitor.Deserialize<IBeltItem>();
            long units = visitor.ReadLong_8();

            if (item is FluidPackageItem package)
            {
                fluids[package.Fluid] = new FluidUnit(units);
            }
        }
    }

    public string Describe()
    {
        StringBuilder text = new StringBuilder();

        Ingredients(text, ShapesIn, FluidIn);
        text.Append(" -> ");
        Ingredients(text, ShapesOut, null);

        text.Append(", ").Append(CyclesPerMinute.ToString("0.##")).Append(" cycles/min");
        text.Append(" (").Append(RatePerMinute.ToString("0")).Append("/min out");

        if (Buildings > 0)
        {
            text.Append(" from ").Append(Buildings).Append(" buildings");
        }

        text.Append(')');

        if (!Transforms)
        {
            text.Append("\nnothing is transformed - the box hands back what it is given");
        }

        if (MeasuredLatency > Ticks.Zero)
        {
            text.Append("\nthe real factory took ")
                .Append(Ticks.Ratio(MeasuredLatency, Ticks.OneSecond).ToString("0.#"))
                .Append("s to first produce, which the box does not reproduce");
        }

        return text.ToString();
    }

    private static void Ingredients(StringBuilder text, Dictionary<IItem, int> shapes,
        Dictionary<IFluid, FluidUnit> fluids)
    {
        bool first = true;

        foreach (KeyValuePair<IItem, int> shape in shapes)
        {
            if (!first)
            {
                text.Append(" + ");
            }

            first = false;
            text.Append(shape.Value).Append("x ").Append(shape.Key);
        }

        if (fluids != null)
        {
            foreach (KeyValuePair<IFluid, FluidUnit> fluid in fluids)
            {
                if (!first)
                {
                    text.Append(" + ");
                }

                first = false;
                text.Append(fluid.Value.LitersApprox.ToString("0.##")).Append("L ")
                    .Append(fluid.Key);
            }
        }

        if (first)
        {
            text.Append("nothing");
        }
    }

}
