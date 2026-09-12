using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using ILogger = Core.Logging.ILogger;

/// <summary>
/// Starting and stopping a recording, and finding the assembly to record.
///
/// One mod at a time and only when asked. Instrumentation is the one part of this profiler
/// that changes the program it measures - every woven method costs two timestamp reads and a
/// dictionary lookup per call - so it is never on by default and never on for everything.
/// </summary>
public class ProfilerSession : IDisposable
{
    /// <summary>
    /// Enough for any mod in this workspace, and a stop before a mistyped name weaves
    /// something enormous.
    /// </summary>
    public const int DefaultBudget = 4000;

    private readonly ILogger Logger;
    private readonly Instrumenter Weaver;

    public ProfilerSession(ILogger logger)
    {
        Logger = logger;
        Weaver = new Instrumenter(logger);
    }

    public bool Recording => CallRecorder.Recording;

    public string Target => Weaver.Assembly;

    public string NameOf(int methodId)
    {
        return Weaver.NameOf(methodId);
    }

    public IEnumerable<string> Record(string name, int budget)
    {
        List<string> report = new List<string>();

        if (Recording)
        {
            report.Add("Already recording " + Target + " - prof.stop first.");
            return report;
        }

        if (!TryFind(name, report, out Assembly assembly))
        {
            return report;
        }

        report.Add("Instrumenting " + assembly.GetName().Name + " - the game will hitch while it weaves.");
        report.AddRange(Weaver.Instrument(assembly, budget));

        if (Weaver.Instrumented == 0)
        {
            report.Add("Nothing was instrumented, so there is nothing to record.");
            Weaver.Reset();
            return report;
        }

        CallRecorder.Start();

        report.Add("Recording. Play for a few seconds, then prof.stop, then open the panel.");
        return report;
    }

    public IEnumerable<string> StopRecording()
    {
        List<string> report = new List<string>();

        if (!Recording)
        {
            report.Add("Not recording.");
            return report;
        }

        CallRecorder.Stop();

        // The weave comes out as soon as the recording does - leaving it in would keep paying
        // the per-call cost for a measurement nobody is taking - but the names stay, because
        // the tree is read through them after this returns.
        int woven = Weaver.Instrumented;
        Weaver.RemoveHooks();

        double seconds = (CallRecorder.StoppedAt - CallRecorder.StartedAt) / (double)Stopwatch.Frequency;

        report.Add("Stopped after " + seconds.ToString("0.0") + "s; removed " + woven + " hooks.");

        // The per-thread split is the line that says whether the recorder was looking in the
        // right place: a mod whose simulation runs on pool threads shows nearly all of its
        // time under a thread that is not main.
        if (CallRecorder.Threads.Count == 0)
        {
            report.Add("  No thread recorded a single call. Either nothing woven ran, or it ran");
            report.Add("  somewhere the recorder was not.");
        }
        else
        {
            foreach (CallRecorder.ThreadSummary thread in CallRecorder.Threads)
            {
                double ms = thread.Ticks * 1000.0 / Stopwatch.Frequency;
                report.Add("  " + thread.Name + ": " + ms.ToString("0.0") + " ms over "
                           + thread.Calls.ToString("N0") + " calls");
            }
        }

        if (CallRecorder.Unwound > 0)
        {
            report.Add("  " + CallRecorder.Unwound + " frame(s) were unwound by exceptions - their time is");
            report.Add("  attributed to the caller rather than to them.");
        }

        if (CallRecorder.Overflowed > 0)
        {
            report.Add("  " + CallRecorder.Overflowed + " call(s) were deeper than the " + CallRecorder.DepthLimit
                       + "-frame limit and are counted in their ancestors only.");
        }

        if (CallRecorder.Unmatched > 0)
        {
            report.Add("  " + CallRecorder.Unmatched + " exit(s) matched no entry - those calls were already");
            report.Add("  running when recording started, so their time is missing from the tree.");
        }

        report.Add("The flame graph is at the bottom of the profiler panel.");
        return report;
    }

    /// <summary>
    /// Finds a loaded assembly by a loose name match, refusing anything ambiguous so a typo
    /// cannot weave the wrong thing - the same rule <c>mrl.reload</c> uses.
    /// </summary>
    private bool TryFind(string name, List<string> report, out Assembly found)
    {
        found = null;

        if (string.IsNullOrEmpty(name))
        {
            report.Add("Which mod? Try prof.record traincargotools");
            return false;
        }

        List<Assembly> matches = new List<Assembly>();

        foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
        {
            string simple;

            try
            {
                simple = candidate.GetName().Name;
            }
            catch (Exception)
            {
                continue;
            }

            if (simple == null || candidate == typeof(ProfilerSession).Assembly)
            {
                continue;
            }

            if (simple.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count == 0)
        {
            report.Add("No loaded assembly matches '" + name + "'.");
            return false;
        }

        if (matches.Count > 1)
        {
            report.Add("'" + name + "' matches more than one assembly:");

            foreach (Assembly candidate in matches)
            {
                report.Add("  " + candidate.GetName().Name);
            }

            return false;
        }

        found = matches[0];
        return true;
    }

    public void Dispose()
    {
        if (Recording)
        {
            CallRecorder.Stop();
        }

        Weaver.Dispose();
    }
}
