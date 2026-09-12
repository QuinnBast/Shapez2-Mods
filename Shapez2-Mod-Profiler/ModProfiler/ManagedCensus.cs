using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

/// <summary>
/// The managed heap by type, and by the assembly that declares the type - which is what turns
/// it into "how much of this is my mod".
///
/// <see cref="ObjectCensus"/> is the other half. It walks
/// <c>Resources.FindObjectsOfTypeAll</c>, so it sees textures, meshes and components: engine
/// objects, nearly all of them the game's. A mod's cost is on this side - its classes, the
/// lists and dictionaries they hold, its strings and boxed structs.
///
/// **Why not the runtime's own heap walk.** Unity's <c>mono_unity_liveness_*</c> is exported
/// and cannot be called from a mod: it reports objects through a callback, a callback into a
/// mod is managed code, and re-entering managed code is exactly what a stopped GC world
/// forbids. It hangs the game. Unity gets away with it because their callback is C++. So the
/// graph is walked here instead, with reflection, and nothing is ever suspended.
///
/// **Why it roots at everything, not at the mod.** The first version rooted at the mod
/// assemblies' own static fields and found nothing but a few mscorlib arrays - which was
/// correct and useless, because that is not where a mod's objects live. Shapez Shifter holds
/// every <c>IMod</c> in an instance field; the game's simulation holds the simulation objects
/// a mod registered; Unity holds MonoBehaviours natively, the managed side reaching them only
/// through GC handles. Every one of those paths starts outside the mod. So the walk roots at
/// every loaded assembly's statics *and* at every live <c>UnityEngine.Object</c> - the second
/// being the only way to reach what Unity owns - and attributes what it finds by declaring
/// assembly rather than by where the path started.
///
/// **Why it is incremental.** That root set reaches most of the heap, which is millions of
/// objects, which is minutes of reflection. Doing it in one call is the freeze this class was
/// already rewritten once to avoid. <see cref="Step"/> does a few milliseconds of work per
/// frame, so the scan finishes over a second or two of wall clock, cancellable, with the game
/// still running underneath.
/// </summary>
public class ManagedCensus
{
    public class Entry
    {
        public string TypeName;
        public string Assembly;
        public int Count;
        public long Bytes;
        public bool IsMod;
    }

    /// <summary>
    /// A hard stop on the visited set rather than on time. Every object visited is held in the
    /// identity table until the scan ends, so this is also what the scan itself costs: three
    /// million objects is a 32 MiB table.
    /// </summary>
    private const int MaxObjects = 3000000;

    /// <summary>
    /// A wall-clock ceiling on the whole scan, counted across frames. The object cap is the
    /// real bound; this one catches the case where the walk has found a way to generate
    /// objects faster than it counts them, which has happened once already - see
    /// <see cref="Readable"/> on pointer fields.
    /// </summary>
    private const long MaxWallMilliseconds = 120000;

    private const int HeaderBytes = 16;
    private const int ArrayHeaderBytes = 32;
    private const int PointerBytes = 8;
    private const int MaxStructDepth = 8;

    /// <summary>Objects between stopwatch reads. Reading the clock per object is its own cost.</summary>
    private const int ClockInterval = 256;

    /// <summary>
    /// Assemblies whose statics are never read.
    ///
    /// Not a performance filter - a safety one. Reading a static through reflection is a call
    /// into the runtime, and the runtime's own corners are where it goes wrong: thread-static
    /// storage that was never allocated for this thread, pointer fields, ref-struct fields.
    /// Unity's native walker skips corlib outright and skips any field at offset -1 (its
    /// "shortcut check for special statics"), which is the same judgement reached the same
    /// way. Nothing is lost: the engine's own objects are rooted through
    /// FindObjectsOfTypeAll instead, and the framework's data is reachable from there.
    /// </summary>
    private static readonly string[] NeverReadStatics =
    {
        "mscorlib",
        "netstandard",
        "System",
        "System.",
        "Mono.",
        "I18N",
        "UnityEngine",
        "Unity.Burst",
        "Unity.Collections",
        "Unity.Jobs",
    };

    private readonly Dictionary<Type, bool> Unreadable = new Dictionary<Type, bool>();

    /// <summary>
    /// The type the previous scan died on, if it died. Written as it goes and deleted on a
    /// clean finish, so a scan that takes the process down leaves its own last words - the
    /// crash log names the line but not the field, and a native crash cannot be caught.
    /// </summary>
    private string Poisoned;

