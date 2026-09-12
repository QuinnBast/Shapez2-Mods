using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Writes what is on screen to files beside <c>Player.log</c>, and puts the path on the
/// clipboard.
///
/// **Why files and not the clipboard alone.** A heap census is thousands of rows and a call
/// tree is thousands more; a clipboard full of that is unusable, and an in-game console cannot
/// be scrolled or selected. What a developer wants is the path, so the file can be opened in
/// something that can search it - so that is what the clipboard gets.
///
/// One file, and one that reads as it stands: counters with their min, mean and max, the
/// garbage collector, both censuses, the per-thread split and the whole call tree, indented.
/// Diffable between builds, greppable, and nothing to import anywhere first.
/// </summary>
public static class ReportWriter
{
    /// <summary>
    /// Writes the report and returns its full path, or null with <paramref name="error"/> set.
    /// </summary>
    public static string Write(CounterFeed feed, ObjectCensus unity, ManagedCensus managed,
        ProfilerSession session, out string error)
    {
        error = null;

        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(Application.persistentDataPath, "modprofiler-" + stamp + ".txt");

            File.WriteAllText(path, BuildReport(feed, unity, managed, session));

            return path;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + exception.Message;
            return null;
        }
    }

    private static string BuildReport(CounterFeed feed, ObjectCensus unity, ManagedCensus managed,
        ProfilerSession session)
    {
        StringBuilder text = new StringBuilder(64 * 1024);

        text.AppendLine("Mod Profiler report");
        text.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        text.AppendLine("Unity " + Application.unityVersion + ", " + Application.productName + " "
                        + Application.version);
        text.AppendLine();

        text.AppendLine("FRAME TIME (ms)");
        text.AppendLine("  last " + feed.FrameLast.ToString("0.00") + "   min " + feed.FrameMin.ToString("0.00")
                        + "   mean " + feed.FrameMean.ToString("0.00") + "   max " + feed.FrameMax.ToString("0.00"));
        text.AppendLine();

        foreach (string section in feed.SectionNames)
        {
            text.AppendLine(section.ToUpperInvariant());

            foreach (CounterFeed.Gauge gauge in feed.Section(section))
            {
                if (!gauge.Live)
                {
                    text.AppendLine("  " + Pad(gauge.Label, 26) + "not exposed by this build");
                    continue;
                }

                text.AppendLine("  " + Pad(gauge.Label, 26) + Pad(Format(gauge, gauge.Value), 14)
                                + "min " + Pad(Format(gauge, gauge.Min), 12)
                                + "mean " + Pad(Format(gauge, (long)gauge.Mean), 12)
                                + "max " + Format(gauge, gauge.Max));
            }

            text.AppendLine();
        }

        text.AppendLine("GARBAGE COLLECTOR");
        text.AppendLine("  " + Pad("Managed heap", 26) + ManagedCensus.Bytes(GC.GetTotalMemory(false)));

        MonoRuntime.Bind();

        if (MonoRuntime.CanReadHeapSize)
        {
            text.AppendLine("  " + Pad("Boehm reserved", 26) + ManagedCensus.Bytes(MonoRuntime.HeapSize()));
            text.AppendLine("  " + Pad("Boehm in use", 26) + ManagedCensus.Bytes(MonoRuntime.UsedSize()));
        }

        text.AppendLine("  " + Pad("Collections", 26) + GC.CollectionCount(0));
        text.AppendLine("  " + Pad("Collections / sec", 26) + feed.CollectionRate.ToString("0.00"));
        text.AppendLine("  " + Pad("Heap growth / sec", 26) + ManagedCensus.Bytes((long)feed.AllocationRate));
        text.AppendLine();

        if (managed != null && managed.HasScanned)
        {
            text.AppendLine("MANAGED OBJECTS");
            text.AppendLine("  " + managed.Status);
            text.AppendLine("  Sizes are estimated from field layout; counts are exact.");
            text.AppendLine();
            text.AppendLine("  BY ASSEMBLY (mods marked *)");

            foreach (ManagedCensus.Entry entry in managed.ByAssembly)
            {
                text.AppendLine("    " + (entry.IsMod ? "* " : "  ") + Pad(entry.TypeName, 44)
                                + Pad(entry.Count.ToString("N0"), 12) + ManagedCensus.Bytes(entry.Bytes));
            }

            text.AppendLine();
            text.AppendLine("  BY TYPE");

            foreach (ManagedCensus.Entry entry in managed.Entries)
            {
                text.AppendLine("    " + Pad(entry.TypeName, 70) + Pad(entry.Count.ToString("N0"), 12)
                                + ManagedCensus.Bytes(entry.Bytes));
            }

            text.AppendLine();
        }

        if (unity != null && unity.HasScanned)
        {
            text.AppendLine("UNITY OBJECTS");
            text.AppendLine("  " + unity.TotalObjects.ToString("N0") + " objects, "
                            + ManagedCensus.Bytes(unity.TotalBytes));
            text.AppendLine();

            foreach (ObjectCensus.Entry entry in unity.Entries)
            {
                text.AppendLine("    " + Pad(entry.TypeName, 70) + Pad(entry.Count.ToString("N0"), 12)
                                + ManagedCensus.Bytes(entry.Bytes));
            }

            text.AppendLine();
        }

        CallRecorder.Node root = CallRecorder.Tree;

        if (session != null && root != null && root.Children.Count > 0)
        {
            text.AppendLine("RECORDING: " + (session.Target ?? "(assembly no longer named)"));
            text.AppendLine("  " + Seconds() + " seconds recorded");

            foreach (CallRecorder.ThreadSummary thread in CallRecorder.Threads)
            {
                text.AppendLine("  " + Pad(thread.Name, 26) + Pad(Milliseconds(thread.Ticks), 14)
                                + thread.Calls.ToString("N0") + " calls");
            }

            text.AppendLine();
            text.AppendLine("  CALL TREE");
            WriteTree(text, root, session, 0);

            text.AppendLine();
        }

        return text.ToString();
    }

    private static void WriteTree(StringBuilder text, CallRecorder.Node node, ProfilerSession session, int depth)
    {
        if (depth > 64)
        {
            return;
        }

        List<CallRecorder.Node> children = new List<CallRecorder.Node>(node.Children.Values);
        children.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));

        foreach (CallRecorder.Node child in children)
        {
            text.Append("    ").Append(' ', depth * 2);
            text.Append(Pad(session.NameOf(child.MethodId), Math.Max(60 - depth * 2, 20)));
            text.Append(Pad(Milliseconds(child.Ticks), 14));
            text.Append(child.Calls.ToString("N0")).AppendLine(" calls");

            WriteTree(text, child, session, depth + 1);
        }
    }

    private static string Seconds()
    {
        long ticks = CallRecorder.StoppedAt - CallRecorder.StartedAt;

        return (ticks / (double)System.Diagnostics.Stopwatch.Frequency).ToString("0.0");
    }

    private static string Milliseconds(long ticks)
    {
        return (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0") + " ms";
    }

    private static string Format(CounterFeed.Gauge gauge, long value)
    {
        if (gauge.IsTime)
        {
            return (value / 1_000_000.0).ToString("0.00") + " ms";
        }

        return gauge.IsBytes ? ManagedCensus.Bytes(value) : value.ToString("N0");
    }

    private static string Pad(object value, int width)
    {
        string text = value?.ToString() ?? "";

        return text.Length >= width ? text.Substring(0, width - 1) + " " : text.PadRight(width);
    }
}
