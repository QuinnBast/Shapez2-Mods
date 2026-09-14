using System;
using Game.Core.Blueprint.Exporter;
using Game.Core.Blueprint.Importer;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// Holds the session-scoped services this mod needs but cannot be handed directly.
///
/// The blueprint exporter and library are bound in the game session's dependency
/// container, so a reference to the orchestrator is enough to reach them. They are resolved
/// on first use rather than when the session is captured, which sidesteps any question of
/// whether the binding has happened yet.
/// </summary>
public class SessionServices
{
    private readonly ILogger Logger;

    private GameSessionOrchestrator Orchestrator;
    private IBlueprintExporter Exporter;
    private IBlueprintLibrary Library;

    public SessionServices(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// Takes the session, if it is not the one already held. Returns true when it changed,
    /// so a caller can redo whatever is bound to a particular session.
    /// </summary>
    public bool Capture(GameSessionOrchestrator orchestrator)
    {
        if (ReferenceEquals(Orchestrator, orchestrator))
        {
            return false;
        }

        Orchestrator = orchestrator;
        Exporter = null;
        Library = null;
        return true;
    }

    /// <summary>
    /// The session itself, for the few things that are not bound in the container - the
    /// prediction simulator, and the pieces a sandbox needs to build its own systems.
    /// </summary>
    public bool TryGetOrchestrator(out GameSessionOrchestrator orchestrator)
    {
        orchestrator = Orchestrator;
        return orchestrator != null;
    }

    /// <summary>
    /// The debug console. Not cached: it belongs to the session, and the mod may outlive one.
    /// </summary>
    public bool TryGetConsole(out IDebugConsole console)
    {
        console = Resolve<IDebugConsole>();
        return console != null;
    }

    private IBlueprintImporter Importer;

    public bool TryGetExporter(out IBlueprintExporter exporter)
    {
        Exporter = Exporter ?? Resolve<IBlueprintExporter>();
        exporter = Exporter;
        return exporter != null;
    }

    /// <summary>
    /// The blueprint importer, which turns exported text back into a blueprint.
    ///
    /// Needed because a blackbox stores the factory it replaced as the same text the blueprint
    /// library uses. A structure reference would not survive a reload, and a private encoding
    /// would not survive this mod being updated.
    /// </summary>
    public bool TryGetImporter(out IBlueprintImporter importer)
    {
        Importer = Importer ?? Resolve<IBlueprintImporter>();
        importer = Importer;
        return importer != null;
    }

    public bool TryGetLibrary(out IBlueprintLibrary library)
    {
        Library = Library ?? Resolve<IBlueprintLibrary>();
        library = Library;
        return library != null;
    }

    /// <summary>
    /// The prediction simulator, which is a field on the orchestrator rather than a binding
    /// in the container. The session creates it lazily and throws it away again whenever
    /// shape predictions are switched off, so it is read fresh every time rather than cached.
    /// </summary>
    public bool TryGetPredictionSimulator(out ISimulator predictions)
    {
        predictions = Orchestrator?.PredictionSimulator;
        return predictions != null;
    }

    private T Resolve<T>() where T : class
    {
        if (Orchestrator == null)
        {
            return null;
        }

        try
        {
            return Orchestrator.DependencyContainer.Resolve<T>();
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            return null;
        }
    }
}
