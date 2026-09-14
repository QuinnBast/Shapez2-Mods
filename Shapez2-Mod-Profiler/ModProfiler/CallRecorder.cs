using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace QuinnBast.Shapez2.ModProfiler;

/// <summary>
/// The hot path: what every instrumented method calls on the way in and on the way out.
///
/// This is where a flame graph differs from everything else in this mod. The counters and the
/// object census are read from the engine, and neither can say which mod spent what. These two
/// methods are compiled into your own code, so every frame they produce is a method you wrote,
/// named with your own type and method name.
///
/// **Every thread is recorded, and that is not a luxury.** The first version recorded only the
/// thread that started the capture, on the reasoning that the game and its mods run on the main
/// thread. They do not: <c>GameSessionOrchestrator</c> calls
/// <c>Simulator.StartAsynchronousUpdate</c>, which returns a <c>Task</c>, so belts, machines and
/// every simulation a mod registers run on pool threads. A 200 second recording of a belt mod
/// came back with six methods and 0.1 ms, because all of its work happened on threads the
/// recorder was throwing away.
///
/// Each thread therefore keeps its own stack and its own tree, with no lock on the hot path -
/// a lock per call would cost more than the measurement is worth - and the trees are merged
/// when the recording stops.
///
/// **Nothing here may allocate after warm-up.** A tree's nodes are created the first time a
/// call path is seen and reused forever after, and each stack is a pre-sized array. A profiler
/// that allocates while measuring changes the thing it is measuring - and on this build, where
/// the only allocation signal is the GC sawtooth, it would corrupt the one memory number we
/// have.
/// </summary>
public static class CallRecorder
{
    /// <summary>
    /// One method, reached by one path. The same method called from two places is two nodes,
    /// which is exactly what makes a flame graph a tree rather than a list.
    /// </summary>
    public class Node
    {
        public int MethodId;
        public Node Parent;
        public readonly Dictionary<int, Node> Children = new Dictionary<int, Node>();
        public long Ticks;
        public long Calls;
        public int Depth;
    }

    /// <summary>What one thread contributed, for the report that says where the time went.</summary>
    public class ThreadSummary
    {
        public string Name;
        public long Ticks;
        public long Calls;
    }

    private struct Frame
    {
        public Node Node;
        public long Started;
    }

    /// <summary>
    /// Deep enough for any sane call tree, and a hard stop for runaway recursion. Past this
    /// the calls are still timed by their ancestors, they just stop being broken out.
    ///
    /// 256 was not deep enough: a 37 second recording of Train Cargo Tools overflowed 158,772
    /// times, which is a path walk that recurses per segment, not a bug. At 16 bytes a frame
    /// this costs 16 KiB per recorded thread, once.
    /// </summary>
    private const int MaxDepth = 1024;

    /// <summary>
    /// One thread's stack and tree. Touched only by its own thread while recording, which is
    /// what makes the hot path lock-free.
    /// </summary>
    private sealed class ThreadState
    {
        public readonly Frame[] Stack = new Frame[MaxDepth];
        public int Depth;
        public int Skipped;
        public int Generation;
        public string Name;
        public Node Root = new Node { MethodId = -1 };
        public long Overflowed;
        public long Unwound;
        public long Unmatched;
    }

    [ThreadStatic]
    private static ThreadState Local;

    private static readonly List<ThreadState> States = new List<ThreadState>();
    private static readonly object Gate = new object();

    /// <summary>
    /// Bumped by every <see cref="Start"/> so a thread that recorded a previous capture knows
    /// its state is stale. Without it, a second recording starts with the first one's stacks.
    /// </summary>
    private static int Generation;

    private static int MainThreadId;

    // Written by the starting thread and read by every recorded one, so the write has to be
    // published rather than sitting in a register or a core's store buffer.
    private static volatile bool RecordingFlag;

    public static bool Recording => RecordingFlag;

    /// <summary>The merged tree, built when recording stops. Null until then.</summary>
    public static Node Tree { get; private set; }

    public static long StartedAt { get; private set; }
    public static long StoppedAt { get; private set; }

    public static long Overflowed { get; private set; }
    public static long Unwound { get; private set; }
    public static long Unmatched { get; private set; }

    /// <summary>Per-thread totals, largest first - built with the merged tree.</summary>
    public static List<ThreadSummary> Threads { get; private set; } = new List<ThreadSummary>();

    /// <summary>The depth cap, so a report can name the number it is talking about.</summary>
    public static int DepthLimit => MaxDepth;

    public static void Start()
    {
        lock (Gate)
        {
            States.Clear();
            Generation++;
        }

        Tree = null;
        Threads = new List<ThreadSummary>();
        Overflowed = 0;
        Unwound = 0;
        Unmatched = 0;
        MainThreadId = Thread.CurrentThread.ManagedThreadId;
        StartedAt = Stopwatch.GetTimestamp();
        StoppedAt = 0;
        RecordingFlag = true;
    }

    public static void Stop()
    {
        RecordingFlag = false;
        StoppedAt = Stopwatch.GetTimestamp();

        Merge();
    }

