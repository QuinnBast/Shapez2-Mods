using System;
using System.Collections.Generic;
using System.Text;
using Game.Content.Features.Fluids;
using Game.Core.Serialization;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// What a blackbox is currently holding, and everything it has ever been handed.
///
/// A factory is not a set of independent lanes. A painter consumes a shape *and* paint to
/// produce one painted shape, and nothing about that is expressible as "each lane transforms
/// its own item" - the two inputs arrive on different notches, in different quantities, and one
/// of them is a fluid. So items entering a box lose their lane and become stock, a recipe is
/// paid for out of that stock, and what it produces goes to whichever output notch has
/// somewhere to put it.
///
/// The consequence worth having is that side and order stop mattering. Feed shapes to any notch
/// and paint to any other, in any arrangement, and the box works - which is what a stand-in
/// owes the player, since the factory it replaced did not care either.
///
/// Fluid is stocked by volume against its fluid, not by package. Packages are interned per
/// (fluid, size), so counting them would make a half-full crate a different ingredient from a
/// full one; volume is the quantity a recipe can actually be measured in.
/// </summary>
public class BlackboxPool
{
    /// <summary>
    /// How much of any one thing the box will hold before refusing more.
    ///
    /// This is what gives the box backpressure. Without a cap an intake would accept forever,
    /// the belts feeding it would never back up, and a box missing one of its ingredients would
    /// quietly swallow an unbounded amount of the others.
    ///
    /// The floors are for anything the recipe does not mention. For ingredients it does, the cap
    /// is derived from the rate instead - see <see cref="Reserve"/> - because a fixed cap is a
    /// throughput limit in disguise. A box running 72 cycles a second against a sixteen-item
    /// buffer has a fifth of a second of stock, and every gap in delivery it cannot ride out
    /// comes straight off the output rate.
    /// </summary>
    private const int ShapeFloor = 16;
    private const int FluidFloorInPackages = 8;

    /// How much stock to keep for a recipe's own ingredients, in seconds of consumption.
    private const float SecondsOfStock = 2f;

    /// The recipe the caps are sized for, or null before one is measured.
    private BlackboxRecipe Recipe;

    /// How many items have been taken in, for reporting.
    public long Deposits { get; private set; }

    /// <summary>Stock, so a rebuilt box can take on the old one's contents.</summary>
    private Dictionary<IItem, int> ShapeStock
    {
        get { return Shapes; }
    }

    private readonly Dictionary<IItem, int> Shapes = new Dictionary<IItem, int>();
    private readonly Dictionary<IFluid, FluidUnit> Fluids = new Dictionary<IFluid, FluidUnit>();

    /// <summary>
    /// One package of each fluid as it actually arrived.
    ///
    /// Kept so fluid can be handed back out in the size it came in, and as the same interned
    /// instance the rest of the map is using, rather than in a package this mod invented.
    /// </summary>
    private readonly Dictionary<IFluid, FluidPackageItem> Packages =
        new Dictionary<IFluid, FluidPackageItem>();

    /// <summary>
    /// Everything deposited since the box was placed, whether or not it is still in stock.
    ///
    /// This, not the current contents, is what a box gets measured against. Stock empties as
    /// the recipe consumes it, so a box asked "what are you being fed?" on the wrong tick would
    /// answer with whichever ingredient happened to be sitting there - and on a painter that
    /// answer alternates between the shape and the paint, which is enough to make the box
    /// re-measure against one ingredient at a time, forever.
    /// </summary>
    public readonly List<IItem> SeenShapes = new List<IItem>();
    public readonly List<IFluid> SeenFluids = new List<IFluid>();

    /// <summary>
    /// A stable name for the set above, used both to notice a change of input and to recall a
    /// recipe already measured for it. Order-independent, because the order ingredients happen
    /// to arrive in is not part of what the factory does.
    /// </summary>
    public string Signature { get; private set; }

    public BlackboxPool()
    {
        Signature = string.Empty;
    }

    /// <summary>
    /// Sizes the caps for a recipe, so the buffer is measured in time rather than in items.
    ///
    /// Two seconds of stock is enough to ride out the gaps in delivery that a belt naturally
    /// has, and small enough that a box does not hoard a visible fraction of the factory's
    /// throughput. Anything the recipe does not consume keeps the floor.
    /// </summary>
    public void Reserve(BlackboxRecipe recipe)
    {
        Recipe = recipe;
    }

