using System;
using System.Collections.Generic;
using Game.Core.Blueprint;
using Game.Core.Blueprint.Exporter;
using Game.Core.Blueprint.Importer;
using Game.Core.Map.Simulation;
using Game.Core.Map.Simulation.Clustering;
using Game.Core.Simulation;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// Links a blueprint being pasted to the blackbox that stands in for it, and measures the
/// factory only once a placed box knows what it is being fed.
///
/// The split matters. A blueprint's **boundary** - how many notches, which way they face,
/// and therefore how big the platform has to be - is a property of the blueprint alone, so it
/// can be read the moment the key is pressed, cheaply, without running anything. Its **rate
/// and shapes** are not: those depend on the input, so measuring before the box is connected
/// produces a rate for a shape that may never arrive.
///
/// So pressing the key reads the boundary and remembers the blueprint. Measurement waits
/// until a box is placed, wired up and holding a real item - which is also what makes
/// re-measuring after an input change a natural extension rather than a special case.
/// </summary>
public class BlackboxPlacement
{
    /// <summary>
    /// Turns a blueprint into savable text and back. Supplied by the mod, because a placement
    /// needs both halves: the text goes into the box's state when it is placed, and comes back
    /// out of it on load.
    /// </summary>
    private readonly SessionServices Session;

    /// <summary>
    /// A placed box, the factory it stands for, and what it has been measured against.
    ///
    /// Kept for the life of the session rather than discarded once measured, because a box's
    /// recipe is only valid for the input it was measured with. Feed it something else and it
    /// has to measure again - which is the whole reason measurement waits until after
    /// placement.
    /// </summary>
    private class Placed
    {
        public BlackboxIslandSimulation Box;
        public IslandBlueprint Blueprint;

        /// The wrapper the simulation graph knows this box by, needed to ask for it to be
        /// re-connected.
        public ILocalizedSimulation Localized;

        /// How many times its connections have been rebuilt, so a box that cannot be wired at all
        /// is not rebuilt on every tick forever.
        public int Relinks;

        /// Whether the "graph was busy" bail has been reported. That branch retries rather than
        /// counting as an attempt, so without this it would either say nothing at all or say it
        /// on every tick.
        public bool ReportedBusy;

        /// The set of ingredients the current recipe was measured against, or empty before the
        /// first measurement.
        public string MeasuredSignature = string.Empty;

        /// The set currently waiting to settle, and for how many polls it has held.
        public string Settling = string.Empty;
        public int Held;

        /// Recipes already worked out for this box, by the ingredient set they were measured
        /// with. A null entry means that set could not be measured, so the box passes items
        /// through - remembered so an unusable input is not re-measured every tick it arrives.
        public readonly Dictionary<string, BlackboxRecipe> Known =
            new Dictionary<string, BlackboxRecipe>();
    }

    /// Polled rather than registered, so it has to dodge the vanilla bindings by hand: C is
    /// the pipette, and pressing it during a placement plays an error sound that buries
    /// whatever this reports. M appears to be free.
    ///
    /// A real keybinding - rebindable, visible in settings, and consuming the input so it
    /// cannot double-fire with a vanilla action - is the right answer once the flow settles.
    private const KeyCode CompactKey = KeyCode.M;

    private readonly ILogger Logger;

    /// The blueprint the next placed box will stand in for, and its savable form.
    private IslandBlueprint Pending;
    private string PendingText;

    private readonly List<Placed> Boxes = new List<Placed>();

    /// <summary>
    /// Boxes that turned up without a blueprint yet, which is every box coming back from a save.
    /// Looked at again each tick until they either produce blueprint text or turn out to have
    /// been placed from the toolbar.
    /// </summary>
    private readonly List<ILocalizedSimulation> Unclassified = new List<ILocalizedSimulation>();

    /// <summary>
    /// How many polls an ingredient set has to hold before it is measured.
    ///
    /// Ingredients do not arrive together. A painter's shape may reach the box a second before
    /// its paint does, and measuring the instant the shape lands would measure a painter with no
    /// paint - which produces nothing, and reads as unmeasurable. Waiting for the set to stop
    /// growing costs a moment and saves a pointless run of the whole factory.
    /// </summary>
    private const int SettlePolls = 120;

    /// How many times a box's connections may be rebuilt before giving up. If two attempts have
    /// not wired it, the fault is not the one this works around.
    private const int MaxRelinks = 1;