    private readonly Dictionary<Type, long> InstanceSizes = new Dictionary<Type, long>();
    private readonly Dictionary<Type, bool> Interesting = new Dictionary<Type, bool>();
    private readonly Dictionary<Type, Entry> ByType = new Dictionary<Type, Entry>();

    private IdentitySet Seen;
    private Stack<object> Pending;

    private List<Type> RootTypes;
    private int RootCursor;

    private readonly Stopwatch Clock = new Stopwatch();

    public List<Entry> Entries { get; private set; } = new List<Entry>();

    /// <summary>Per-assembly rollup of <see cref="Entries"/>: mods first, then largest first.</summary>
    public List<Entry> ByAssembly { get; private set; } = new List<Entry>();

    public int TotalObjects { get; private set; }
    public long TotalBytes { get; private set; }
    public double Milliseconds { get; private set; }

    public bool Running { get; private set; }
    public bool HasScanned { get; private set; }
    public bool Truncated { get; private set; }

    /// <summary>Objects queued and not yet visited - the closest thing to a progress bar.</summary>
    public int Queued => Pending?.Count ?? 0;

    public int Visited { get; private set; }

    /// <summary>What the scan is doing now, for the panel's status line.</summary>
    public string Status { get; private set; } = "Not scanned.";

    /// <summary>
    /// Starts a scan. Nothing is walked here beyond finding the roots; the work happens in
    /// <see cref="Step"/>.
    /// </summary>
    public void Begin()
    {
        Reset();

        Running = true;
        Clock.Reset();
        Clock.Start();
        Status = "Collecting roots...";

        Seen = new IdentitySet(1 << 16);
        Pending = new Stack<object>(1 << 16);

        // Unity's objects first: they are held natively, so no managed static reaches them,
        // and a mod's MonoBehaviours and the components carrying its state hang off them.
        try
        {
            UnityEngine.Object[] live = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();

            foreach (UnityEngine.Object item in live)
            {
                if (item != null && Seen.Add(item))
                {
                    Pending.Push(item);
                }
            }
        }
        catch (Exception)
        {
            // A partial root set still produces a useful census.
        }

        RootTypes = new List<Type>(4096);
        RootCursor = 0;
        Poisoned = ReadBreadcrumb();

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (SkipStatics(assembly))
            {
                continue;
            }

            Type[] types;

            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException partial)
            {
                types = partial.Types;
            }
            catch (Exception)
            {
                continue;
            }

