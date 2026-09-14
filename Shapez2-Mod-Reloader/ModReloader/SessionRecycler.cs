using System;
using System.Collections.Generic;
using Game.Core.Serialization;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Rebuilds the game session without restarting the game.
///
/// This is what makes a reload cover new *content* rather than only new code. Almost
/// nothing a mod registers lives for the process: buildings and islands are baked in
/// <c>GameMode.From</c>, meshes and animations into the session's own <c>MeshCache</c>,
/// the toolbar in <c>ToolbarBuilder.BuildToolbar</c>, panels and console commands in the
/// orchestrator's <c>Init_</c> steps. All of it is built per session, from the vanilla
/// baseline, with Shifter's interceptors consulting the mod on the way past - so
/// rebuilding the session applies the new code's content exactly as a fresh launch would.
/// Swapping the assembly alone cannot: that content was already built, by the old code.
///
/// The route is the game's own <c>IGameFlowNavigator.LoadSession</c> - what the pause menu
/// and the end-of-scenario dialog use - and the sequence mirrors
/// <c>ScenarioTransferGameOptionsFactory</c>, which does this to move a save into a new
/// scenario.
/// </summary>
public class SessionRecycler
{
    /// <summary>
    /// A saved session and everything needed to re-enter it.
    ///
    /// Held across the swap because the two halves have to straddle it: only the old
    /// instance can still write its own save data, and only the new one can build the
    /// session that replaces it.
    /// </summary>
    public class Handle
    {
        public readonly string Uid;
        public readonly SaveFileAccessor Accessor;
        public readonly ISavegameManager Savegames;
        public readonly IGameFlowNavigator Navigator;

        public Handle(string uid, SaveFileAccessor accessor, ISavegameManager savegames,
            IGameFlowNavigator navigator)
        {
            Uid = uid;
            Accessor = accessor;
            Savegames = savegames;
            Navigator = navigator;
        }
    }

    private readonly ILogger Logger;
    private readonly ModRegistry Registry;

    public SessionRecycler(ILogger logger, ModRegistry registry)
    {
        Logger = logger;
        Registry = registry;
    }

    /// <summary>
    /// Saves the running session, and captures what re-entering it will need.
    ///
    /// Called before the old mod instance is disposed: it is the only code that can still
    /// write its own save data, and everything since the last save would otherwise be
    /// rolled back by the reload that follows. A null handle means there is nothing to
    /// rebuild and the reload should stay a code-only one - it is not a failure.
    /// </summary>
    public Handle TrySave(List<string> report)
    {
        if (!Registry.TryGetSession(out GameSessionOrchestrator orchestrator))
        {
            report.Add("  no session yet, so this reloads code only");
            return null;
        }

        try
        {
            // TryGetCurrentSessionSaveManagers throws on a disposed orchestrator, and a
            // stale one is exactly what we would be holding between two sessions.
            if (orchestrator.IsDisposed)
            {
                report.Add("  the session is shutting down, so this reloads code only");
                return null;
            }

            IGameSessionManagersProvider provider = orchestrator;

            if (!provider.TryGetCurrentSessionSaveManagers(out Savegame _, out SaveFileAccessor accessor,
                    out IGameSessionSaver saver, out SavegameOptionsManager options))
            {
                report.Add("  the session has no savegame, so this reloads code only");
                return null;
            }

            // The main menu runs a session too, over a throwaway map with uid "menu".
            // Saving that would write a savegame nobody asked for, and re-entering it
            // would drop the player into it.
            //
            // Headless is flagged obsolete by the game with no replacement offered - its
            // own code still checks it everywhere, which is the note's actual complaint.
#pragma warning disable CS0618
            if (options.Headless)
#pragma warning restore CS0618
            {
                report.Add("  that is the main menu's background map, not a savegame - code only");
                return null;
            }

            if (!saver.TrySaveCurrentSync())
            {
                report.Add("  the save failed, so the session is left alone rather than rolled back");
                return null;
            }

            report.Add("  saved " + options.Uid);

            return new Handle(
                options.Uid,
                accessor,
                orchestrator.DependencyContainer.Resolve<ISavegameManager>(),
                orchestrator.DependencyContainer.Resolve<IGameFlowNavigator>());
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.Add("  could not save the session - see the log; reloading code only");
            return null;
        }
    }

    /// <summary>
    /// Re-enters the saved game, which tears the session down and builds a new one - and
    /// with it every piece of content the reloaded mods register.
    /// </summary>
    public bool TryReload(Handle handle, List<string> report)
    {
        try
        {
            SavegameReference reference = handle.Savegames.FindMostRecentSavegameEntryByUid(handle.Uid);

            if (reference == null)
            {
                report.Add("  could not find the save just written, so the session is left alone");
                return false;
            }

            SavegameBlobReader reader = handle.Accessor.Read(
                reference.FullPath, new Dictionary<Type, IDataSerializer>());

            // Deliberately not awaited, as the main menu and the pause menu also leave it:
            // LoadSession shows the loading screen before it unloads anything, so the
            // session is torn down a frame later - after this command has returned. Doing
            // it synchronously would dispose the console midway through printing this
            // report.
            handle.Navigator.LoadSession(
                new GameStartOptionsContinueExisting(reader, menuMode: false, reference.Uid));

            report.Add("  rebuilding the session - new content is applied as it comes back");
            return true;
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.Add("  could not rebuild the session - see the log. The new code is loaded;");
            report.Add("  its content will appear next time the session is built.");
            return false;
        }
    }
}
