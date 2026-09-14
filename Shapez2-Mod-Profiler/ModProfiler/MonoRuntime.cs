using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace QuinnBast.Shapez2.ModProfiler;

/// <summary>
/// The Mono runtime's own C entry points, reached by <c>GetProcAddress</c> on the copy of
/// <c>mono-2.0-bdwgc.dll</c> already mapped into the process.
///
/// **Why this exists.** Everything else in this mod is limited by what Unity chose to expose
/// to managed code, and for memory that is very little: no managed heap walk, no allocation
/// counter, sizes only for <c>UnityEngine.Object</c>. None of that is a limit of the
/// *runtime* - it is a limit of the managed surface. The runtime hosting us is an ordinary
/// Mono embedding build and it exports its whole embedding API - 1238 symbols, including the
/// heap walker Unity's own memory profiler is built on.
///
/// Not all of it is *callable* from a mod, and the heap walker is the example: it reports
/// objects through a callback, and a callback into a mod is managed code, which a stopped GC
/// world forbids re-entering. It hangs the game. The symbols are still listed by
/// <see cref="Report"/>, because knowing they exist and knowing they are unusable are both
/// worth keeping. What is left is what can be read without a callback and without stopping
/// anything: the collector's own heap numbers.
///
/// **Why GetProcAddress and not DllImport.** A DllImport cannot be asked whether it would
/// work; it binds on first call or throws <c>EntryPointNotFoundException</c> at the call site,
/// which is a poor way to discover that a build renamed something. Resolving by hand lets
/// <see cref="Report"/> list what is present without calling any of it - and calling is the
/// part that can take the process down. <c>GetModuleHandle</c> also guarantees we bind to the
/// runtime that is *running* rather than risk loading a second copy from disk.
///
/// **These calls are not safe the way managed calls are safe.** A wrong signature corrupts the
/// stack; it does not throw. Every signature here was read off Unity's own fork
/// (github.com/Unity-Technologies/mono - <c>mono/metadata/unity-liveness.c</c> and
/// <c>unity-utils.c</c>, byte-identical on <c>unity-main</c> and the 6000.3 branches; this game
/// is Unity 6000.3.19f1) rather than guessed.
/// </summary>
public static class MonoRuntime
{
    private const string Module = "mono-2.0-bdwgc.dll";

    [DllImport("kernel32", EntryPoint = "GetModuleHandleA", ExactSpelling = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandleA(string name);

    [DllImport("kernel32", EntryPoint = "GetProcAddress", ExactSpelling = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddressA(IntPtr module, string name);

    // Mono's embedding API is cdecl. On x64 Windows that is the same ABI as stdcall, so this
    // is belt and braces - but it would not be on x86, and being explicit costs nothing.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate long CountFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VoidFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr PointerFn(IntPtr argument);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint SizeFn(IntPtr obj);

    /// <summary>Every symbol this mod binds, so the report can be built without calling any.</summary>
    private static readonly string[] Symbols =
    {
        "mono_gc_get_heap_size",
        "mono_gc_get_used_size",
        "mono_unity_gc_disable",
        "mono_unity_gc_enable",
        "mono_unity_stop_gc_world",
        "mono_unity_start_gc_world",
        "mono_unity_liveness_allocate_struct",
        "mono_unity_liveness_calculation_from_statics",
        "mono_unity_liveness_finalize",
        "mono_unity_liveness_free_struct",
        "mono_object_get_class",
        "mono_object_get_size",
        "mono_class_get_name",
        "mono_class_get_namespace",
        "mono_class_get_image",
        "mono_image_get_name",
    };

    private static readonly Dictionary<string, IntPtr> Addresses = new Dictionary<string, IntPtr>();

    private static bool Bound;

    /// <summary>Zero when the runtime module is not in this process at all.</summary>
    public static IntPtr Handle { get; private set; }

    public static CountFn HeapSize { get; private set; }
    public static CountFn UsedSize { get; private set; }

    public static bool CanReadHeapSize => HeapSize != null && UsedSize != null;

    /// <summary>
    /// Resolves every symbol once. Binding is pure <c>GetProcAddress</c> - nothing in the
    /// runtime is called - so it is safe at mod load, and safe on a build where none of these
    /// exist.
    /// </summary>
    public static void Bind()
    {
        if (Bound)
        {
            return;
        }

        Bound = true;

        try
        {
            Handle = GetModuleHandleA(Module);
        }
        catch (Exception)
        {
            Handle = IntPtr.Zero;
        }

        if (Handle == IntPtr.Zero)
        {
            return;
        }

        foreach (string symbol in Symbols)
        {
            Addresses[symbol] = GetProcAddressA(Handle, symbol);
        }

        HeapSize = Bind<CountFn>("mono_gc_get_heap_size");
        UsedSize = Bind<CountFn>("mono_gc_get_used_size");
    }

    private static T Bind<T>(string symbol) where T : class
    {
        if (!Addresses.TryGetValue(symbol, out IntPtr address) || address == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads a <c>const char *</c> the runtime owns. Never freed - it is not ours.</summary>
    public static string Text(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringAnsi(pointer);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// What this build exports, without calling any of it. That distinction is the whole point
    /// of the report: a symbol resolving is the only evidence available short of a call, and
    /// the call is the part that can be fatal.
    /// </summary>
    public static IEnumerable<string> Report()
    {
        Bind();

        List<string> report = new List<string> { "MONO RUNTIME (native)" };

        if (Handle == IntPtr.Zero)
        {
            report.Add("  " + Module + " is not loaded in this process - either this is not the Mono");
            report.Add("  player, or the runtime was renamed. Nothing native is available.");
            return report;
        }

        report.Add("  module " + Module + " at 0x" + Handle.ToString("x"));

        foreach (string symbol in Symbols)
        {
            Addresses.TryGetValue(symbol, out IntPtr address);
            report.Add("  " + (address == IntPtr.Zero ? "missing  " : "resolved ") + symbol);
        }

        report.Add("");
        report.Add("  The liveness symbols resolve, and calling them from a mod still does not");
        report.Add("  work: the walk reports objects through a callback, a callback into a mod is");
        report.Add("  managed code, and re-entering managed code is what a stopped GC world");
        report.Add("  forbids. It hangs. prof.managed walks the same roots with reflection.");

        if (CanReadHeapSize)
        {
            report.Add("  Boehm heap: " + HeapSize() + " bytes reserved, " + UsedSize() + " bytes in use.");
        }

        return report;
    }
}
