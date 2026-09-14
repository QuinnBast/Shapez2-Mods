using System;
using System.Collections.Generic;
using ShapezShifter.Hijack;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Console commands, registered directly as an <see cref="IConsoleRewirer"/> so they keep a
/// short prefix rather than being named after the assembly.
///
/// Includes the clipboard commands, which exist because there is no way to select text in
/// the in-game console - so any command that prints something worth keeping is unreadable
/// outside the game. <c>mrl.run</c> runs another command and captures what it printed;
/// <c>mrl.copy</c> copies what the last command printed, whether or not it was run that
/// way, which is what <see cref="ConsoleTap"/> is for.
/// </summary>
public class ReloaderCommands : IConsoleRewirer
{
    private const string Prefix = "mrl.";

    private readonly ILogger Logger;
    private readonly ModRegistry Registry;
    private readonly Reloader Reloader;
    private readonly Seeder Seeder;
    private readonly ConsoleTap Tap;
    private readonly StagedBuildWatcher Watcher;

    public ReloaderCommands(ILogger logger, ModRegistry registry, Reloader reloader, Seeder seeder,
        ConsoleTap tap, StagedBuildWatcher watcher)
    {
        Logger = logger;
        Registry = registry;
        Reloader = reloader;
        Seeder = seeder;
        Tap = tap;
        Watcher = watcher;
    }

    public void RegisterCommands(IDebugConsole console)
    {
        Register(console, "list", null, context => Emit(context, Registry.Describe()));

        Register(console, "reload", new DebugConsole.StringOption("mod"),
            context => Emit(context, Reloader.Reload(context.GetString(0))));

        // Toggled rather than started: there is no way to pass a flag, and leaving a
        // watcher running for the rest of the session is not always what you want.
        Register(console, "watch", null, context => Emit(context, Watcher.Toggle()));

        // Runs at startup too; this is for when a staged build appears mid-session.
        Register(console, "seed", null, context => Emit(context, Seeder.Seed()));

        // Run another command, print what it printed, and put it on the clipboard.
        Register(console, "run", new DebugConsole.StringOption("command"), context =>
        {
            string command = context.GetString(0);
            List<string> captured = new List<string>();

            try
            {
                console.ParseAndExecute(command, line => captured.Add(line));
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
                captured.Add("\"" + command + "\" threw - see the log.");
            }

            Emit(context, captured);

            if (TrySetClipboard(string.Join(Environment.NewLine, captured.ToArray()), out string problem))
            {
                context.Output?.Invoke("[" + captured.Count + " lines copied to the clipboard]");
            }
            else
            {
                context.Output?.Invoke("[clipboard unavailable: " + problem + " - the lines are in Player.log]");
            }
        });

        // Copies the last command's output, so a command only worth keeping once it has
        // been seen does not have to be run a second time through mrl.run.
        Register(console, "copy", null, context =>
        {
            List<string> captured = Tap.LastOutput();

            if (captured.Count == 0)
            {
                context.Output?.Invoke("Nothing to copy - the last command printed nothing.");
                return;
            }

            context.Output?.Invoke(
                TrySetClipboard(string.Join(Environment.NewLine, captured.ToArray()), out string problem)
                    ? "[" + captured.Count + " lines copied to the clipboard]"
                    : "Clipboard unavailable: " + problem + " - the lines are in Player.log.");
        });

        // The console has no paste either, so this runs whatever is on the clipboard -
        // which is also the way to run a command that needs several arguments.
        Register(console, "paste", null, context =>
        {
            string command;
            try
            {
                command = GUIUtility.systemCopyBuffer;
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
                context.Output?.Invoke("Could not read the clipboard - see the log.");
                return;
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                context.Output?.Invoke("The clipboard is empty.");
                return;
            }

            command = command.Trim();
            context.Output?.Invoke("> " + command);

            try
            {
                console.ParseAndExecute(command, line => Emit(context, line));
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
                context.Output?.Invoke("That command threw - see the log.");
            }
        });
    }

    /// <summary>
    /// Prints to the console and mirrors to the log, so output survives even when the
    /// clipboard does not work and the console cannot be scrolled back.
    /// </summary>
    private void Emit(DebugConsole.CommandContext context, IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Emit(context, line);
        }
    }

    private void Emit(DebugConsole.CommandContext context, string line)
    {
        context.Output?.Invoke(line);
        Logger.Info?.Log(line);
    }

    private static bool TrySetClipboard(string text, out string problem)
    {
        problem = null;

        try
        {
            GUIUtility.systemCopyBuffer = text;
            return true;
        }
        catch (Exception exception)
        {
            problem = exception.GetType().Name;
            return false;
        }
    }

    /// One command failing to register must not take the rest of them down with it.
    private void Register(IDebugConsole console, string id, DebugConsole.ConsoleOption option,
        Action<DebugConsole.CommandContext> handler)
    {
        try
        {
            if (option == null)
            {
                console.Register(Prefix + id, handler);
            }
            else
            {
                console.Register(Prefix + id, option, handler);
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }
}
