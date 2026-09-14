using System;
using System.Collections.Generic;
using System.IO;
using Core.Collections;
using Game.Core.Blueprint.Exporter;
using Game.Core.Blueprint;
using Game.Core.Simulation;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// The mod's commands. Registered directly as an <see cref="IConsoleRewirer"/> so they keep
/// a short prefix instead of being named after the assembly.
/// </summary>
public class BlackboxCommands : IConsoleRewirer
{
    private const string Prefix = "pbx.";

    private readonly ILogger Logger;
    private readonly SessionServices Session;
    private readonly ToolbarMap Toolbar;
    private readonly BlackboxPlacement Placement;

    public BlackboxCommands(ILogger logger, SessionServices session, ToolbarMap toolbar,
        BlackboxPlacement placement)
    {
        Logger = logger;
        Session = session;
        Toolbar = toolbar;
        Placement = placement;
    }

    private static readonly string[] Ids = { "analyze", "ports", "predict", "sandbox", "measure", "recipe", "toolbar", "capture", "export", "box", "limits", "relink" };

    public void RegisterCommands(IDebugConsole console)
    {
        Register(console, "analyze", null, Analyze);
        Register(console, "ports", null, ListPorts);
        Register(console, "predict", null, Predict);
        Register(console, "sandbox", null, Sandbox);
        Register(console, "measure", null, Measure);
        Register(console, "recipe", null, Recipe);
        Register(console, "box", null, ShowBoxes);
        Register(console, "relink", null, Relink);
        Register(console, "limits", new DebugConsole.StringOption("set"), Limits);
        Register(console, "toolbar", null, ShowToolbar);
        Register(console, "capture", new DebugConsole.StringOption("name"), Capture);
        Register(console, "export", new DebugConsole.StringOption("file"), Export);
    }

    /// <summary>
    /// Takes the commands back out, so a disposed instance leaves nothing behind that would
    /// keep dispatching into an assembly that is on its way out.
    /// </summary>
    public void UnregisterCommands(IDebugConsole console)
    {
        foreach (string id in Ids)
        {
            Forget(console, id);
        }
    }

    /// <summary>
    /// The console has no unregister of its own and its backing dictionary throws on a
    /// duplicate key, so an id has to be cleared before it can be claimed again.
    /// </summary>
    private static void Forget(IDebugConsole console, string id)
    {
        (console as DebugConsole)?.Commands.Remove(Prefix + id);
    }

    /// <summary>
    /// Prints the toolbar tree with the index path of every element.
    ///
    /// Toolbar positions are index paths and cannot be looked up by id, so this is how to
    /// find out where an entry actually needs to go rather than guessing - a wrong path
    /// inserts silently and looks exactly like a failed registration.
    /// </summary>
    private void ShowToolbar(DebugConsole.CommandContext context)
    {
        string tree;
        if (!Toolbar.TryGet(out tree))
        {
            context.Output?.Invoke("The toolbar has not been built yet - load a save first.");
            return;
        }

        context.Output?.Invoke(tree);
        Report("toolbar", tree, context);
    }

    /// <summary>What the selection looks like from outside: size, and what crosses the boundary.</summary>
    private void Analyze(DebugConsole.CommandContext context)
    {
        if (!TrySelection(context, out IMapModel map, out ISelection<IslandModel> selection))
        {
            return;
        }

        context.Output?.Invoke(SelectionAnalysis.Of(map, selection).Describe());
    }

    /// <summary>Every boundary-crossing port, so a wrong count can be traced to a platform.</summary>
    private void ListPorts(DebugConsole.CommandContext context)
    {
        if (!TrySelection(context, out IMapModel map, out ISelection<IslandModel> selection))
        {
            return;
        }

        SelectionAnalysis analysis = SelectionAnalysis.Of(map, selection);
        if (analysis.ExternalPorts.Count == 0)
        {
            context.Output?.Invoke("No ports cross the boundary - this selection is self-contained.");
            return;
        }

        // Listed by notch rather than one port per line, because the notch is the unit a
        // blackbox has to reproduce - and seeing the grouping is what makes it checkable.
        context.Output?.Invoke(analysis.Notches.Describe());
    }

