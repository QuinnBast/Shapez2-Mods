using System;
using System.Collections.Generic;
using ShapezShifter.Hijack;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModProfiler;

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

        // The panel's typeface is a probe, not a certainty: whether the game's own font is
        // reachable from a mod depends on whether its TMP asset kept its source file. These two
        // commands are how that question gets answered by looking at the screen.
        Register(console, "fonts", context => Emit(context, Fonts()));

        // The other half of "does the page look right": whether the game's own full-screen
        // background was found, or whether the panel is falling back to its generated gradient.
        Register(console, "backdrop", context =>
        {
            GameBackdrop.Resolve();
            Emit(context, new[] { GameBackdrop.Status });
        });

        RegisterWithArgument(console, "font", new DebugConsole.StringOption("name"),
            context => Emit(context, new[] { PanelFont.Apply(context.GetString(0)) }));

        // Judging a background is a thing you do by looking at it beside a real page, which is
        // not something the numbers can settle on their own. This makes that a live dial.
        RegisterWithArgument(console, "bg", new DebugConsole.StringOption("scale"),
            context => Emit(context, new[] { Brightness(context.GetString(0)) }));

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

    /// <summary>Scales the panel's ground. 1 is the tuned value; useful range is about 0.6 to 1.6.</summary>
    private static string Brightness(string argument)
    {
        if (!float.TryParse(argument, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float scale))
        {
            return "Give it a number, for example prof.bg 1.2. Currently "
                   + PanelTheme.Brightness.ToString("0.00") + ".";
        }

        PanelTheme.Brightness = UnityEngine.Mathf.Clamp(scale, 0.2f, 2.5f);

        return "Background brightness is now " + PanelTheme.Brightness.ToString("0.00")
               + ". Tell me the value that looks right and it becomes the default.";
    }

    /// <summary>Every face the panel could be drawn in, and which one it is using.</summary>
    private static IEnumerable<string> Fonts()
    {
        List<string> lines = new List<string>();
        List<UnityEngine.Font> candidates = PanelFont.Candidates();

        lines.Add("Panel font: " + (PanelTheme.CurrentFont == null
            ? "IMGUI built-in"
            : PanelTheme.CurrentFont.name));

        if (candidates.Count == 0)
        {
            lines.Add("No dynamic font is loaded in this process, so there is nothing to switch to.");
            lines.Add("That means every TMP_FontAsset in the build dropped its sourceFontFile.");

            return lines;
        }

        lines.Add(candidates.Count + " candidate(s) - prof.font <name> to use one, prof.font default to revert:");

        foreach (UnityEngine.Font font in candidates)
        {
            lines.Add("  " + font.name);
        }

        return lines;
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
