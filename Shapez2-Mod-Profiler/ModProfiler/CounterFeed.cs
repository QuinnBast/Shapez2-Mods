using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// Samples the counters this player actually feeds, once per frame.
///
/// Of the 231 counters a release build exposes, 33 carry a value; the other 198 are almost
/// all <c>Scripts /</c> markers, which need <c>ENABLE_PROFILER</c> and so are dead here. The
/// live ones cost nothing to read - no detours, no instrumentation - and between them they
/// cover frame timing, memory and rendering completely. That is the whole of this panel's
/// data, and none of it required a profiler to be written.
///
/// What they cannot do is attribute any of it. They say the main thread took 7ms; nothing in
/// them says which mod spent it. That gap is what instrumentation is for, and it is a
/// separate piece of work.
/// </summary>
public class CounterFeed : IDisposable
{
    /// <summary>
    /// A counter, its recorder, and a little history for the graph.
    /// </summary>
    public class Gauge
    {
        public string Label;
        public string Category;
        public string Name;
        public bool IsTime;
        public bool IsBytes;
        public ProfilerRecorder Recorder;
        public long Value;

        /// <summary>
        /// Min, mean and max since the stats were last reset.
        ///
        /// A single live number answers "what is it now" and nothing else, which is the wrong
        /// question for a frame time: a stutter is a max that the instant reading has already
        /// forgotten by the time you look at it. Kept as a running min/max and a sum rather
        /// than a history, because the mean over the whole session is the stable number and
        /// the history that matters is drawn as a graph anyway.
        /// </summary>
        public long Min;

        public long Max;
        public double Sum;
        public long Samples;

        public double Mean => Samples == 0 ? 0 : Sum / Samples;

        public bool Live => Recorder.Valid;

        public void Observe(long value)
        {
            if (Samples == 0 || value < Min)
            {
                Min = value;
            }

            if (Samples == 0 || value > Max)
            {
                Max = value;
            }

            Sum += value;
            Samples++;
        }

        public void ResetStatistics()
        {
            Min = 0;
            Max = 0;
            Sum = 0;
            Samples = 0;
        }
    }

    /// <summary>
    /// Every counter verified live on this build, grouped the way the panel shows them.
    ///
    /// Named explicitly rather than enumerated: a panel wants a chosen, ordered, labelled
    /// set, and the full enumeration is what <c>prof.counters</c> is for.
    /// </summary>
    private static readonly (string Section, string Label, string Category, string Name, bool Time, bool Bytes)[] Wanted =
    {
        ("Frame", "Main thread", "Internal", "Main Thread", true, false),
        ("Frame", "Player loop", "PlayerLoop", "PlayerLoop", true, false),
        ("Frame", "CPU main", "Render", "CPU Main Thread Frame Time", true, false),
        ("Frame", "CPU render", "Render", "CPU Render Thread Frame Time", true, false),
        ("Frame", "CPU total", "Render", "CPU Total Frame Time", true, false),
        ("Frame", "GPU", "Render", "GPU Frame Time", true, false),

        // Subtracting this from the main thread is the difference between "slow" and
        // "finished early and waited", which no other counter here distinguishes.
        ("Frame", "Waiting for vsync", "VSync", "WaitForTargetFPS", true, false),

        ("Memory", "GC heap used", "Memory", "GC Used Memory", false, true),
        ("Memory", "GC heap reserved", "Memory", "GC Reserved Memory", false, true),
        ("Memory", "Total used", "Memory", "Total Used Memory", false, true),
        ("Memory", "Total reserved", "Memory", "Total Reserved Memory", false, true),
        ("Memory", "App resident", "Memory", "App Resident Memory", false, true),
        ("Memory", "Video memory", "Render", "Video Memory Bytes", false, true),

        ("Render", "Draw calls", "Render", "Draw Calls Count", false, false),
        ("Render", "Batches", "Render", "Batches Count", false, false),
        ("Render", "SetPass calls", "Render", "SetPass Calls Count", false, false),
        ("Render", "Triangles", "Render", "Triangles Count", false, false),
        ("Render", "Vertices", "Render", "Vertices Count", false, false),
        ("Render", "Render textures", "Render", "Render Textures Count", false, false),
        ("Render", "Buffers", "Render", "Used Buffers Count", false, false),
    };

    private readonly Dictionary<string, List<Gauge>> Sections = new Dictionary<string, List<Gauge>>();
    private readonly List<string> Order = new List<string>();

    /// <summary>How many frames the strip chart remembers.</summary>
    public const int HistoryLength = 180;

    /// <summary>Frame times, newest last, for the strip chart.</summary>
    public readonly float[] FrameHistory = new float[HistoryLength];

    private int FrameCursor;

    private long LastGcUsed;
    private int LastCollections;
    private float RateWindow;
    private long RateAccumulator;