    /// <summary>
    /// What the game itself predicts will pass through the selection's ports.
    ///
    /// This is the experiment the rest of the design rests on: if the shapes leaving a
    /// selection can be read straight off the game's prediction graph, then a blackbox never
    /// has to reconstruct a connector graph or compose operations by hand.
    /// </summary>
    private void Predict(DebugConsole.CommandContext context)
    {
        if (!TrySelection(context, out IMapModel map, out ISelection<IslandModel> selection))
        {
            return;
        }

        if (!Session.TryGetPredictionSimulator(out ISimulator predictions))
        {
            context.Output?.Invoke("No prediction simulation is running - switch shape "
                + "predictions on in the game's settings and try again.");
            return;
        }

        SelectionAnalysis analysis = SelectionAnalysis.Of(map, selection);
        string report = PredictionReader.Of(map, predictions, selection, analysis).Describe();

        context.Output?.Invoke(report);
        Report("predict", report, context);
    }

    /// <summary>
    /// Puts a report somewhere it can be copied out of. The in-game console cannot be
    /// selected from and the game logs only the command name, not its output, so a report
    /// worth comparing between runs goes to the log and to a file as well.
    /// </summary>
    private void Report(string name, string report, DebugConsole.CommandContext context)
    {
        Logger.Info?.Log(Prefix + name + "\n" + report);

        try
        {
            string path = Path.Combine(GameEnvironment.DataPath, "blackbox");
            Directory.CreateDirectory(path);

            string file = Path.Combine(path, name + ".txt");
            File.WriteAllText(file, report);

            context.Output?.Invoke("(also written to " + file + ")");
        }
        catch (Exception exception)
        {
            // The console already has the report; failing to file a copy is not worth
            // interrupting the command over.
            Logger.Exception?.LogException(exception);
        }
    }

    /// <summary>
    /// Stands up a private copy of the selection and runs it on its own.
    ///
    /// This is the experiment the rate half of the design rests on. Predictions say which
    /// shapes a blackbox emits but nothing about how fast; a sandbox can be saturated and
    /// timed without touching the factory the player is looking at. Before any of that is
    /// worth writing, a second simulator has to actually run - so this builds one, ticks it,
    /// and reports whether the game wired it up.
    /// </summary>
    private void Sandbox(DebugConsole.CommandContext context)
    {
        if (!TrySelection(context, out IMapModel map, out ISelection<IslandModel> selection))
        {
            return;
        }

        if (!Session.TryGetOrchestrator(out GameSessionOrchestrator orchestrator))
        {
            context.Output?.Invoke("No session is running yet - load a save first.");
            return;
        }

        BlackboxSandbox sandbox;
        string failure;
        if (!BlackboxSandbox.TryBuild(orchestrator, selection, Logger, out sandbox, out failure))
        {
            context.Output?.Invoke(failure);
            return;
        }

        using (sandbox)
        {
            string before = sandbox.Describe();

            try
            {
                // A second of simulation is enough to show it runs without being long enough
                // to hurt if it goes wrong.
                for (int i = 0; i < Seconds; i++)
                {
                    sandbox.Advance(Ticks.OneSecond);
                }
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
                context.Output?.Invoke(before + "\n\nBuilt, but ticking threw: " + exception.Message
                    + "\nSee the log for the stack.");
                return;
            }

            Report("sandbox", sandbox.Describe() + "\n\nIt runs. Measuring rates is the next step.", context);
            context.Output?.Invoke(sandbox.Describe());
        }
    }