    /// <summary>
    /// Whether there is room for another shape, and separately for more fluid.
    ///
    /// Two answers rather than one, and that distinction is the whole point. A lane reading free
    /// space has not named an item, so it can only be told about the kind of thing it carries -
    /// and a single pool-wide answer meant a full shape buffer reported "no room" to the paint
    /// pipes as well.
    ///
    /// That was a latch, not a hiccup. A painter with no paint cannot run a cycle, so its shapes
    /// never drain, so it stays full, so the pipes stay blocked - one momentary paint shortage
    /// and the box is starved for good. Which is exactly what a box that had run 292 cycles and
    /// then sat at 288/288 shapes and 0L paint was doing.
    /// </summary>
    public bool CrowdedForShapes
    {
        get
        {
            foreach (KeyValuePair<IItem, int> stock in Shapes)
            {
                if (stock.Value < CapFor(stock.Key))
                {
                    return false;
                }
            }

            // An empty pool is not crowded. Nor is one holding only fluid - a box being fed paint
            // and no shapes has to keep accepting shapes.
            return Shapes.Count > 0;
        }
    }

    public bool CrowdedForFluid
    {
        get
        {
            foreach (KeyValuePair<IFluid, FluidUnit> stock in Fluids)
            {
                if (stock.Value < CapFor(stock.Key, PackageFor(stock.Key).Size))
                {
                    return false;
                }
            }

            return Fluids.Count > 0;
        }
    }

    /// How many of this shape the box will hold: two seconds of what the recipe consumes, or
    /// the floor for a shape the recipe has never heard of.
    private int CapFor(IItem shape)
    {
        int perCycle;
        if (Recipe == null || shape == null || !Recipe.ShapesIn.TryGetValue(shape, out perCycle))
        {
            return ShapeFloor;
        }

        float perSecond = perCycle * Recipe.CyclesPerMinute / 60f;
        return Math.Max(ShapeFloor, (int)Math.Ceiling(perSecond * SecondsOfStock));
    }

    /// The same in volume. Paint is the ingredient most likely to be starved by a fixed cap,
    /// because a cycle draws a fraction of a litre and a busy box runs thousands a minute.
    private FluidUnit CapFor(IFluid fluid, FluidUnit packageSize)
    {
        FluidUnit floor = packageSize * FluidFloorInPackages;

        FluidUnit perCycle;
        if (Recipe == null || fluid == null || !Recipe.FluidIn.TryGetValue(fluid, out perCycle))
        {
            return floor;
        }

        long units = (long)(perCycle.Units * (Recipe.CyclesPerMinute / 60f) * SecondsOfStock);
        FluidUnit wanted = new FluidUnit(units);

        return wanted > floor ? wanted : floor;
    }

    /// <summary>Room for another of this, which is what an intake asks before accepting.</summary>
    public bool HasRoomFor(IBeltItem item)
    {
        if (item is FluidPackageItem package)
        {
            return Held(package.Fluid) < CapFor(package.Fluid, package.Size);
        }

        return item is IItem shape && Count(shape) < CapFor(shape);
    }

    public void Deposit(IBeltItem item)
    {
        Deposits++;

        if (item is FluidPackageItem package)
        {
            Fluids[package.Fluid] = Held(package.Fluid) + package.Size;
            Packages[package.Fluid] = package;

            if (!SeenFluids.Contains(package.Fluid))
            {
                SeenFluids.Add(package.Fluid);
                Resign();
            }

            return;
        }

        if (!(item is IItem shape))
        {
            return;
        }

        Shapes[shape] = Count(shape) + 1;

        if (!SeenShapes.Contains(shape))
        {
            SeenShapes.Add(shape);
            Resign();
        }
    }

    public int Count(IItem shape)
    {
        int count;
        return shape != null && Shapes.TryGetValue(shape, out count) ? count : 0;
    }

    public FluidUnit Held(IFluid fluid)
    {
        FluidUnit amount;
        return fluid != null && Fluids.TryGetValue(fluid, out amount) ? amount : FluidUnit.Zero;
    }