    /// <summary>
    /// Called at the top of every instrumented method, on whatever thread runs it. Must stay
    /// cheap enough that timing it does not dominate what is being timed.
    /// </summary>
    public static void Enter(int methodId)
    {
        if (!RecordingFlag)
        {
            return;
        }

        ThreadState state = Local;

        if (state == null || state.Generation != Generation)
        {
            state = Register();

            if (state == null)
            {
                return;
            }
        }

        if (state.Depth >= MaxDepth)
        {
            state.Skipped++;
            state.Overflowed++;
            return;
        }

        Node parent = state.Depth == 0 ? state.Root : state.Stack[state.Depth - 1].Node;

        if (!parent.Children.TryGetValue(methodId, out Node node))
        {
            node = new Node { MethodId = methodId, Parent = parent, Depth = parent.Depth + 1 };
            parent.Children[methodId] = node;
        }

        state.Stack[state.Depth].Node = node;
        state.Stack[state.Depth].Started = Stopwatch.GetTimestamp();
        state.Depth++;
    }

    /// <summary>
    /// Called before every return. Tolerates frames left behind by a method that threw:
    /// unwinding to the matching frame is what keeps one exception from corrupting the whole
    /// tree, and is why this needs no try/finally woven into the IL.
    /// </summary>
    public static void Exit(int methodId)
    {
        if (!RecordingFlag)
        {
            return;
        }

        ThreadState state = Local;

        if (state == null || state.Generation != Generation)
        {
            return;
        }

        // Spend the entries dropped at the depth cap before touching the stack: this exit
        // belongs to the most recent one, and nothing above the cap was ever pushed. Without
        // this the exit would go looking down the stack, find the *ancestor* that caused the
        // recursion, and close a frame it never opened.
        if (state.Skipped > 0)
        {
            state.Skipped--;
            return;
        }

        if (state.Depth == 0)
        {
            state.Unmatched++;
            return;
        }

        long now = Stopwatch.GetTimestamp();

        // Find the frame this exit belongs to. Normally it is the top one.
        int target = -1;

        for (int i = state.Depth - 1; i >= 0; i--)
        {
            if (state.Stack[i].Node.MethodId == methodId)
            {
                target = i;
                break;
            }
        }

        if (target < 0)
        {
            // An exit with no matching entry: recording started midway through this call.
            state.Unmatched++;
            return;
        }

        if (target != state.Depth - 1)
        {
            state.Unwound += state.Depth - 1 - target;
        }

        // Close everything above the match as well, attributing each its elapsed time.
        for (int i = state.Depth - 1; i >= target; i--)
        {
            state.Stack[i].Node.Ticks += now - state.Stack[i].Started;
            state.Stack[i].Node.Calls++;
        }

        state.Depth = target;

        // An empty stack is the one moment the books are known to balance, so it is also the
        // moment a skip count left over from an exception can safely be forgotten.
        if (state.Depth == 0)
        {
            state.Skipped = 0;
        }
    }

    /// <summary>
    /// First call on a thread: the only place the recorder takes a lock or allocates, and it
    /// happens once per thread per recording.
    /// </summary>
    private static ThreadState Register()
    {
        ThreadState state = new ThreadState
        {
            Generation = Generation,
            Name = Describe(Thread.CurrentThread),
        };

        lock (Gate)
        {
            // Checked inside the lock: a recording can stop between the flag test and here.
            if (state.Generation != Generation)
            {
                return null;
            }

            States.Add(state);
        }

        Local = state;

        return state;
    }

    private static string Describe(Thread thread)
    {
        if (thread.ManagedThreadId == MainThreadId)
        {
            return "main";
        }

        return string.IsNullOrEmpty(thread.Name)
            ? "thread " + thread.ManagedThreadId
            : thread.Name;
    }

    /// <summary>
    /// Folds every thread's tree into one.
    ///
    /// A method that runs on four simulation threads is one entry in the merged tree with
    /// four threads' time in it, which is the number worth reading - the per-thread split is
    /// kept separately in <see cref="Threads"/> for the report.
    /// </summary>
    private static void Merge()
    {
        Node merged = new Node { MethodId = -1 };
        List<ThreadSummary> summaries = new List<ThreadSummary>();

        lock (Gate)
        {
            foreach (ThreadState state in States)
            {
                Overflowed += state.Overflowed;
                Unwound += state.Unwound;
                Unmatched += state.Unmatched;

                ThreadSummary summary = new ThreadSummary { Name = state.Name };

                try
                {
                    Fold(state.Root, merged, summary);
                }
                catch (Exception)
                {
                    // A thread still inside an instrumented call while this runs can be
                    // mutating the dictionary being read. The hooks come out immediately
                    // after, so the window is small; a partial merge is better than losing
                    // every thread's data to one straggler.
                }

                if (summary.Ticks > 0 || summary.Calls > 0)
                {
                    summaries.Add(summary);
                }
            }
        }

        summaries.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));

        Threads = summaries;
        Tree = merged;
    }

    private static void Fold(Node source, Node into, ThreadSummary summary)
    {
        foreach (KeyValuePair<int, Node> pair in source.Children)
        {
            Node child = pair.Value;

            if (!into.Children.TryGetValue(child.MethodId, out Node target))
            {
                target = new Node
                {
                    MethodId = child.MethodId,
                    Parent = into,
                    Depth = into.Depth + 1,
                };

                into.Children[child.MethodId] = target;
            }

            target.Ticks += child.Ticks;
            target.Calls += child.Calls;

            if (into.MethodId < 0)
            {
                // Only the top level, or nested calls would be counted several times over.
                summary.Ticks += child.Ticks;
            }

            summary.Calls += child.Calls;

            Fold(child, target, summary);
        }
    }
}