    /// <summary>
    /// Saturates a private copy of the selection and counts what leaves it.
    ///
    /// This is the rate half of a recipe. Predictions say which shapes come out; running a
    /// copy flat out with every input held full and every output drained says how many.
    /// </summary>
    private void Measure(DebugConsole.CommandContext context)
    {
        if (!TrySelection(context, out IMapModel map, out ISelection<IslandModel> selection))
        {
            return;
        }

        if (!Session.TryGetOrchestrator(out GameSessionOrchestrator orchestrator))
        {
            context.Output?.Invoke("No session is running yet - load a save first.");
            return;
        }

        if (!Session.TryGetPredictionSimulator(out ISimulator predictions))
        {
            context.Output?.Invoke("No prediction simulation is running - the sandbox needs it "
                + "to know which shapes to feed. Switch shape predictions on in the settings.");
            return;
        }

        BlackboxSandbox sandbox;
        string failure;
        if (!BlackboxSandbox.TryBuild(orchestrator, selection, Logger, out sandbox, out failure))
        {
            context.Output?.Invoke(failure);
            return;
        }

        using (sandbox)
        {
            try
            {
                SandboxMeter meter = SandboxMeter.Attach(sandbox,
                    PredictionReader.FeedFor(map, predictions, selection));

                RunToCompletion(meter, sandbox);

                string report = sandbox.Describe() + "\n\n" + meter.Describe();
                context.Output?.Invoke(report);
                Report("measure", report, context);
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
                context.Output?.Invoke("Measuring threw: " + exception.Message
                    + "\nSee the log for the stack.");
            }
        }
    }

    /// <summary>
    /// Derives a recipe from a blueprint rather than from platforms on the map.
    ///
    /// This is the path a blackbox actually needs. A blueprint shared as a string carries
    /// buildings and no recipe, so a recipe has to be re-derivable from the blueprint alone -
    /// which means expanding it into a private world, standing a prediction graph over that
    /// world to learn what its ports carry, and saturating it to learn how fast.
    ///
    /// Running this on a selection <c>pbx.measure</c> was just run on is the check that
    /// matters: the same factory reached two different ways should give the same numbers.
    /// </summary>
    private void Recipe(DebugConsole.CommandContext context)
    {
        if (!TrySelection(context, out IMapModel map, out ISelection<IslandModel> selection))
        {
            return;
        }

        if (!Session.TryGetOrchestrator(out GameSessionOrchestrator orchestrator))
        {
            context.Output?.Invoke("No session is running yet - load a save first.");
            return;
        }

        if (!Session.TryGetPredictionSimulator(out ISimulator predictions))
        {
            context.Output?.Invoke("No prediction simulation is running - it is what says which "
                + "shapes this block is fed. Switch shape predictions on in the settings.");
            return;
        }

        // The probe comes from the live selection, not from the blueprint's copy. A blueprint
        // alone has no predicted inputs at all: predictions flow downstream from sources, and
        // an isolated block has none. What the block does with the probe is then observed.
        SandboxFeed feed = PredictionReader.FeedFor(map, predictions, selection);

        if (feed.ProbeItem == null && feed.DistinctItems > 1)
        {
            context.Output?.Invoke("This block is fed " + feed.DistinctItems + " different "
                + "shapes, and there is nothing yet that says which\nport wants which - so it "
                + "cannot be probed with one of them. Single-shape blocks work.");
            return;
        }

        AnnotatedIslandBlueprint annotated;
        if (!TryBuildBlueprint(context, selection, out annotated))
        {
            return;
        }

        BlackboxSandbox sandbox;
        string failure;
        if (!BlackboxSandbox.TryBuild(orchestrator, annotated.IslandBlueprint, Logger,
            out sandbox, out failure))
        {
            context.Output?.Invoke(failure);
            return;
        }

        using (sandbox)
        {
            try
            {
                SandboxMeter meter = SandboxMeter.Attach(sandbox, feed);
                RunToCompletion(meter, sandbox);

                string report = "from a blueprint of the selection\n" + sandbox.Describe()
                    + "\n\n" + meter.Describe();

                // The ratios are the point of this command now: a rate on its own cannot say
                // whether a block combines two ingredients or transforms one.
                BlackboxRecipe recipe;
                string why;
                report += BlackboxRecipe.TryReduce(meter, out recipe, out why)
                    ? "\n\nas a recipe: " + recipe.Describe()
                    : "\n\nNo recipe: " + why + ".";

                context.Output?.Invoke(report);
                Report("recipe", report, context);
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
                context.Output?.Invoke("Deriving the recipe threw: " + exception.Message
                    + "\nSee the log for the stack.");
            }
        }
    }

