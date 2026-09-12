using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

/// <summary>
/// Weaves <see cref="CallRecorder"/> into every method of one mod assembly.
///
/// **Why IL rewriting and not a detour.** A `Hook` wraps a method with a delegate of matching
/// signature, which is fine for one known method and impossible for a few thousand unknown
/// ones. An `ILHook` edits the body instead - push an id, call Enter at the top, push an id,
/// call Exit before every `ret` - and that is signature-agnostic, so it works on every method
/// in an assembly without knowing anything about any of them.
///
/// MonoMod and Mono.Cecil are both live in the process already: they ship as their own
/// workshop mod that Shapez Shifter depends on, so nothing extra has to be loaded for this.
///
/// **What it cannot reach**, and these gaps are reported rather than hidden because a flame
/// graph that quietly omits things is worse than one that says what it missed:
///
/// - **Methods on generic types, and generic methods.** `Hook.CheckSupported` refuses them,
///   and it tests the declaring type, so `Foo&lt;ShapeId&gt;.Bar()` is as unhookable as
///   `Foo&lt;T&gt;.Bar()`. Train Cargo Tools' `CargoPathPlacement&lt;,&gt;` is exactly this.
/// - **Anything with no body** - abstract, extern, interface declarations.
/// - **Inlined calls.** Mono's JIT inlines small methods, and a body rewritten after a caller
///   has already inlined it does not affect that caller's copy. Tiny hot helpers will
///   therefore under-report.
/// </summary>
public class Instrumenter : IDisposable
{
    private readonly ILogger Logger;
    private readonly List<ILHook> Hooks = new List<ILHook>();
    private readonly List<string> Names = new List<string>();

    /// <summary>Methods skipped because they are generic, and so unhookable.</summary>
    public int SkippedGeneric { get; private set; }

    /// <summary>Methods skipped because they have no body to rewrite.</summary>
    public int SkippedBodyless { get; private set; }

    /// <summary>Methods the weave itself refused, with the reason logged.</summary>
    public int Failed { get; private set; }

    public int Instrumented => Hooks.Count;

    public string Assembly { get; private set; }

    public Instrumenter(ILogger logger)
    {
        Logger = logger;
    }

    public string NameOf(int methodId)
    {
        return methodId >= 0 && methodId < Names.Count ? Names[methodId] : "(root)";
    }

    /// <summary>
    /// Rewrites every eligible method in the assembly.
    ///
    /// <paramref name="budget"/> caps how many methods are woven. Instrumentation costs two
    /// timestamp reads and a dictionary lookup per call, which is nothing next to a method
    /// doing real work and ruinous next to one called per belt item per frame. The cap is a
    /// blunt guard against weaving a whole simulation by accident.
    /// </summary>
    public IEnumerable<string> Instrument(Assembly assembly, int budget)
    {
        List<string> report = new List<string>();

        Reset();
        Assembly = assembly.GetName().Name;

        MethodInfo enter = typeof(CallRecorder).GetMethod(nameof(CallRecorder.Enter));
        MethodInfo exit = typeof(CallRecorder).GetMethod(nameof(CallRecorder.Exit));

        if (enter == null || exit == null)
        {
            report.Add("Could not find the recorder's entry points - nothing was instrumented.");
            return report;
        }

        Type[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            // A mod that references something unavailable still yields the types that did
            // load, and those are worth instrumenting.
            types = Array.FindAll(partial.Types, t => t != null);
            report.Add("  some types would not load; instrumenting the " + types.Length + " that did");
        }

        foreach (Type type in types)
        {
            if (type.IsGenericType || type.IsGenericTypeDefinition)
            {
                SkippedGeneric++;
                continue;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.DeclaredOnly;

            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if (Hooks.Count >= budget)
                {
                    report.Add("  stopped at the " + budget + " method budget - raise it if you need more");
                    report.Add(Summary());
                    return report;
                }

                Weave(method, enter, exit);
            }
        }

        report.Add(Summary());
        return report;
    }

    private void Weave(MethodInfo method, MethodInfo enter, MethodInfo exit)
    {
        if (method.IsGenericMethod || method.IsGenericMethodDefinition)
        {
            SkippedGeneric++;
            return;
        }

        if (method.IsAbstract || (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0)
        {
            SkippedBodyless++;
            return;
        }

        int id = Names.Count;
        string name = (method.DeclaringType?.FullName ?? "?") + "." + method.Name;

        try
        {
            ILHook hook = new ILHook(method, il =>
            {
                ILCursor cursor = new ILCursor(il);

                cursor.Goto(0);
                cursor.Emit(OpCodes.Ldc_I4, id);
                cursor.Emit(OpCodes.Call, enter);

                // Every exit point, not just the last: a method with early returns would
                // otherwise leave frames open on all but one path.
                //
                // MoveType.Before is also what makes a branch to this ret run the exit call -
                // incoming labels are retargeted onto the emitted instructions, so a jump to
                // the return still closes the frame.
                while (cursor.TryGotoNext(MoveType.Before, i => i.MatchRet()))
                {
                    cursor.Emit(OpCodes.Ldc_I4, id);
                    cursor.Emit(OpCodes.Call, exit);

                    // Emit leaves the cursor after what it wrote, which is exactly on the ret
                    // again - so one step clears it and the next search starts past it.
                    // Stepping further walks off the end of the body, and since almost every
                    // method's last instruction *is* a ret, that throws
                    // ArgumentOutOfRangeException for almost every method.
                    cursor.Index += 1;
                }
            });

            Hooks.Add(hook);
            Names.Add(name);
        }
        catch (Exception exception)
        {
            Failed++;

            // A weave that refuses everything would otherwise write one line per method -
            // 162 of them, the first time this ran - and bury the summary that explains it.
            if (Failed <= 5)
            {
                Logger.Info?.Log("Mod Profiler: could not instrument " + name
                                 + " (" + exception.GetType().Name + ": " + exception.Message + ")");
            }
            else if (Failed == 6)
            {
                Logger.Info?.Log("Mod Profiler: further refusals are counted in the summary, not listed.");
            }
        }
    }

    private string Summary()
    {
        string line = "Instrumented " + Instrumented + " methods in " + Assembly
                      + " (" + SkippedGeneric + " generic, " + SkippedBodyless + " bodyless, "
                      + Failed + " refused).";

        if (Failed > Instrumented && Failed > 0)
        {
            line += " More were refused than woven - that is a fault in the weave, not in the mod.";
        }

        return line;
    }

    /// <summary>
    /// Takes the weave out and leaves the names in.
    ///
    /// They are two different lifetimes and conflating them cost a recording: the hooks have
    /// to come out the moment recording stops, or every call keeps paying for a measurement
    /// nobody is taking - but the names are what the recorded tree is *read* through, and the
    /// tree is read after the stop. Clearing both made every frame in the flame graph render
    /// as "(root)", because <see cref="NameOf"/> falls back to that for an id past the end of
    /// the list.
    /// </summary>
    public void RemoveHooks()
    {
        foreach (ILHook hook in Hooks)
        {
            try
            {
                hook.Dispose();
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
            }
        }

        Hooks.Clear();
    }

    /// <summary>Forgets the names as well - only safe once the tree they label is finished with.</summary>
    public void Reset()
    {
        RemoveHooks();

        Names.Clear();
        SkippedGeneric = 0;
        SkippedBodyless = 0;
        Failed = 0;
        Assembly = null;
    }

    public void Dispose()
    {
        Reset();
    }
}
