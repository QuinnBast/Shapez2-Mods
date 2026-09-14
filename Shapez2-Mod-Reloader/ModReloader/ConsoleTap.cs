using System;
using System.Collections.Generic;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Remembers what the last console command printed, so it can be copied afterwards -
/// without having to know in advance that it was worth wrapping in <c>mrl.run</c>.
///
/// Every command's output goes through the <see cref="Action{T}"/> that
/// <see cref="DebugConsole.ParseAndExecute"/> is handed, so tapping that one argument
/// catches every command - the game's own and every other mod's - without touching any
/// of them.
/// </summary>
public class ConsoleTap : IDisposable
{
    /// <summary>
    /// Commands whose own output must not become the capture: these are the ones that
    /// exist to copy it, and their own chatter would displace what they were asked for.
    /// <c>mrl.run</c> is here because the command it runs is parsed and executed in turn,
    /// so that inner call captures the output that was actually wanted.
    /// </summary>
    private static readonly HashSet<string> Wrappers = new HashSet<string>
    {
        "mrl.run",
        "mrl.copy",
        "mrl.paste"
    };

    private readonly ILogger Logger;
    private readonly Hook OutputHook;

    /// Replaced wholesale by each captured command rather than cleared, so the tee handed
    /// to a command keeps writing to the list it started with even if it outlives the call.
    private List<string> Captured = new List<string>();

    public ConsoleTap(ILogger logger)
    {
        Logger = logger;

        try
        {
            OutputHook = DetourHelper.CreatePrefixHook<DebugConsole, string, Action<string>>(
                (console, command, output) => console.ParseAndExecute(command, output), Tap);
        }
        catch (Exception exception)
        {
            // An inert tap costs mrl.copy; a throw here would cost the whole mod.
            Logger.Exception?.LogException(exception);
        }
    }

    /// <summary>
    /// The last captured command's output, without the blank line
    /// <see cref="DebugConsole.ParseAndExecute"/> prints ahead of every command and
    /// without any blank line a command left behind.
    /// </summary>
    public List<string> LastOutput()
    {
        List<string> lines = Captured;

        int first = 0;
        int last = lines.Count - 1;

        while (first <= last && string.IsNullOrWhiteSpace(lines[first]))
        {
            first++;
        }

        while (last >= first && string.IsNullOrWhiteSpace(lines[last]))
        {
            last--;
        }

        return lines.GetRange(first, last - first + 1);
    }

    /// <summary>
    /// Records lines that no command printed, so <c>mrl.copy</c> can reach those too. The
    /// staged-build watcher reports this way: its reload rebuilds the session, which takes
    /// the console's history with it before anyone can read the report.
    /// </summary>
    public void Capture(IEnumerable<string> lines)
    {
        Captured = new List<string>(lines);
    }

    /// Swaps in an output action that records as well as prints.
    private (string, Action<string>) Tap(DebugConsole console, string command, Action<string> output)
    {
        try
        {
            if (output == null || command == null || IsWrapper(command))
            {
                return (command, output);
            }

            List<string> lines = new List<string>();
            Captured = lines;

            return (command, line =>
            {
                lines.Add(line);
                output(line);
            });
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            return (command, output);
        }
    }

    private static bool IsWrapper(string command)
    {
        string trimmed = command.TrimStart();
        int end = trimmed.IndexOf(' ');

        return Wrappers.Contains((end < 0 ? trimmed : trimmed.Substring(0, end)).ToLowerInvariant());
    }

    public void Dispose()
    {
        OutputHook?.Dispose();
    }
}