    /// <summary>
    /// Runs a meter straight through, for a command the player deliberately typed.
    ///
    /// The automatic path spreads this across frames, because it fires on its own and must not
    /// freeze the game. A command is different: the player asked for it and is waiting for the
    /// answer, so blocking is the expected behaviour rather than a fault.
    /// </summary>
    private static void RunToCompletion(SandboxMeter meter, BlackboxSandbox sandbox)
    {
        MeasurementLimits limits = MeasurementLimits.Current.Copy();
        meter.Begin(limits);

        while (!meter.PumpMeasure(sandbox, limits.StepsPerWindow))
        {
        }
    }

    /// <summary>
    /// Shows or changes the measurement limits.
    ///
    /// One set of numbers cannot cover the range of things people compact: a painter produces in
    /// fourteen seconds, a Make Anything Machine can take five minutes just to saturate. Rather
    /// than guess, they are settable.
    /// </summary>
    private void Limits(DebugConsole.CommandContext context)
    {
        string argument = context.GetString(0);

        if (string.IsNullOrWhiteSpace(argument))
        {
            context.Output?.Invoke("measurement limits (pbx.limits \"name value\" to change):\n"
                + MeasurementLimits.Current.Describe());
            return;
        }

        string[] parts = argument.Trim().Split(' ');
        int value;

        if (parts.Length != 2 || !int.TryParse(parts[1], out value))
        {
            context.Output?.Invoke("Give a name and a number, as in: pbx.limits \"giveup 400\"");
            return;
        }

        string problem;
        if (!MeasurementLimits.Current.TrySet(parts[0], value, out problem))
        {
            context.Output?.Invoke("Cannot set \"" + parts[0] + "\": " + problem + ".\n\n"
                + MeasurementLimits.Current.Describe());
            return;
        }

        context.Output?.Invoke(parts[0] + " is now " + value + ".\n\n"
            + MeasurementLimits.Current.Describe());
    }

    /// Predictions propagate one hop per pass, so this has to exceed the depth of the graph.
    private const int PredictionPasses = 200;

    /// Long enough to show the thing runs, short enough not to stall a frame if it misbehaves.
    private const int Seconds = 5;

    /// <summary>
    /// Rebuilds every placed box's connections to the belts and pipes around it.
    ///
    /// A box loaded from a save can come back with its connections drawn on the map but nothing
    /// wired to its lanes, which leaves it full and idle. Deleting and replacing one of its belts
    /// fixes that one belt; this does the same for all of them at once.
    /// </summary>
    private void Relink(DebugConsole.CommandContext context)
    {
        string report = Placement.RelinkAll();
        context.Output?.Invoke(report);
        Report("relink", report, context);
    }

    /// <summary>
    /// What every placed box is doing. Read this when a box runs below the rate it measured -
    /// it reports the stalls rather than leaving the shortfall to be reasoned about.
    /// </summary>
    private void ShowBoxes(DebugConsole.CommandContext context)
    {
        string report = Placement.DescribeBoxes();
        context.Output?.Invoke(report);
        Report("box", report, context);
    }