    /// <summary>The package this fluid arrives in, or a one-litre stand-in if it never has.</summary>
    public FluidPackageItem PackageFor(IFluid fluid)
    {
        if (fluid == null)
        {
            return null;
        }

        FluidPackageItem package;
        return Packages.TryGetValue(fluid, out package)
            ? package
            : new FluidPackageItem(fluid, FluidUnit.FromLiters(1));
    }

    /// <summary>Whether a whole cycle of this recipe can be paid for right now.</summary>
    public bool CanPay(BlackboxRecipe recipe)
    {
        foreach (KeyValuePair<IItem, int> ingredient in recipe.ShapesIn)
        {
            if (Count(ingredient.Key) < ingredient.Value)
            {
                return false;
            }
        }

        foreach (KeyValuePair<IFluid, FluidUnit> ingredient in recipe.FluidIn)
        {
            if (Held(ingredient.Key) < ingredient.Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Takes one cycle's worth out. Only called once <see cref="CanPay"/> has agreed.</summary>
    public void Pay(BlackboxRecipe recipe)
    {
        foreach (KeyValuePair<IItem, int> ingredient in recipe.ShapesIn)
        {
            Shapes[ingredient.Key] = Count(ingredient.Key) - ingredient.Value;
        }

        foreach (KeyValuePair<IFluid, FluidUnit> ingredient in recipe.FluidIn)
        {
            Fluids[ingredient.Key] = Held(ingredient.Key) - ingredient.Value;
        }
    }

    /// <summary>
    /// Hands back anything in stock, for a box with no recipe to pay.
    ///
    /// An unmeasured box is a pass-through, and a pass-through is just a box that gives back
    /// whatever it was given.
    /// </summary>
    public bool TryTakeAnything(out IBeltItem item)
    {
        foreach (KeyValuePair<IItem, int> stock in Shapes)
        {
            if (stock.Value <= 0 || !(stock.Key is IBeltItem belt))
            {
                continue;
            }

            Shapes[stock.Key] = stock.Value - 1;
            item = belt;
            return true;
        }

        foreach (KeyValuePair<IFluid, FluidUnit> stock in Fluids)
        {
            FluidPackageItem package = PackageFor(stock.Key);
            if (package == null || stock.Value < package.Size)
            {
                continue;
            }

            Fluids[stock.Key] = stock.Value - package.Size;
            item = package;
            return true;
        }

        item = null;
        return false;
    }

    public void Clear()
    {
        Shapes.Clear();
        Fluids.Clear();
    }

    /// <summary>
    /// Takes on another pool's stock and history.
    ///
    /// Needed because reconnecting a platform makes the game build its simulation again, and the
    /// framework hands the new one a brand new state object - <c>SimulationStateContainer.New</c>
    /// does <c>State = new T()</c>. On a load that is harmless, since the save is read into the
    /// fresh object afterwards. Mid-session there is nothing to read it back, so whatever the box
    /// knew has to be carried across by hand or it is simply lost.
    /// </summary>
    public void CopyFrom(BlackboxPool other)
    {
        if (other == null || ReferenceEquals(other, this))
        {
            return;
        }

        Shapes.Clear();
        Fluids.Clear();
        Packages.Clear();
        SeenShapes.Clear();
        SeenFluids.Clear();

        foreach (KeyValuePair<IItem, int> stock in other.Shapes)
        {
            Shapes[stock.Key] = stock.Value;
        }

        foreach (KeyValuePair<IFluid, FluidUnit> stock in other.Fluids)
        {
            Fluids[stock.Key] = stock.Value;
        }

        foreach (KeyValuePair<IFluid, FluidPackageItem> package in other.Packages)
        {
            Packages[package.Key] = package.Value;
        }

        SeenShapes.AddRange(other.SeenShapes);
        SeenFluids.AddRange(other.SeenFluids);
        Deposits = other.Deposits;

        Resign();
    }

    /// <summary>
    /// Writes the pool into a save, or reads it back.
    ///
    /// Both halves matter and for different reasons. **Stock** is the box's contents, and leaving
    /// it out would destroy whatever it was holding every time the game was saved. **What has
    /// been seen** is what the box was measured against, and without it a reloaded box would look
    /// at itself, find an ingredient set it had no record of, and start measuring again for a
    /// recipe it already had.
    ///
    /// Fluid rides as one of its own packages, since the game can serialise a package and the
    /// fluid reads straight back off it.
    /// </summary>
    public void Sync(ISerializationVisitor visitor)
    {
        if (visitor.Writing)
        {
            visitor.WriteInt_4(Shapes.Count);
            foreach (KeyValuePair<IItem, int> stock in Shapes)
            {
                visitor.Serialize(stock.Key as IBeltItem);
                visitor.WriteInt_4(stock.Value);
            }

            visitor.WriteInt_4(Fluids.Count);
            foreach (KeyValuePair<IFluid, FluidUnit> stock in Fluids)
            {
                visitor.Serialize<IBeltItem>(PackageFor(stock.Key));
                visitor.WriteLong_8(stock.Value.Units);
            }

            visitor.WriteInt_4(SeenShapes.Count);
            foreach (IItem shape in SeenShapes)
            {
                visitor.Serialize(shape as IBeltItem);
            }

            visitor.WriteInt_4(SeenFluids.Count);
            foreach (IFluid fluid in SeenFluids)
            {
                visitor.Serialize<IBeltItem>(PackageFor(fluid));
            }

            return;
        }

        Shapes.Clear();
        Fluids.Clear();
        Packages.Clear();
        SeenShapes.Clear();
        SeenFluids.Clear();

        int shapes = visitor.ReadInt_4();
        for (int i = 0; i < shapes; i++)
        {
            IBeltItem item = visitor.Deserialize<IBeltItem>();
            int held = visitor.ReadInt_4();

            if (item is IItem shape)
            {
                Shapes[shape] = held;
            }
        }

        int fluids = visitor.ReadInt_4();
        for (int i = 0; i < fluids; i++)
        {
            IBeltItem item = visitor.Deserialize<IBeltItem>();
            long units = visitor.ReadLong_8();

            if (item is FluidPackageItem package)
            {
                Fluids[package.Fluid] = new FluidUnit(units);
                Packages[package.Fluid] = package;
            }
        }

        int seenShapes = visitor.ReadInt_4();
        for (int i = 0; i < seenShapes; i++)
        {
            if (visitor.Deserialize<IBeltItem>() is IItem shape && !SeenShapes.Contains(shape))
            {
                SeenShapes.Add(shape);
            }
        }

        int seenFluids = visitor.ReadInt_4();
        for (int i = 0; i < seenFluids; i++)
        {
            if (visitor.Deserialize<IBeltItem>() is FluidPackageItem package
                && !SeenFluids.Contains(package.Fluid))
            {
                SeenFluids.Add(package.Fluid);
                Packages[package.Fluid] = package;
            }
        }

        Resign();
    }

    /// <summary>What is in stock right now, which is what says whether a box is starved.</summary>
    public string Stock()
    {
        StringBuilder text = new StringBuilder();

        foreach (KeyValuePair<IItem, int> held in Shapes)
        {
            text.Append(text.Length == 0 ? string.Empty : ", ").Append(held.Value)
                .Append('/').Append(CapFor(held.Key)).Append("x ").Append(held.Key);
        }

        foreach (KeyValuePair<IFluid, FluidUnit> held in Fluids)
        {
            FluidPackageItem package = PackageFor(held.Key);
            text.Append(text.Length == 0 ? string.Empty : ", ")
                .Append(held.Value.LitersApprox.ToString("0.#")).Append('/')
                .Append(CapFor(held.Key, package.Size).LitersApprox.ToString("0.#"))
                .Append("L ").Append(held.Key);
        }

        return text.Length == 0 ? "empty" : text.ToString();
    }

    public string Describe()
    {
        StringBuilder text = new StringBuilder();

        foreach (IItem shape in SeenShapes)
        {
            text.Append(text.Length == 0 ? string.Empty : " + ").Append(shape);
        }

        foreach (IFluid fluid in SeenFluids)
        {
            text.Append(text.Length == 0 ? string.Empty : " + ").Append(fluid);
        }

        return text.Length == 0 ? "nothing yet" : text.ToString();
    }

    private void Resign()
    {
        List<string> parts = new List<string>();

        foreach (IItem shape in SeenShapes)
        {
            parts.Add("s:" + shape);
        }

        foreach (IFluid fluid in SeenFluids)
        {
            parts.Add("f:" + fluid);
        }

        parts.Sort(StringComparer.Ordinal);
        Signature = string.Join("|", parts);
    }
}
