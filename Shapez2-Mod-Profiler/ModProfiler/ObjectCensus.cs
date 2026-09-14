using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace QuinnBast.Shapez2.ModProfiler;

/// <summary>
/// Counts what is on the heap, by type, largest first.
///
/// **What this can and cannot see.** It walks <c>Resources.FindObjectsOfTypeAll</c>, which
/// enumerates every live <c>UnityEngine.Object</c> - textures, meshes, materials, shaders,
/// audio clips, GameObjects, components - and asks
/// <c>Profiler.GetRuntimeMemorySizeLong</c> for each one's real size. In a Unity game that is
/// nearly always where the memory actually is, and it is a complete census of that half.
///
/// It cannot see pure managed objects: your mod's classes, the lists and dictionaries they
/// hold, strings, boxed structs. Nothing in Mono exposes a managed heap walk to managed code,
/// and this build's GC is Boehm, which has no such API at all. The only route to those is
/// <c>MemoryProfiler.TakeSnapshot</c>, which writes a capture file in a format that needs
/// Unity's own Memory Profiler window to read. That is a separate piece of work and an open
/// question - the symbol is present in this build, but so was
/// <c>GC.GetAllocatedBytesForCurrentThread</c>, which turned out to be frozen at zero.
///
/// **Why it is a button and not a graph.** FindObjectsOfTypeAll walks every object the engine
/// knows about, including ones not in any scene. On a large save that is a visible hitch, so
/// it runs when asked and not before.
/// </summary>
public class ObjectCensus
{
    public class Entry
    {
        public string TypeName;
        public int Count;
        public long Bytes;
    }

    /// <summary>Types, largest total first. Empty until the first scan.</summary>
    public List<Entry> Entries { get; private set; } = new List<Entry>();

    public int TotalObjects { get; private set; }
    public long TotalBytes { get; private set; }
    public double Milliseconds { get; private set; }
    public bool HasScanned { get; private set; }

    /// <summary>
    /// Set when sizes come back as zero across the board, which would mean
    /// <c>GetRuntimeMemorySizeLong</c> is another API that exists and does nothing here. The
    /// counts stay meaningful either way, so this reports rather than hides it.
    /// </summary>
    public bool SizesUnavailable { get; private set; }

    public void Scan()
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        Dictionary<string, Entry> byType = new Dictionary<string, Entry>();
        int total = 0;
        long totalBytes = 0;

        try
        {
            UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();

            foreach (UnityEngine.Object item in objects)
            {
                if (item == null)
                {
                    continue;
                }

                // The fully qualified name, because two assemblies can each define a Mesh or
                // a Settings and the whole point is to tell them apart.
                string name = item.GetType().FullName ?? "(unknown)";

                if (!byType.TryGetValue(name, out Entry entry))
                {
                    entry = new Entry { TypeName = name };
                    byType[name] = entry;
                }

                entry.Count++;
                total++;

                long size = SizeOf(item);
                entry.Bytes += size;
                totalBytes += size;
            }
        }
        catch (Exception)
        {
            // A partial census is still worth showing; the totals say how far it got.
        }

        List<Entry> entries = new List<Entry>(byType.Values);

        // By bytes, then by count, so that types the size probe could not measure still sort
        // sensibly among themselves rather than landing in arbitrary order.
        entries.Sort((a, b) => b.Bytes != a.Bytes ? b.Bytes.CompareTo(a.Bytes) : b.Count.CompareTo(a.Count));

        Entries = entries;
        TotalObjects = total;
        TotalBytes = totalBytes;
        SizesUnavailable = total > 0 && totalBytes == 0;
        HasScanned = true;

        clock.Stop();
        Milliseconds = clock.Elapsed.TotalMilliseconds;
    }

    private static long SizeOf(UnityEngine.Object item)
    {
        try
        {
            return Profiler.GetRuntimeMemorySizeLong(item);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
