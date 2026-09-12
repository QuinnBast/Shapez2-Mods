using System;
using System.Collections.Generic;
using ShapezShifter.Hijack;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

/// <summary>
/// The probe's console commands.
///
/// Everything is also mirrored into Player.log and offered on the clipboard, because the
/// in-game console cannot be selected from - and this report exists to be pasted somewhere
/// and read, not glanced at.
/// </summary>
public class ProbeCommands : IConsoleRewirer
{
    private const string Prefix = "prof.";

    private readonly ILogger Logger;
    private readonly RuntimeProbe Probe;
    private readonly LiveCounters Counters;
    private readonly ProfilerSession Session;
    private readonly ManagedCensus Managed;

    /// <summary>
    /// What the last command printed, kept for <c>prof.copy</c>. The counter listing is
    /// hundreds of lines, so the clipboard is the only realistic way to read it.
    /// </summary>
    private List<string> Captured = new List<string>();

    public ProbeCommands(ILogger logger, RuntimeProbe probe, LiveCounters counters, ProfilerSession session,
        ManagedCensus managed)
    {
        Logger = logger;
        Probe = probe;
        Counters = counters;
        Session = session;
        Managed = managed;
    }

    public void RegisterCommands(IDebugConsole console)
    {
        Register(console, "probe", context => Emit(context, Probe.Run()));
        Register(console, "counters", context => Emit(context, Probe.Counters()));

        // Resolving symbols calls nothing in the runtime, so this command is safe on any
        // build - which is the point of having it separate from the walk that uses them.
        Register(console, "native", context => Emit(context, MonoRuntime.Report()));

        // The console has no frames to spread the walk over, so it runs to completion here
        // with a ceiling on it. The panel's button is the better route: it walks a slice a
        // frame and the game keeps running.
        Register(console, "managed", context =>
        {
            Managed.RunToCompletion(4000);
            Emit(context, Managed.Summarise(40));
        });

        // Toggled, and read separately: the whole point is that frames have to pass between
        // opening a recorder and the number it reports meaning anything.
        RegisterWithArgument(console, "record", new DebugConsole.StringOption("mod"),
            context => Emit(context, Session.Record(context.GetString(0), ProfilerSession.DefaultBudget)));

        Register(console, "stop", context => Emit(context, Session.StopRecording()));

        Register(console, "watch", context => Emit(context, Counters.Start()));
        Register(console, "live", context => Emit(context, Counters.Read()));

        Register(console, "copy", context =>
        {
            if (Captured.Count == 0)
            {
                Emit(context, new[] { "Nothing captured yet - run prof.probe first." });
                return;
            }

            try
            {
                GUIUtility.systemCopyBuffer = string.Join("\n", Captured.ToArray());
                context.Output?.Invoke(Captured.Count + " lines copied.");
            }
            catch (Exception exception)
            {
                context.Output?.Invoke("Clipboard unavailable (" + exception.GetType().Name
                                       + ") - the report is in Player.log.");
                Logger.Exception?.LogException(exception);
            }
        });
    }

    /// <summary>
    /// Prints to the console, mirrors to the log, and keeps the lines for <c>prof.copy</c>.
    /// </summary>
    private void Emit(DebugConsole.CommandContext context, IEnumerable<string> lines)
    {
        List<string> captured = new List<string>();

        foreach (string line in lines)
        {
            captured.Add(line);
            context.Output?.Invoke(line);
            Logger.Info?.Log(line);
        }

        Captured = captured;
    }

    private void RegisterWithArgument(IDebugConsole console, string id, DebugConsole.ConsoleOption option,
        Action<DebugConsole.CommandContext> handler)
    {
        try
        {
            console.Register(Prefix + id, option, handler);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    /// One command failing to register must not take the rest of them down with it.
    private void Register(IDebugConsole console, string id, Action<DebugConsole.CommandContext> handler)
    {
        try
        {
            console.Register(Prefix + id, handler);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }
}