    /// Platforms already re-offered this session, by where they sit. Position rather than object
    /// identity, because re-offering replaces the simulation object.
    private readonly HashSet<string> Relinked = new HashSet<string>();

    private ISimulator Watching;
    private bool Busy;

    /// <summary>
    /// The measurement in flight, and the box it is for.
    ///
    /// One at a time, deliberately. Two measurements would each get a slice of the frame and both
    /// would take twice as long, and a player who has just pasted six boxes would rather see the
    /// first one working than all six creeping along together.
    /// </summary>
    private BlackboxMeasurement Running;
    private Placed RunningFor;

    public BlackboxPlacement(ILogger logger, SessionServices session)
    {
        Logger = logger;
        Session = session;
    }

    /// <summary>
    /// Watches a session's simulator for blackbox platforms coming into existence. Called
    /// whenever the session changes, and cheap to call again with the same one.
    /// </summary>
    public void Watch(ISimulator simulator)
    {
        if (ReferenceEquals(Watching, simulator))
        {
            return;
        }

        if (Watching != null)
        {
            Watching.OnSimulationCreated.Unregister(OnSimulationCreated);
            Watching.OnBeforeSimulationDestroyed.Unregister(OnSimulationDestroyed);
        }

        Watching = simulator;
        Boxes.Clear();
        Unclassified.Clear();
        Relinked.Clear();

        if (Watching == null)
        {
            return;
        }

        Watching.OnSimulationCreated.Register(OnSimulationCreated);

        // A box is not built once and left alone: the game throws its simulation away and builds
        // a new one whenever the island changes - attaching a belt is enough. Without this, every
        // rebuild left the old one in the list and pbx.box reported three boxes where one stood.
        Watching.OnBeforeSimulationDestroyed.Register(OnSimulationDestroyed);

        // Everything already there. The creation event only fires for simulations built while
        // this is listening, and a loaded map builds all of its own during the load - before
        // there has been a tick to notice the session at all. So a box that came out of a save
        // was never announced, was never tracked, and could never be measured again; pbx.box
        // reported nothing because there was nothing in the list.
        int found = 0;

        foreach (ILocalizedSimulation localized in Watching.Simulations)
        {
            if (localized.Simulation is BlackboxIslandSimulation)
            {
                Unclassified.Add(localized);
                found++;
            }
        }

        if (found > 0)
        {
            Logger.Info?.Log("Blackbox: found " + found
                + (found == 1 ? " box" : " boxes") + " already on the map.");
        }
    }

