using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine.Profiling;

/// <summary>
/// Asks the running game which profiling APIs it actually implements.
///
/// Every API this reports on is present in the assemblies shipped with the game - that much
/// can be read off the metadata without launching anything. What metadata cannot say is
/// whether Unity's Mono fork *implements* a method or throws, and whether a counter that
/// exists returns a real number or a constant zero. Those are the facts a profiler has to be
/// designed around, and there is no way to learn them except to call and see.
///
/// So every probe is wrapped: an unimplemented method reports itself as unavailable rather
/// than taking the rest of the report down. A probe that throws is a result, not a failure.
///
/// What this build is known to be, before any of it runs:
///
/// - **Mono, not IL2CPP.** No GameAssembly.dll; MonoBleedingEdge/EmbedRuntime holds
///   mono-2.0-bdwgc.dll. Runtime detours and reflection over method bodies therefore work,
///   which is the only reason a mod-side profiler is possible at all.
/// - **A release player.** boot.config carries no player-connection or debug markers, so
///   Unity's own profiler cannot attach over the network and Profiler.BeginSample is
///   compiled out. ProfilerRecorder is the documented exception that still works.
/// - **Boehm GC** (bdwgc), which is non-generational. Generation-by-generation collection
///   counts are unlikely to mean much here even though the API exposes them.
/// </summary>
public class RuntimeProbe
{
    /// <summary>
    /// Counters worth asking for by name.
    ///
    /// Which of Unity's built-in counters a release player exposes is not documented in a
    /// form worth trusting, so this asks for a spread of the ones a profiler would want and
    /// reports which came back valid. <see cref="Counters"/> enumerates the full set
    /// instead; this list is for the ones whose absence would change the design.
    /// </summary>
    private static readonly (string Category, string Name)[] Wanted =
    {
        ("Memory", "GC Reserved Memory"),
        ("Memory", "GC Used Memory"),
        ("Memory", "GC Allocated In Frame"),
        ("Memory", "System Used Memory"),
        ("Memory", "Total Reserved Memory"),
        ("Memory", "Total Used Memory"),
        ("Internal", "Main Thread"),
        ("Render", "Draw Calls Count"),
        ("Render", "SetPass Calls Count"),
        ("Scripts", "GC.Alloc"),
    };

    public IEnumerable<string> Run()
    {
        List<string> report = new List<string> { "Mod Profiler - runtime probe" };

        report.Add("");
        report.Add("RUNTIME");
        report.Add(Probe("mono version", MonoVersion));
        report.Add(Probe("Environment.Version", () => Environment.Version.ToString()));
        report.Add(Probe("processor count", () => Environment.ProcessorCount.ToString()));

        report.Add("");
        report.Add("GARBAGE COLLECTOR");
        report.Add(Probe("GC.MaxGeneration", () => GC.MaxGeneration.ToString()));
        report.Add(Probe("GC.GetTotalMemory(false)", () => Bytes(GC.GetTotalMemory(false))));
        report.Add(Probe("GC.CollectionCount(all gens)", CollectionCounts));

        // The single most consequential line in this report. If this counter moves, a
        // profiler can attribute allocations to individual calls exactly, by reading it on
        // entry and exit and subtracting what the children allocated - which turns a flame
        // graph of time into a flame graph of garbage, and GC spikes are the usual reason a
        // mod stutters. If it does not move, allocation attribution is off the table and the
        // profiler can only report process-wide heap totals.
        report.Add(Probe("GC.GetAllocatedBytesForCurrentThread", AllocationCounter));

        report.Add("");
        report.Add("UNITY PROFILER CLASS");
        report.Add(Probe("Profiler.supported", () => Profiler.supported.ToString()));
        report.Add(Probe("Profiler.enabled", () => Profiler.enabled.ToString()));
        report.Add(Probe("GetMonoUsedSizeLong", () => Bytes(Profiler.GetMonoUsedSizeLong())));
        report.Add(Probe("GetMonoHeapSizeLong", () => Bytes(Profiler.GetMonoHeapSizeLong())));
        report.Add(Probe("GetTotalAllocatedMemoryLong", () => Bytes(Profiler.GetTotalAllocatedMemoryLong())));
        report.Add(Probe("GetTotalReservedMemoryLong", () => Bytes(Profiler.GetTotalReservedMemoryLong())));

        report.Add("");
        report.Add("PROFILER RECORDERS");
        report.Add(Probe("available counters", () => AvailableCount().ToString()));
        report.AddRange(WantedCounters());

        report.Add("");
        report.Add("prof.counters lists every available counter by name.");
        report.Add("prof.watch, play a few seconds, then prof.live - which counters carry a value.");
        report.Add("prof.copy puts the last report on the clipboard.");

        return report;
    }