    /// <summary>
    /// Bytes per second climbing onto the GC heap, measured from the sawtooth.
    ///
    /// This is as close to an allocation rate as this build allows.
    /// <c>GC.GetAllocatedBytesForCurrentThread</c> is frozen at zero here and neither
    /// <c>GC Allocated In Frame</c> nor <c>Scripts / GC.Alloc</c> is exposed, so the only
    /// remaining evidence is the heap climbing between collections. It is process-wide and
    /// undercounts whatever a collection reclaims mid-window, but it moves when a mod starts
    /// allocating, which is what it is for.
    /// </summary>
    public double AllocationRate { get; private set; }

    /// <summary>Collections per second, the other half of GC pressure.</summary>
    public double CollectionRate { get; private set; }

    public IReadOnlyList<string> SectionNames => Order;

    public CounterFeed()
    {
        foreach ((string section, string label, string category, string name, bool time, bool bytes) in Wanted)
        {
            Gauge gauge = new Gauge
            {
                Label = label,
                Category = category,
                Name = name,
                IsTime = time,
                IsBytes = bytes,
            };

            try
            {
                gauge.Recorder = ProfilerRecorder.StartNew(new ProfilerCategory(category), name);
            }
            catch
            {
                // A counter that will not open is reported as absent by the panel rather
                // than crashing the feed; the set is verified but the build may change.
            }

            if (!Sections.TryGetValue(section, out List<Gauge> gauges))
            {
                gauges = new List<Gauge>();
                Sections[section] = gauges;
                Order.Add(section);
            }

            gauges.Add(gauge);
        }

        LastCollections = GC.CollectionCount(0);
    }

    public IReadOnlyList<Gauge> Section(string name)
    {
        return Sections.TryGetValue(name, out List<Gauge> gauges) ? gauges : new List<Gauge>();
    }

    /// <summary>
    /// Reads every counter once. Called from the panel's update, not from a hook - there is
    /// nothing to intercept, which is the point of this whole tier.
    /// </summary>
    public void Sample(float deltaTime)
    {
        long gcUsed = 0;

        foreach (List<Gauge> gauges in Sections.Values)
        {
            foreach (Gauge gauge in gauges)
            {
                try
                {
                    if (gauge.Recorder.Valid)
                    {
                        gauge.Value = gauge.Recorder.LastValue;
                        gauge.Observe(gauge.Value);

                        if (gauge.Name == "GC Used Memory")
                        {
                            gcUsed = gauge.Value;
                        }
                    }
                }
                catch
                {
                    gauge.Value = 0;
                }
            }
        }

        float ms = deltaTime * 1000f;

        FrameHistory[FrameCursor] = ms;
        FrameCursor = (FrameCursor + 1) % FrameHistory.Length;

        FrameLast = ms;
        FrameMin = FrameSamples == 0 ? ms : Mathf.Min(FrameMin, ms);
        FrameMax = FrameSamples == 0 ? ms : Mathf.Max(FrameMax, ms);
        FrameSum += ms;
        FrameSamples++;
        FrameMean = (float)(FrameSum / FrameSamples);

        UpdateGcRates(deltaTime, gcUsed);
    }

    /// <summary>Newest-last view of the frame history, for drawing left to right.</summary>
    public float FrameAt(int index)
    {
        return FrameHistory[(FrameCursor + index) % FrameHistory.Length];
    }

    /// <summary>Frame time now, and the shape of the window behind it.</summary>
    public float FrameLast { get; private set; }

    public float FrameMin { get; private set; }

    public float FrameMax { get; private set; }

    public float FrameMean { get; private set; }

    /// <summary>
    /// Clears the running statistics on every gauge. The graph keeps rolling - it is a window
    /// by construction - but min and max are session-long by construction, so there has to be
    /// a way to say "from here".
    /// </summary>
    public void ResetStatistics()
    {
        foreach (List<Gauge> gauges in Sections.Values)
        {
            foreach (Gauge gauge in gauges)
            {
                gauge.ResetStatistics();
            }
        }

        FrameMin = 0;
        FrameMax = 0;
        FrameMean = 0;
        FrameSum = 0;
        FrameSamples = 0;
    }

    private double FrameSum;
    private long FrameSamples;

    private void UpdateGcRates(float deltaTime, long gcUsed)
    {
        // Only the climbs count. A drop is a collection, and how much it freed says nothing
        // about how fast the heap was being filled.
        if (gcUsed > LastGcUsed && LastGcUsed > 0)
        {
            RateAccumulator += gcUsed - LastGcUsed;
        }

        LastGcUsed = gcUsed;
        RateWindow += deltaTime;

        if (RateWindow < 1f)
        {
            return;
        }

        int collections = GC.CollectionCount(0);

        AllocationRate = RateAccumulator / RateWindow;
        CollectionRate = (collections - LastCollections) / RateWindow;

        LastCollections = collections;
        RateAccumulator = 0;
        RateWindow = 0f;
    }

    public void Dispose()
    {
        foreach (List<Gauge> gauges in Sections.Values)
        {
            foreach (Gauge gauge in gauges)
            {
                try
                {
                    gauge.Recorder.Dispose();
                }
                catch
                {
                    // Handles go with the process either way.
                }
            }
        }

        Sections.Clear();
        Order.Clear();
    }
}