    /// <summary>Saves the selection into the player's blueprint library.</summary>
    private void Capture(DebugConsole.CommandContext context)
    {
        if (!TrySelection(context, out IMapModel map, out ISelection<IslandModel> selection))
        {
            return;
        }

        if (!Session.TryGetLibrary(out IBlueprintLibrary library))
        {
            context.Output?.Invoke("The blueprint library is not available yet - load a save first.");
            return;
        }

        string name = context.GetString(0);
        if (string.IsNullOrWhiteSpace(name))
        {
            context.Output?.Invoke("Give it a name: " + Prefix + "capture my-factory");
            return;
        }

        if (!TryBuildBlueprint(context, selection, out AnnotatedIslandBlueprint annotated))
        {
            return;
        }

        if (library.TrySaveEntry(name, annotated, library.RootEntry, allowOverride: true, version: 5))
        {
            SelectionAnalysis analysis = SelectionAnalysis.Of(map, selection);
            context.Output?.Invoke("Saved \"" + name + "\" to the blueprint library: "
                + analysis.IslandCount + " platforms, " + analysis.BuildingCount + " buildings, "
                + analysis.ExternalPorts.Count + " ports crossing the boundary.");
        }
        else
        {
            context.Output?.Invoke("The library refused to save it - the name may already be taken.");
        }
    }

    /// <summary>Writes the shareable blueprint string to a file next to the save games.</summary>
    private void Export(DebugConsole.CommandContext context)
    {
        if (!TrySelection(context, out IMapModel map, out ISelection<IslandModel> selection))
        {
            return;
        }

        if (!Session.TryGetExporter(out IBlueprintExporter exporter))
        {
            context.Output?.Invoke("The blueprint exporter is not available yet - load a save first.");
            return;
        }

        if (!TryBuildBlueprint(context, selection, out AnnotatedIslandBlueprint annotated))
        {
            return;
        }

        try
        {
            string exported = exporter.Export(annotated);
            string path = Path.Combine(GameEnvironment.DataPath, "blackbox");
            Directory.CreateDirectory(path);

            string file = Path.Combine(path, SanitiseFileName(context.GetString(0)) + ".txt");
            File.WriteAllText(file, exported);

            context.Output?.Invoke("Wrote " + exported.Length + " characters to " + file);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            context.Output?.Invoke("Export failed - see the log.");
        }
    }

    private bool TryBuildBlueprint(DebugConsole.CommandContext context,
        ISelection<IslandModel> selection, out AnnotatedIslandBlueprint annotated)
    {
        annotated = null;

        try
        {
            IslandBlueprint blueprint = IslandBlueprint.FromSelection(
                selection, GameHelper.Core.DataSerializers);
            annotated = AnnotatedIslandBlueprint.WithoutMeta(blueprint);
            return true;
        }
        catch (Exception exception)
        {
            // Blueprints refuse some selections outright - overlapping entries, or content
            // the current game mode does not define.
            Logger.Exception?.LogException(exception);
            context.Output?.Invoke("Could not build a blueprint from this selection: " + exception.Message);
            return false;
        }
    }

    private static bool TrySelection(DebugConsole.CommandContext context,
        out IMapModel map, out ISelection<IslandModel> selection)
    {
        map = null;
        selection = null;

        Player player = GameHelper.Core?.LocalPlayer;
        map = player?.CurrentMap;
        if (map == null)
        {
            context.Output?.Invoke("No map loaded.");
            return false;
        }

        selection = player.InteractionState.IslandSelection;
        if (selection.Count == 0)
        {
            context.Output?.Invoke("Select one or more platforms first (space view, drag a selection).");
            return false;
        }

        return true;
    }

    private static string SanitiseFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "blackbox";
        }

        List<char> kept = new List<char>(name.Length);
        char[] invalid = Path.GetInvalidFileNameChars();

        foreach (char c in name)
        {
            kept.Add(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return new string(kept.ToArray());
    }

    /// One command failing to register must not take the rest of them down with it.
    private void Register(IDebugConsole console, string id, DebugConsole.ConsoleOption option,
        Action<DebugConsole.CommandContext> handler)
    {
        try
        {
            // Registering twice is the ordinary case under a hot reload, and without this the
            // console would go on dispatching to the previous assembly.
            Forget(console, id);

            if (option == null)
            {
                console.Register(Prefix + id, handler);
            }
            else
            {
                console.Register(Prefix + id, option, handler);
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }
}