    /// <summary>
    /// Every placed box and what it is doing, which is the only way to tell the three reasons a
    /// box runs below its measured rate apart: no ingredients, nowhere to put the result, or not
    /// being asked to run often enough.
    /// </summary>
    public string DescribeBoxes()
    {
        if (Boxes.Count == 0)
        {
            return "No blackbox has been placed this session. Press M while holding a blueprint, "
                + "then place the box\nthat appears on the cursor.";
        }

        System.Text.StringBuilder text = new System.Text.StringBuilder();

        for (int i = 0; i < Boxes.Count; i++)
        {
            if (i != 0)
            {
                text.Append("\n\n");
            }

            text.Append("box ").Append(i + 1).Append(" of ").Append(Boxes.Count).Append(": ");

            if (Boxes[i].Box == null)
            {
                text.Append("gone");
                continue;
            }

            text.Append(Boxes[i].Box.Describe());

            string progress = RunningOn(Boxes[i].Box);
            if (progress != null)
            {
                text.Append("\n  measuring  ").Append(progress);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Was a hand lever for reconnecting boxes. Disabled - see <see cref="TryRelink"/>.
    /// </summary>
    public string RelinkAll()
    {
        return "Reconnecting boxes from the mod is disabled: it never fixed the wiring and it "
            + "was not safe.\nDeleting and replacing an output belt still works, and is the "
            + "honest workaround for now.";
    }

    /// <summary>Polled from the tick.</summary>
    public void Update(GameSessionOrchestrator orchestrator)
    {
        // A measurement takes long enough that the tick it runs on will see the key still
        // down, or another box ready; re-entering would stall the frame twice over.
        if (Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            if (Input.GetKeyDown(CompactKey))
            {
                Compact(orchestrator);
            }

            // Boxes read back from a save arrive before their state does, so this runs every
            // tick rather than only when the session changes.
            ClassifyNewBoxes();

            // A measurement in flight gets its slice first. Nothing else starts until it lands.
            if (PumpRunning())
            {
                return;
            }

            MeasureWhatIsReady(orchestrator);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Reads the boundary of whatever blueprint is on the cursor and remembers it. Nothing is
    /// simulated here, so it stays cheap even on a large blueprint.
    /// </summary>
    private void Compact(GameSessionOrchestrator orchestrator)
    {
        AnnotatedIslandBlueprint blueprint = CurrentBlueprint();
        if (blueprint == null)
        {
            return;
        }

        BlueprintBoundary boundary;
        string failure;
        if (!BlueprintBoundary.TryOf(orchestrator, blueprint.IslandBlueprint, Logger,
            out boundary, out failure))
        {
            Logger.Info?.Log("Blackbox: " + failure);
            return;
        }

        Pending = blueprint.IslandBlueprint;
        PendingText = Export(blueprint);

        BlackboxIsland.Size size;
        bool fits = BlackboxIsland.TryFit(boundary.Notches.NotchCount, out size);

        Logger.Info?.Log("Blackbox: compacting this blueprint\n" + boundary.Describe()
            + "\nusing the " + size + " platform" + (fits
                ? string.Empty
                : " - the largest there is, and still short for that boundary"));

        // Swap what is on the cursor. Starting a placement stops the current one, so the
        // blueprint ghost becomes a blackbox ghost and the player places it as usual.
        if (!TryStartPlacement(orchestrator, size))
        {
            Logger.Info?.Log("Blackbox: could not switch the cursor to a blackbox - place one "
                + "from the toolbar instead and it will still pick this blueprint up.");
        }
    }

    /// <summary>
    /// Puts a blackbox on the cursor in place of whatever was there.
    ///
    /// Placements are started through their initiator, which is registered under a serialised
    /// id. There is no lookup by island definition, so the id is matched by name - and when
    /// that fails, every registered id is logged, because guessing twice is worse than looking.
    /// </summary>
    private bool TryStartPlacement(GameSessionOrchestrator orchestrator, BlackboxIsland.Size size)
    {
        PlacementInitiatorIdRegistry registry =
            orchestrator.PlayerInteractionOrchestrator?.PlacementInitiatorRegistry;

        if (registry == null)
        {
            return false;
        }

        List<string> seen = new List<string>();

        foreach (PlacementInitiatorId id in registry.InitiatorIds)
        {
            string serial = registry.Serial(id).Id;
            seen.Add(serial ?? "(none)");

            if (!Names(serial, size.DefinitionId.Name))
            {
                continue;
            }

            IPlacementInitiator initiator = registry.GetInitiator(id);
            if (initiator == null || !initiator.CanStartPlacement())
            {
                Logger.Info?.Log("Blackbox: found placer \"" + serial
                    + "\" but it will not start - is the platform unlocked?");
                return false;
            }

            initiator.RequestStartPlacement();
            Logger.Info?.Log("Blackbox: cursor switched to " + size + " (placer \"" + serial + "\")");
            return true;
        }

        Logger.Info?.Log("Blackbox: no placer matched \"" + size.DefinitionId.Name
            + "\". Registered placers:\n  " + string.Join("\n  ", seen));
        return false;
    }

    /// <summary>
    /// Whether a placer id names this definition and not merely something starting with it.
    ///
    /// The 1x1 is called "Blackbox" - it has to keep the id it was first released under, or
    /// saves holding one stop loading - and the larger sizes are "Blackbox2x2" and so on. A
    /// plain substring test therefore matches every size when looking for the 1x1, and the
    /// player gets whichever the registry happened to enumerate first.
    ///
    /// The suffixes all begin with a digit, so requiring the name not to be followed by one is
    /// enough to tell "Blackbox" from "Blackbox2x2".
    /// </summary>
    private static bool Names(string serial, string definition)
    {
        if (serial == null)
        {
            return false;
        }

        int at = serial.IndexOf(definition, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return false;
        }

        int after = at + definition.Length;
        return after >= serial.Length || !char.IsDigit(serial[after]);
    }

    /// <summary>
    /// Measures any placed box whose ingredients have settled - one per call, because a
    /// measurement stalls the frame and doing several at once would stall it several times over.
    ///
    /// A box is measured against the *set* of things it has been fed, not against the item it
    /// happens to be holding. Holding is the wrong question: stock is spent as the recipe
    /// consumes it, so a painter asked what it has would answer "a shape" on one tick and "some
    /// paint" on the next, and measuring each answer in turn gives a box that alternates between
    /// a half-recipe and no recipe at all.
    /// </summary>
    private void MeasureWhatIsReady(GameSessionOrchestrator orchestrator)
    {
        foreach (Placed placed in Boxes)
        {
            if (placed.Box == null)
            {
                continue;
            }

            string signature = placed.Box.Pool.Signature;

            // Nothing has arrived, or this is already what the box is configured for - which is
            // the common case on every tick of a box that is simply running.
            if (signature.Length == 0 || signature == placed.MeasuredSignature)
            {
                continue;
            }

            if (signature != placed.Settling)
            {
                placed.Settling = signature;
                placed.Held = 0;
                continue;
            }

            // Seen before - switching inputs back and forth costs one dictionary lookup rather
            // than a fresh measurement.
            BlackboxRecipe known;
            if (placed.Known.TryGetValue(signature, out known))
            {
                Apply(placed, signature, known, "recalled");
                return;
            }

            if (++placed.Held < SettlePolls)
            {
                continue;
            }

            Measure(orchestrator, placed, signature);
            return;
        }
    }

    private void Apply(Placed placed, string signature, BlackboxRecipe recipe, string how)
    {
        placed.Box.Configure(recipe);
        placed.MeasuredSignature = signature;
        placed.Box.Saved.MeasuredSignature = signature;

        Logger.Info?.Log("Blackbox: " + how + " a recipe for " + placed.Box.Pool.Describe()
            + " - " + (recipe == null ? "no recipe, so items pass through" : recipe.Describe()));
    }

    /// <summary>
    /// Starts measuring a box, which from here on happens a slice of a frame at a time.
    ///
    /// The box is left running on whatever recipe it already had while this goes on. That is the
    /// right thing rather than a compromise: a real factory told to make something else keeps
    /// shipping the old product until its belts clear, so a box that carries on for a minute and
    /// then switches is closer to the truth than one that stops dead.
    /// </summary>
    private void Measure(GameSessionOrchestrator orchestrator, Placed placed, string signature)
    {
        Running = BlackboxMeasurement.Start(orchestrator, placed.Blueprint, placed.Box.Pool,
            Logger, MeasurementLimits.Current.Copy());

        RunningFor = placed;

        Logger.Info?.Log("Blackbox: measuring against what arrived ("
            + placed.Box.Pool.Describe() + ") - this runs in the background.");
    }

    /// <summary>
    /// Gives the measurement in flight its slice, and files the answer when it lands. True while
    /// one is still running, so nothing else is started on top of it.
    /// </summary>
    private bool PumpRunning()
    {
        if (Running == null)
        {
            return false;
        }

        // The box it was for can be gone by now - deleted, or the session reloaded.
        if (RunningFor == null || RunningFor.Box == null || !Boxes.Contains(RunningFor))
        {
            Running.Cancel();
            Running = null;
            RunningFor = null;
            return false;
        }

        if (!Running.Pump())
        {
            return true;
        }

        BlackboxRecipe recipe = Running.Recipe;
        string signature = Running.Signature;

        Logger.Info?.Log("Blackbox: measured " + RunningFor.Box.Pool.Describe() + "\n"
            + Running.Report);

        // A failure is remembered as "no recipe" rather than retried, so a box being fed
        // something it cannot be measured for does not run the whole factory again every tick.
        RunningFor.Known[signature] = recipe;
        Apply(RunningFor, signature, recipe, recipe == null ? "could not measure" : "measured");

        Running = null;
        RunningFor = null;
        return false;
    }

    /// <summary>What the measurement in flight is doing, if there is one.</summary>
    public string RunningOn(object box)
    {
        return Running != null && RunningFor != null && ReferenceEquals(RunningFor.Box, box)
            ? Running.Progress()
            : null;
    }

    /// <summary>
    /// The blueprint currently on the cursor, or null. Island blueprints only - a building
    /// blueprint goes onto a platform and is not something a platform can stand in for.
    ///
    /// The game has a <c>CurrentBlueprintOrDefault()</c> helper for exactly this, but it is
    /// declared in an assembly a mod cannot reference, so the placer is asked directly. The
    /// blueprint hangs off it privately, and <c>BlueprintPlacer</c> is generic in ten
    /// parameters, so naming its type in order to cast is not practical - hence the search.
    /// </summary>
    private static AnnotatedIslandBlueprint CurrentBlueprint()
    {
        EntityPlacementRunner runner = GameHelper.Core?.EntityPlacementRunner as EntityPlacementRunner;
        if (runner == null)
        {
            return null;
        }

        IEntityPlacer placer;
        if (!runner.TryGetCurrentPlacer(out placer) || placer == null)
        {
            return null;
        }

        return FindBlueprint(placer, 0) as AnnotatedIslandBlueprint;
    }

    /// <summary>
    /// Looks for the blueprint a placer is placing. Two levels is enough: the placer holds its
    /// processors, and an island blueprint processor holds the blueprint.
    /// </summary>
    private static IAnnotatedBlueprint FindBlueprint(object target, int depth)
    {
        if (target == null || depth > 2)
        {
            return null;
        }

        System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        foreach (System.Reflection.FieldInfo field in target.GetType().GetFields(flags))
        {
            object value;
            try
            {
                value = field.GetValue(target);
            }
            catch (Exception)
            {
                continue;
            }

            if (value is IAnnotatedBlueprint blueprint)
            {
                return blueprint;
            }

            if (value is System.Collections.IEnumerable items && !(value is string))
            {
                foreach (object item in items)
                {
                    IAnnotatedBlueprint found = FindBlueprint(item, depth + 1);
                    if (found != null)
                    {
                        return found;
                    }
                }

                continue;
            }

            // Only follow the game's own objects; walking into Unity or framework types would
            // wander a long way for nothing.
            if (value != null && value.GetType().Assembly == target.GetType().Assembly)
            {
                IAnnotatedBlueprint found = FindBlueprint(value, depth + 1);
                if (found != null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Notes a blackbox coming into existence so it can be measured once something reaches it.
    /// The blueprint is not consumed: a blueprint compacted once should be placeable many
    /// times, which is the point of sharing a recipe rather than the machines.
    /// </summary>
    private void OnSimulationCreated(ILocalizedSimulation localized)
    {
        if (!(localized.Simulation is BlackboxIslandSimulation box))
        {
            return;
        }

        // Freshly placed off a blueprint: its state is new and writing to it now is safe.
        if (Pending != null)
        {
            box.Saved.Blueprint = PendingText;

            Boxes.Add(new Placed { Box = box, Localized = localized, Blueprint = Pending });
            Logger.Info?.Log("Blackbox: placed one - waiting for something to arrive before "
                + "measuring." + (PendingText == null
                    ? " Its blueprint could not be saved, so it will forget on reload."
                    : string.Empty));
            return;
        }

        // Otherwise this is either a box coming back from a save or one placed from the toolbar,
        // and it is too early to tell: a simulation is built around a state the save has not been
        // read into yet, so its blueprint text is still empty however it got here. Asking again
        // once it has had a chance to be filled in is the only way to know.
        Unclassified.Add(localized);
    }

    /// <summary>
    /// Forgets a box whose simulation the game has discarded.
    ///
    /// The state survives - the game keeps that, which is why a rebuilt box comes straight back
    /// with its recipe - but this simulation object is about to become a dead reference, and a
    /// measurement running against it would be measuring for something that no longer exists.
    /// </summary>
    private void OnSimulationDestroyed(ILocalizedSimulation localized)
    {
        if (!(localized.Simulation is BlackboxIslandSimulation box))
        {
            return;
        }

        for (int i = Unclassified.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(Unclassified[i].Simulation, box))
            {
                Unclassified.RemoveAt(i);
            }
        }

        for (int i = Boxes.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(Boxes[i].Box, box))
            {
                continue;
            }

            if (ReferenceEquals(RunningFor, Boxes[i]))
            {
                Running?.Cancel();
                Running = null;
                RunningFor = null;
            }

            Boxes.RemoveAt(i);
        }
    }

    /// <summary>
    /// Looks again at boxes that were not identifiable when they appeared.
    ///
    /// A loaded box gets its blueprint text some time after the simulation is constructed. One
    /// placed from the toolbar never gets any, and simply stays on this list doing nothing, which
    /// costs a null check per tick.
    /// </summary>
    private void ClassifyNewBoxes()
    {
        for (int i = Unclassified.Count - 1; i >= 0; i--)
        {
            ILocalizedSimulation localized = Unclassified[i];
            BlackboxIslandSimulation box = localized.Simulation as BlackboxIslandSimulation;

            if (box == null || string.IsNullOrEmpty(box.Saved.Blueprint))
            {
                continue;
            }

            Unclassified.RemoveAt(i);

            Logger.Info?.Log("Blackbox: a saved box came back with "
                + (box.Saved.Recipe == null ? "no recipe" : "a recipe")
                + ", signature \"" + box.Saved.MeasuredSignature + "\", holding "
                + box.Pool.Stock() + ", blueprint " + box.Saved.Blueprint.Length + " characters.");

            Adopt(box, localized);
        }
    }

    /// <summary>
    /// Disabled, and left here as a record rather than deleted.
    ///
    /// This removed a box's island from the simulation systems and offered it back, which is what
    /// the game does when an island is registered. It was an attempt at the one bug still
    /// outstanding: a reloaded box comes back with its connections drawn on the map - "select
    /// connected" finds every belt - but with only some of its lanes wired to them, differently
    /// each time the same save is opened.
    ///
    /// It did not fix that, and it was not safe. Re-offering builds a new simulation around a
    /// brand new state, because <c>SimulationStateContainer.New</c> does <c>State = new T()</c>
    /// and nothing reads the save back into it mid-session - so it cost boxes their recipes until
    /// that was carried across by hand. Driving island registration from a hook on the tick is
    /// reaching a long way into the game's own lifecycle, and it should not be done again without
    /// a much better reason than a hypothesis.
    /// </summary>
    private bool TryRelink(Placed placed)
    {
        return false;
    }

    /// <summary>The blackbox simulation now sitting on a given island, after it was rebuilt.</summary>
    private BlackboxIslandSimulation FindBox(IslandInstance island)
    {
        if (Watching == null)
        {
            return null;
        }

        foreach (ILocalizedSimulation localized in Watching.Simulations)
        {
            ConnectableIslandSimulation connectable = localized as ConnectableIslandSimulation;

            if (connectable != null
                && connectable.Island.Transform.Position == island.Transform.Position
                && localized.Simulation is BlackboxIslandSimulation box)
            {
                return box;
            }
        }

        return null;
    }

    /// <summary>
    /// Picks up a box that was saved with a blueprint, so it can be re-measured when its inputs
    /// change rather than being stuck on the recipe it was saved with.
    ///
    /// The recipe itself is applied by the simulation, which takes up whatever its state holds on
    /// its next update. What a box needs from here is the ability to answer a *new* question
    /// later, which means having the blueprint back in a form that can be measured again.
    /// </summary>
    private void Adopt(BlackboxIslandSimulation box, ILocalizedSimulation localized)
    {
        foreach (Placed known in Boxes)
        {
            if (ReferenceEquals(known.Box, box))
            {
                return;
            }
        }

        IslandBlueprint blueprint = Import(box.Saved.Blueprint);

        if (blueprint == null)
        {
            Logger.Info?.Log("Blackbox: a saved box's blueprint could not be read back, so it "
                + "cannot be re-measured. It keeps the recipe it was saved with.");
            return;
        }

        Placed placed = new Placed
        {
            Box = box,
            Localized = localized,
            Blueprint = blueprint,
            MeasuredSignature = box.Saved.MeasuredSignature ?? string.Empty
        };

        // What it already knew, so a reload does not throw away a measurement.
        if (box.Saved.Recipe != null && placed.MeasuredSignature.Length > 0)
        {
            placed.Known[placed.MeasuredSignature] = box.Saved.Recipe;
        }

        Boxes.Add(placed);

        Logger.Info?.Log("Blackbox: picked up a saved box - "
            + (box.Saved.Recipe == null
                ? "it has no recipe yet."
                : box.Saved.Recipe.Describe()));
    }

    /// <summary>
    /// A blueprint as text. Null rather than throwing: a box that cannot save its blueprint still
    /// works for this session, and saying so is better than refusing to place it.
    /// </summary>
    private string Export(AnnotatedIslandBlueprint blueprint)
    {
        try
        {
            IBlueprintExporter exporter;
            if (!Session.TryGetImporter(out _) || !Session.TryGetExporter(out exporter))
            {
                return null;
            }

            return exporter.Export(blueprint);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            return null;
        }
    }

    private IslandBlueprint Import(string text)
    {
        try
        {
            IBlueprintImporter importer;
            if (string.IsNullOrEmpty(text) || !Session.TryGetImporter(out importer))
            {
                return null;
            }

            IAnnotatedBlueprint imported;
            int version;
            BlueprintException failure;

            if (!importer.TryImport(text, out imported, out version, out failure))
            {
                Logger.Info?.Log("Blackbox: could not read a saved blueprint back: "
                    + failure?.Message);
                return null;
            }

            return (imported as AnnotatedIslandBlueprint)?.IslandBlueprint;
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            return null;
        }
    }
}