            foreach (Type type in types)
            {
                // An open generic has no static storage of its own; the closed instantiations
                // do, and they are reachable from whatever holds them.
                if (type != null && !type.IsGenericTypeDefinition && !type.ContainsGenericParameters)
                {
                    RootTypes.Add(type);
                }
            }
        }
    }

    public void Cancel()
    {
        if (Running)
        {
            Finish("Cancelled");
        }
    }

    /// <summary>
    /// Does up to <paramref name="milliseconds"/> of walking. Called once a frame while a scan
    /// is running, which is what keeps the game responsive through it.
    /// </summary>
    public void Step(long milliseconds)
    {
        if (!Running)
        {
            return;
        }

        Stopwatch slice = Stopwatch.StartNew();

        while (RootCursor < RootTypes.Count)
        {
            if (slice.ElapsedMilliseconds >= milliseconds)
            {
                Status = "Reading statics (" + RootCursor.ToString("N0") + " of "
                         + RootTypes.Count.ToString("N0") + " types)";
                return;
            }

            // The breadcrumb is written per batch rather than per type: often enough to name
            // the neighbourhood a crash happened in, rarely enough that the file write does
            // not dominate the phase.
            WriteBreadcrumb(RootTypes[RootCursor]);

            for (int i = 0; i < 64 && RootCursor < RootTypes.Count; i++)
            {
                ReadStatics(RootTypes[RootCursor++]);
            }
        }

        int sinceClock = 0;

        while (Pending.Count > 0)
        {
            if (sinceClock++ >= ClockInterval)
            {
                sinceClock = 0;

                if (slice.ElapsedMilliseconds >= milliseconds)
                {
                    Status = "Walking (" + Visited.ToString("N0") + " visited, "
                             + Pending.Count.ToString("N0") + " queued)";
                    return;
                }
            }

            if (Visited >= MaxObjects)
            {
                Truncated = true;
                Finish("Stopped at " + MaxObjects.ToString("N0") + " objects");
                return;
            }

            if (Clock.ElapsedMilliseconds > MaxWallMilliseconds)
            {
                Truncated = true;
                Finish("Stopped after " + MaxWallMilliseconds / 1000 + " seconds");
                return;
            }

            Visit(Pending.Pop());
        }

        Finish("Complete");
    }

    /// <summary>Runs a whole scan on this thread - for the console, which has no frames.</summary>
    public void RunToCompletion(long budgetMilliseconds)
    {
        Begin();

        Stopwatch total = Stopwatch.StartNew();

        while (Running && total.ElapsedMilliseconds < budgetMilliseconds)
        {
            Step(50);
        }

        if (Running)
        {
            Truncated = true;
            Finish("Stopped at the " + budgetMilliseconds + " ms console budget");
        }
    }

    private void Reset()
    {
        ByType.Clear();
        Visited = 0;
        TotalObjects = 0;
        TotalBytes = 0;
        Truncated = false;
        Entries = new List<Entry>();
        ByAssembly = new List<Entry>();
    }

    /// <summary>
    /// Whether this assembly's statics are read at all - see <see cref="NeverReadStatics"/>.
    /// </summary>
    private static bool SkipStatics(Assembly assembly)
    {
        string name;

        try
        {
            name = assembly.GetName().Name;
        }
        catch (Exception)
        {
            return true;
        }

        foreach (string denied in NeverReadStatics)
        {
            if (denied.EndsWith(".", StringComparison.Ordinal)
                ? name.StartsWith(denied, StringComparison.Ordinal)
                : name == denied || name.StartsWith(denied + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BreadcrumbPath()
    {
        return Application.persistentDataPath + "/modprofiler-scan.txt";
    }

    private static void WriteBreadcrumb(Type type)
    {
        try
        {
            System.IO.File.WriteAllText(BreadcrumbPath(), type.FullName ?? type.Name);
        }
        catch (Exception)
        {
            // A breadcrumb that cannot be written costs a diagnosis, not a scan.
        }
    }

    private static string ReadBreadcrumb()
    {
        try
        {
            string path = BreadcrumbPath();

            if (!System.IO.File.Exists(path))
            {
                return null;
            }

            string name = System.IO.File.ReadAllText(path).Trim();
            System.IO.File.Delete(path);

            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ClearBreadcrumb()
    {
        try
        {
            string path = BreadcrumbPath();

            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Left behind, it costs one skipped type on the next scan.
        }
    }

    private void Finish(string status)
    {
        Running = false;
        ClearBreadcrumb();
        Clock.Stop();
        Milliseconds = Clock.Elapsed.TotalMilliseconds;

        Materialise();

        // Dropped as soon as the walk ends: the identity table holds a strong reference to
        // every object it saw, so keeping it would keep the heap it measured alive.
        Seen = null;
        Pending = null;
        RootTypes = null;

        HasScanned = true;
        Status = status + " - " + TotalObjects.ToString("N0") + " objects, about "
                 + Bytes(TotalBytes) + ", in " + Milliseconds.ToString("0") + " ms."
                 + (Poisoned == null ? "" : " Skipped " + Poisoned + ", which the last scan died in.");
    }

    private void Visit(object item)
    {
        Type type = item.GetType();

        if (!ByType.TryGetValue(type, out Entry entry))
        {
            entry = new Entry
            {
                TypeName = Readable(type),
                Assembly = type.Assembly.GetName().Name,
                IsMod = ModAssemblies.IsMod(type.Assembly),
            };

            ByType[type] = entry;
        }

        long size = SizeOf(item, type);

        entry.Count++;
        entry.Bytes += size;
        TotalBytes += size;
        Visited++;

        Expand(item, type);
    }

    /// <summary>
    /// Reads one type's static fields into the queue.
    ///
    /// Reading a static through reflection runs that type's initializer if it has not run yet.
    /// There is no "is this type initialised" in the managed API, so the side effect cannot be
    /// avoided - only stated. In a running session the types that hold anything are already
    /// initialised.
    /// </summary>
    private void ReadStatics(Type type)
    {
        // The type the last scan died inside. Skipped once, and the user is told; a scan that
        // kills the process twice for the same reason is not a tool.
        if (Poisoned != null && Poisoned == type.FullName)
        {
            return;
        }

        FieldInfo[] fields;

        try
        {
            fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                                    | BindingFlags.DeclaredOnly);
        }
        catch (Exception)
        {
            return;
        }

        foreach (FieldInfo field in fields)
        {
            if (!Readable(field, true))
            {
                continue;
            }

            object value;

            try
            {
                value = field.GetValue(null);
            }
            catch (Exception)
            {
                continue;
            }

            Offer(value, field.FieldType, 0);
        }
    }

    /// <summary>
    /// Whether this field can be read at all.
    ///
    /// A try/catch is not enough here, which is the whole point: <c>GetValue</c> on the wrong
    /// field does not throw, it takes the process down inside
    /// <c>RuntimeFieldInfo.GetValueInternal</c> - a crash window, not an exception. So the
    /// unreadable shapes are excluded before the call rather than after it.
    ///
    /// - **Pointer fields.** Reading one boxes it into a <c>System.Reflection.Pointer</c>,
    ///   which is an object this scan created and then counted as if it were on the heap:
    ///   212,478 of them, 6.5 MiB, in the first run that produced a table. Wrong twice over.
    /// - **Ref structs.** A <c>Span&lt;T&gt;</c> cannot be boxed, and asking anyway is
    ///   undefined at best.
    /// - **Thread statics.** The storage is per thread and may never have been allocated for
    ///   this one. This is the same check Unity's own walker makes when it skips a field at
    ///   offset -1.
    /// </summary>
    private bool Readable(FieldInfo field, bool isStatic)
    {
        if (field.IsLiteral)
        {
            return false;
        }

        Type type = field.FieldType;

        if (type == null || type.IsPointer || type.IsByRef || IsByRefLike(type))
        {
            return false;
        }

        if (!isStatic)
        {
            return true;
        }

        try
        {
            return !field.IsDefined(typeof(ThreadStaticAttribute), false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Detected by attribute name rather than <c>Type.IsByRefLike</c>, which this runtime's
    /// class library may not carry - and a MissingMethodException inside the walk would be a
    /// worse failure than the one being prevented.
    /// </summary>
    private bool IsByRefLike(Type type)
    {
        if (!type.IsValueType)
        {
            return false;
        }

        if (Unreadable.TryGetValue(type, out bool known))
        {
            return known;
        }

        bool refLike = false;

        try
        {
            foreach (CustomAttributeData data in type.CustomAttributes)
            {
                if (data.AttributeType.Name == "IsByRefLikeAttribute")
                {
                    refLike = true;
                    break;
                }
            }
        }
        catch (Exception)
        {
            refLike = true;
        }

        Unreadable[type] = refLike;

        return refLike;
    }

    private void Offer(object value, Type declared, int depth)
    {
        if (value == null)
        {
            return;
        }

        if (declared != null && declared.IsValueType)
        {
            // A struct is not an object on the heap, but the references inside it are.
            if (depth < MaxStructDepth && MayHoldReferences(declared))
            {
                ExpandStruct(value, declared, depth + 1);
            }

            return;
        }

        if (!Seen.Add(value))
        {
            return;
        }

        Pending.Push(value);
    }

    private void Expand(object item, Type type)
    {
        if (type == typeof(string) || type.IsPointer)
        {
            return;
        }

        if (type.IsArray)
        {
            ExpandArray(item, type);
            return;
        }

        for (Type level = type; level != null && level != typeof(object); level = level.BaseType)
        {
            FieldInfo[] fields;

            try
            {
                fields = level.GetFields(BindingFlags.Instance | BindingFlags.Public
                                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                return;
            }

            foreach (FieldInfo field in fields)
            {
                if (!Readable(field, false))
                {
                    continue;
                }

                object value;

                try
                {
                    value = field.GetValue(item);
                }
                catch (Exception)
                {
                    continue;
                }

                Offer(value, field.FieldType, 0);
            }
        }
    }

    private void ExpandArray(object item, Type type)
    {
        Type element = type.GetElementType();

        if (element == null || (element.IsValueType && !MayHoldReferences(element)))
        {
            return;
        }

        try
        {
            // foreach rather than GetValue(i): it handles rank greater than one, which
            // GetValue(int) throws on.
            foreach (object value in (Array)item)
            {
                Offer(value, element, 0);
            }
        }
        catch (Exception)
        {
            // A partial array is still worth what was read off it.
        }
    }

    private void ExpandStruct(object boxed, Type type, int depth)
    {
        FieldInfo[] fields;

        try
        {
            fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch (Exception)
        {
            return;
        }

        foreach (FieldInfo field in fields)
        {
            if (!Readable(field, false))
            {
                continue;
            }

            object value;

            try
            {
                value = field.GetValue(boxed);
            }
            catch (Exception)
            {
                continue;
            }

            Offer(value, field.FieldType, depth);
        }
    }

    /// <summary>
    /// Whether a value type can contain a reference, cached because it is asked per field per
    /// object. A struct of numbers is a dead end, and skipping it is most of the speed.
    /// </summary>
    private bool MayHoldReferences(Type type)
    {
        if (Interesting.TryGetValue(type, out bool known))
        {
            return known;
        }

        // A struct that reaches itself through a generic argument answers "yes" while it is
        // still being worked out, which is the safe direction.
        Interesting[type] = true;

        bool holds = false;

        try
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public
                                                       | BindingFlags.NonPublic))
            {
                Type fieldType = field.FieldType;

                if (!fieldType.IsValueType)
                {
                    holds = true;
                    break;
                }

                if (!fieldType.IsPrimitive && !fieldType.IsEnum && MayHoldReferences(fieldType))
                {
                    holds = true;
                    break;
                }
            }
        }
        catch (Exception)
        {
            holds = true;
        }

        Interesting[type] = holds;

        return holds;
    }

    private void Materialise()
    {
        List<Entry> entries = new List<Entry>(ByType.Values);
        entries.Sort(Largest);

        Dictionary<string, Entry> assemblies = new Dictionary<string, Entry>();

        int total = 0;

        foreach (Entry entry in entries)
        {
            total += entry.Count;

            if (!assemblies.TryGetValue(entry.Assembly, out Entry rollup))
            {
                rollup = new Entry { TypeName = entry.Assembly, Assembly = entry.Assembly, IsMod = entry.IsMod };
                assemblies[entry.Assembly] = rollup;
            }

            rollup.Count += entry.Count;
            rollup.Bytes += entry.Bytes;
        }

        List<Entry> rollups = new List<Entry>(assemblies.Values);

        // Mods first whatever their size. The table exists to answer "what is mine", and on a
        // loaded save the game outweighs every mod by two orders of magnitude - which would
        // bury the answer at the bottom.
        rollups.Sort((a, b) => a.IsMod != b.IsMod ? (a.IsMod ? -1 : 1) : Largest(a, b));

        Entries = entries;
        ByAssembly = rollups;
        TotalObjects = total;
    }

    /// <summary>
    /// An estimate, and labelled as one wherever it is shown.
    ///
    /// Mono's object header is two words, its arrays carry a length and a bounds pointer, and
    /// everything else is the sum of the declared fields at eight bytes per reference. Exact
    /// for the shapes that dominate a heap - arrays, strings, and classes of plain fields -
    /// and wrong at the margins, which is the price of not stopping the world to ask the
    /// runtime for the real number.
    /// </summary>
    private long SizeOf(object item, Type type)
    {
        if (type == typeof(string))
        {
            return Align(HeaderBytes + 4 + 2 * (long)((string)item).Length + 2);
        }

        if (type.IsArray)
        {
            try
            {
                Array array = (Array)item;

                return Align(ArrayHeaderBytes + array.LongLength * FieldSize(type.GetElementType()));
            }
            catch (Exception)
            {
                return ArrayHeaderBytes;
            }
        }

        return InstanceSize(type);
    }

    private long InstanceSize(Type type)
    {
        if (InstanceSizes.TryGetValue(type, out long known))
        {
            return known;
        }

        InstanceSizes[type] = HeaderBytes;

        long total = HeaderBytes;

        try
        {
            for (Type level = type; level != null && level != typeof(object); level = level.BaseType)
            {
                foreach (FieldInfo field in level.GetFields(BindingFlags.Instance | BindingFlags.Public
                                                            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    total += FieldSize(field.FieldType);
                }
            }
        }
        catch (Exception)
        {
            // Whatever was counted before the failure is closer than nothing.
        }

        total = Align(total);
        InstanceSizes[type] = total;

        return total;
    }

    private long FieldSize(Type type)
    {
        if (type == null || !type.IsValueType)
        {
            return PointerBytes;
        }

        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Boolean:
            case TypeCode.SByte:
            case TypeCode.Byte:
                return 1;
            case TypeCode.Char:
            case TypeCode.Int16:
            case TypeCode.UInt16:
                return 2;
            case TypeCode.Int32:
            case TypeCode.UInt32:
            case TypeCode.Single:
                return 4;
            case TypeCode.Int64:
            case TypeCode.UInt64:
            case TypeCode.Double:
            case TypeCode.DateTime:
                return 8;
            case TypeCode.Decimal:
                return 16;
        }

        return Math.Max(InstanceSize(type) - HeaderBytes, PointerBytes);
    }

    private static long Align(long value)
    {
        return (value + 7) & ~7L;
    }

    private static int Largest(Entry a, Entry b)
    {
        return b.Bytes != a.Bytes ? b.Bytes.CompareTo(a.Bytes) : b.Count.CompareTo(a.Count);
    }

    /// <summary>
    /// <c>List&lt;CargoPackage&gt;</c> rather than the assembly-qualified two-liner reflection
    /// gives for a generic - the point of the table is to be scanned down.
    /// </summary>
    private static string Readable(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        string name = type.Name;
        int tick = name.IndexOf('`');

        if (tick > 0)
        {
            name = name.Substring(0, tick);
        }

        Type[] arguments = type.GetGenericArguments();
        string[] parts = new string[arguments.Length];

        for (int i = 0; i < arguments.Length; i++)
        {
            parts[i] = arguments[i].Name;
        }

        string space = type.Namespace;

        return (string.IsNullOrEmpty(space) ? "" : space + ".") + name + "<" + string.Join(", ", parts) + ">";
    }

    /// <summary>
    /// Identity, not equality, and open-addressed rather than a <c>HashSet&lt;object&gt;</c>.
    ///
    /// Identity because a type that overrides <c>Equals</c> would otherwise fold distinct
    /// objects into one and lose half the heap - and calling a mod's <c>Equals</c> a million
    /// times during a memory scan is its own bad idea. Open-addressed because the set holds
    /// every object visited, and a HashSet's per-entry bucket, hash and next field cost three
    /// times what one bare reference does at this scale.
    /// </summary>
    private sealed class IdentitySet
    {
        private object[] Slots;
        private int Mask;
        private int Count;
        private int Limit;

        public IdentitySet(int capacity)
        {
            Allocate(capacity);
        }

        private void Allocate(int capacity)
        {
            Slots = new object[capacity];
            Mask = capacity - 1;
            Limit = capacity * 5 / 8;
            Count = 0;
        }

        public bool Add(object item)
        {
            int index = RuntimeHelpers.GetHashCode(item) & Mask;

            while (true)
            {
                object slot = Slots[index];

                if (slot == null)
                {
                    Slots[index] = item;

                    if (++Count > Limit)
                    {
                        Grow();
                    }

                    return true;
                }

                if (ReferenceEquals(slot, item))
                {
                    return false;
                }

                index = (index + 1) & Mask;
            }
        }

        private void Grow()
        {
            object[] old = Slots;

            Allocate(old.Length * 2);

            foreach (object item in old)
            {
                if (item != null)
                {
                    Add(item);
                }
            }
        }
    }

    /// <summary>The top types, for the console command - the panel reads the lists directly.</summary>
    public IEnumerable<string> Summarise(int limit)
    {
        List<string> report = new List<string> { Status };

        if (!HasScanned)
        {
            return report;
        }

        report.Add("Sizes are estimated from field layout; counts are exact.");
        report.Add("");
        report.Add("BY ASSEMBLY (mods first, marked *)");

        foreach (Entry entry in ByAssembly)
        {
            report.Add("  " + (entry.IsMod ? "* " : "  ") + Pad(entry.TypeName, 44)
                       + Pad(entry.Count.ToString("N0"), 12) + Bytes(entry.Bytes));
        }

        report.Add("");
        report.Add("BY TYPE");

        for (int i = 0; i < Math.Min(limit, Entries.Count); i++)
        {
            Entry entry = Entries[i];
            report.Add("  " + Pad(entry.TypeName, 60) + Pad(entry.Count.ToString("N0"), 12) + Bytes(entry.Bytes));
        }

        return report;
    }

    private static string Pad(string value, int width)
    {
        if (value == null)
        {
            value = "";
        }

        return value.Length >= width ? value.Substring(0, width - 1) + " " : value.PadRight(width);
    }

    public static string Bytes(long value)
    {
        if (value < 1024)
        {
            return value + " B";
        }

        if (value < 1024L * 1024L)
        {
            return (value / 1024.0).ToString("0.0") + " KiB";
        }

        return value < 1024L * 1024L * 1024L
            ? (value / (1024.0 * 1024.0)).ToString("0.0") + " MiB"
            : (value / (1024.0 * 1024.0 * 1024.0)).ToString("0.00") + " GiB";
    }
}
