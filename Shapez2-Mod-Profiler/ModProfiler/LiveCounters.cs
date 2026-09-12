using System;
using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

/// <summary>
/// Holds recorders open across frames, which is the only way to find out whether a counter
/// carries a number.
///
/// A <see cref="ProfilerRecorder"/> reports <c>Valid</c> the instant it is created but has no
/// sample until a frame boundary has gone past. Creating one and reading it in the same call
/// therefore reports zero for every counter in the game, valid or not - which is exactly what
/// the first version of this probe did, and why its "231 counters, all zero" result said
/// nothing at all.
///
/// So these are started by a command, left running, and read by a later one. A counter that
/// is still zero after seconds of play is genuinely not being fed; one that has moved is
/// usable. That distinction decides how much of a profiler needs to be built by hand and how
/// much the player is already measuring.
/// </summary>
public class LiveCounters : IDisposable
{
    private readonly List<Entry> Entries = new List<Entry>();

    private class Entry
    {
        public string Category;
        public string Name;
        public ProfilerRecorder Recorder;
    }

    public bool Watching => Entries.Count > 0;

    /// <summary>
    /// Opens a recorder on every counter the player exposes.
    ///
    /// All of them rather than a chosen few: which counters a release player feeds is the
    /// open question, and a guessed shortlist is how the last probe missed it. The cost is a
    /// few hundred recorders sampling one value each, which is a development tool's problem
    /// to have.
    /// </summary>
    public IEnumerable<string> Start()
    {
        List<string> report = new List<string>();

        if (Watching)
        {
            return Stop();
        }

        try
        {
            List<ProfilerRecorderHandle> handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);

            int failed = 0;

            foreach (ProfilerRecorderHandle handle in handles)
            {
                try
                {
                    ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                    ProfilerRecorder recorder = ProfilerRecorder.StartNew(description.Category, description.Name);

                    if (!recorder.Valid)
                    {
                        recorder.Dispose();
                        failed++;
                        continue;
                    }

                    Entries.Add(new Entry
                    {
                        Category = description.Category.Name ?? "(none)",
                        Name = description.Name,
                        Recorder = recorder,
                    });
                }
                catch
                {
                    failed++;
                }
            }

            report.Add("Watching " + Entries.Count + " counters"
                       + (failed > 0 ? ", " + failed + " would not start" : "") + ".");
            report.Add("Play for a few seconds, then prof.live.");
        }
        catch (Exception exception)
        {
            report.Add("Could not enumerate counters (" + exception.GetType().Name + ": " + exception.Message + ")");
        }

        return report;
    }

    public IEnumerable<string> Stop()
    {
        int count = Entries.Count;

        foreach (Entry entry in Entries)
        {
            try
            {
                entry.Recorder.Dispose();
            }
            catch
            {
                // A recorder that will not close is not worth a report line; the handles go
                // with the process either way.
            }
        }

        Entries.Clear();

        return new[] { "Stopped watching " + count + " counters." };
    }

    /// <summary>
    /// Reports the counters that have actually moved, which is the answer being looked for.
    ///
    /// The ones still at zero are listed by name only, at the end: a counter that is exposed
    /// but never fed is worth knowing about precisely because it looks available.
    /// </summary>
    public IEnumerable<string> Read()
    {
        if (!Watching)
        {
            return new[] { "Not watching anything - run prof.watch first, then play for a few seconds." };
        }

        List<string> live = new List<string>();
        List<string> silent = new List<string>();

        foreach (Entry entry in Entries)
        {
            try
            {
                long value = entry.Recorder.LastValue;

                if (value != 0)
                {
                    live.Add("  " + entry.Category + " / " + entry.Name + " = " + value);
                }
                else
                {
                    silent.Add(entry.Category + " / " + entry.Name);
                }
            }
            catch (Exception exception)
            {
                silent.Add(entry.Category + " / " + entry.Name + " (threw " + exception.GetType().Name + ")");
            }
        }

        List<string> report = new List<string>
        {
            live.Count + " of " + Entries.Count + " counters are carrying a value.",
            "",
            "LIVE",
        };

        live.Sort(StringComparer.Ordinal);
        report.AddRange(live);

        report.Add("");
        report.Add("STILL ZERO (" + silent.Count + ") - exposed but not fed by this player");

        silent.Sort(StringComparer.Ordinal);

        foreach (string name in silent)
        {
            report.Add("  " + name);
        }

        return report;
    }

    public void Dispose()
    {
        Stop();
    }
}