    /// <summary>
    /// Every counter the player exposes, one per line, grouped by category.
    ///
    /// Separate from <see cref="Run"/> because there are usually far too many to read in the
    /// in-game console - this is meant for the clipboard.
    /// </summary>
    public IEnumerable<string> Counters()
    {
        List<string> report = new List<string>();

        try
        {
            List<ProfilerRecorderHandle> handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);

            SortedDictionary<string, List<string>> byCategory = new SortedDictionary<string, List<string>>();

            foreach (ProfilerRecorderHandle handle in handles)
            {
                try
                {
                    ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                    string category = description.Category.Name ?? "(no category)";

                    if (!byCategory.TryGetValue(category, out List<string> names))
                    {
                        names = new List<string>();
                        byCategory[category] = names;
                    }

                    names.Add("  " + description.Name + "  [" + description.DataType + ", " + description.UnitType + "]");
                }
                catch (Exception exception)
                {
                    report.Add("  (a counter description threw: " + exception.GetType().Name + ")");
                }
            }

            report.Add(handles.Count + " counters in " + byCategory.Count + " categories");

            foreach (KeyValuePair<string, List<string>> pair in byCategory)
            {
                report.Add("");
                report.Add(pair.Key + " (" + pair.Value.Count + ")");
                pair.Value.Sort(StringComparer.Ordinal);
                report.AddRange(pair.Value);
            }
        }
        catch (Exception exception)
        {
            report.Add("counter enumeration unavailable (" + exception.GetType().Name + ": " + exception.Message + ")");
        }

        return report;
    }

    /// <summary>
    /// Reads each counter this profiler would want by name.
    ///
    /// A recorder reports <c>Valid</c> immediately but has no sample until a frame has gone
    /// past, so a zero here means "ask again next frame", not "always zero". The line that
    /// matters is whether it is valid at all.
    /// </summary>
    private static IEnumerable<string> WantedCounters()
    {
        List<string> lines = new List<string>();

        foreach ((string category, string name) in Wanted)
        {
            lines.Add(Probe(category + " / " + name, () =>
            {
                using (ProfilerRecorder recorder = ProfilerRecorder.StartNew(new ProfilerCategory(category), name))
                {
                    // Deliberately not reporting LastValue. A recorder has no sample until a
                    // frame boundary has passed, so reading one here returns zero for every
                    // counter in the game and says nothing about whether it is fed. That is
                    // what prof.watch and prof.live are for.
                    return recorder.Valid ? "exposed" : "not exposed by this player";
                }
            }));
        }

        return lines;
    }

    private static int AvailableCount()
    {
        List<ProfilerRecorderHandle> handles = new List<ProfilerRecorderHandle>();
        ProfilerRecorderHandle.GetAvailable(handles);

        return handles.Count;
    }

    /// <summary>
    /// Allocates, then asks whether the counter noticed.
    ///
    /// The allocation has to survive until after the second read or the JIT is entitled to
    /// remove it, which would make a working counter look broken.
    /// </summary>
    private static string AllocationCounter()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        object[] sink = new object[4096];

        for (int i = 0; i < sink.Length; i++)
        {
            sink[i] = new object();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(sink);

        long delta = after - before;

        // Reported as absolutes as well as a delta: a counter frozen at zero and one frozen
        // at some large number are both unusable, but they are different bugs, and the
        // delta alone cannot tell them apart.
        string readings = "before=" + before + ", after=" + after;

        if (delta <= 0)
        {
            return "implemented but did not move over 4096 allocations (" + readings
                   + ") - per-call allocation attribution is NOT possible";
        }

        return "moved " + Bytes(delta) + " over 4096 allocations (" + readings
               + ") - per-call allocation attribution IS possible";
    }

    private static string CollectionCounts()
    {
        List<string> counts = new List<string>();

        for (int generation = 0; generation <= GC.MaxGeneration; generation++)
        {
            counts.Add("gen" + generation + "=" + GC.CollectionCount(generation));
        }

        return string.Join(", ", counts.ToArray());
    }

    /// <summary>
    /// Mono's version, read the only way a managed caller can: the runtime's own private
    /// display-name method. Absent on any other runtime, which is itself the answer.
    /// </summary>
    private static string MonoVersion()
    {
        Type runtime = Type.GetType("Mono.Runtime");

        if (runtime == null)
        {
            return "not running on Mono";
        }

        MethodInfo displayName = runtime.GetMethod(
            "GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static);

        return displayName == null
            ? "Mono, version not reportable"
            : (string)displayName.Invoke(null, null);
    }

    /// <summary>
    /// Runs one probe and turns whatever happens into a line.
    ///
    /// A throw is the expected outcome for anything Unity's Mono declares but does not
    /// implement, so the exception type is the result and is reported as such.
    /// </summary>
    private static string Probe(string name, Func<string> read)
    {
        try
        {
            return "  " + name + ": " + read();
        }
        catch (Exception exception)
        {
            Exception cause = exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException
                : exception;

            return "  " + name + ": unavailable (" + cause.GetType().Name + ": " + cause.Message + ")";
        }
    }

    private static string Bytes(long value)
    {
        if (value < 1024)
        {
            return value + " B";
        }

        if (value < 1024 * 1024)
        {
            return (value / 1024.0).ToString("0.0") + " KiB";
        }

        return (value / (1024.0 * 1024.0)).ToString("0.0") + " MiB";
    }
}
