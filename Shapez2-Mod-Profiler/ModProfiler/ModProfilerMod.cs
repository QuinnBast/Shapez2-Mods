using JetBrains.Annotations;
using ShapezShifter.Flow;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

/// <summary>
/// Mod Profiler - a development tool.
///
/// This version does one thing: it reports which profiling APIs the game's runtime actually
/// implements, so the profiler proper can be built against what exists.
///
/// That step is not ceremony. No off-the-shelf profiler can attach to this process - every
/// agent worth having (Pyroscope, Datadog, dotTrace's attach mode) hooks the CLR Profiling
/// API, which Mono does not implement; dotnet-trace needs EventPipe, which Unity's Mono fork
/// does not ship; Mono's own log profiler is a native module this player does not carry; and
/// Unity's profiler is compiled out of a release build. So the collector has to be written
/// here, and what it can collect is decided entirely by the answers this probe returns.
///
/// The two answers that matter most:
///
/// - Does <c>GC.GetAllocatedBytesForCurrentThread</c> move? If it does, calls can be
///   attributed exact allocation counts, and the profiler can produce allocation flame
///   graphs rather than only time ones.
/// - Which <c>ProfilerRecorder</c> counters does a release player expose? Those are free -
///   no hooking, no overhead - and whatever they cover does not need instrumenting.
///
/// Run <c>prof.probe</c> in the console (F1), then <c>prof.copy</c>.
/// </summary>
[UsedImplicitly]
public class ModProfilerMod : IMod
{
    private readonly RewirerHandle CommandsHandle;
    private readonly LiveCounters Counters = new LiveCounters();
    private readonly CounterFeed Feed = new CounterFeed();
    private readonly ProfilerSession Session;
    private readonly ManagedCensus Managed = new ManagedCensus();
    private readonly DebugPanel Panel;
    private readonly TopBarButton Button;
    private readonly PanelInput Input;

    public ModProfilerMod(ILogger logger)
    {
        Session = new ProfilerSession(logger);

        // The commands are registered first and unconditionally. Everything below them is
        // scenery that a hot reload can legitimately refuse to rebuild, and the last time one
        // did, it threw out of this constructor and took prof.probe and prof.record with it.
        CommandsHandle = GameRewirers.AddRewirer(new ProbeCommands(logger, new RuntimeProbe(), Counters, Session, Managed));

        try
        {
            Panel = DebugPanel.Attach(Feed, Session, Managed);
            Button = new TopBarButton(logger, Panel);

            // Only worth hooking if there is a panel to be modal about.
            if (Panel != null)
            {
                Input = new PanelInput(logger, Panel);
            }
        }
        catch (System.Exception exception)
        {
            logger.Exception?.LogException(exception);
        }

        logger.Info?.Log(Panel != null
            ? "Mod Profiler ready - the tinted button beside Statistics, or prof.probe (F1)."
            : "Mod Profiler ready - commands only. The panel needs a restart: Unity will not add a "
              + "MonoBehaviour from a hot-reloaded assembly, so it cannot be rebuilt in place.");
    }

    public void Dispose()
    {
        GameRewirers.RemoveRewirer(CommandsHandle);

        Button?.Dispose();
        Input?.Dispose();
        Session?.Dispose();
        Counters.Dispose();
        Feed.Dispose();

        // The panel lives on a DontDestroyOnLoad object of its own, so nothing else will
        // take it away and a reload would otherwise leave one panel per generation.
        if (Panel != null)
        {
            UnityEngine.Object.Destroy(Panel.gameObject);
        }
    }
}
