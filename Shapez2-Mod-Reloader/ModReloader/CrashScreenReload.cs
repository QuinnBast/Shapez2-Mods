using System;
using System.Collections.Generic;
using Game.Orchestration;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// A "Reload Mods" button on the game's crash screen.
///
/// When a mod throws, <c>GameOrchestrator.HandleFatalException</c> shows the crash overlay
/// and then awaits <c>TryDisposing</c>, so by the time the screen is up the session is
/// already unloaded. Vanilla offers no way on from there - Copy to Clipboard and Report on
/// Discord both assume you are about to restart - and a restart is the two-minute wait this
/// whole mod exists to avoid, for a fix that is usually one line and already built.
///
/// This adds a third button beside those two: swap every staged build, then take the game's
/// own route back to the main menu.
///
/// It cannot save anything. The session was torn down before the screen appeared, so this
/// is necessarily a code-only reload and the new content appears when the save is re-entered
/// from the menu - which is also the point of landing there rather than in the save. A mod
/// that throws while a session is loading would otherwise be re-entered straight back into
/// the crash.
/// </summary>
public class CrashScreenReload : IDisposable
{
    /// <summary>
    /// The clone's object name, and also how a later crash finds the button already made.
    /// <see cref="HUDCrashOverlay.Setup"/> runs again once <see cref="Dismiss"/> clears its
    /// guard, and cloning unconditionally would stack up one button per crash.
    /// </summary>
    private const string ButtonName = "UIBtnReloadMods";

    private readonly ILogger Logger;
    private readonly Reloader Reloader;
    private readonly ConsoleTap Tap;
    private readonly Hook SetupHook;

    /// <summary>
    /// Revives input while the crash screen is up. Held so it can be taken away again once
    /// the screen is dismissed. See <see cref="CrashScreenInput"/> for why it is needed.
    /// </summary>
    private CrashScreenInput Input;

    public CrashScreenReload(ILogger logger, Reloader reloader, ConsoleTap tap)
    {
        Logger = logger;
        Reloader = reloader;
        Tap = tap;

        // HUDCrashOverlay is a plain MonoBehaviour and Setup is non-generic, so this is an
        // ordinary hook rather than the relocation a generic type would have forced.
        SetupHook = DetourHelper.CreatePostfixHook<HUDCrashOverlay, string, string>(
            (overlay, message, version) => overlay.Setup(message, version), Attach);
    }

    public void Dispose()
    {
        SetupHook?.Dispose();
    }

    /// <summary>
    /// Adds the button, or re-points the one a previous crash left behind.
    ///
    /// Setup is re-entrant in one direction only - it does nothing when an error is already
    /// showing - so this has to tolerate running against a screen that is already dressed.
    /// </summary>
    private void Attach(HUDCrashOverlay overlay, string message, string version)
    {
        try
        {
            if (overlay == null)
            {
                return;
            }

            Button template = overlay.UIBtnOpenDiscord ?? overlay.UIBtnCopyToClipboard;

            if (template == null)
            {
                Logger.Info?.Log("Crash screen has no button to copy, so no Reload button was added.");
                return;
            }

            Button button = Find(template) ?? Clone(overlay, template);

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => Reload(overlay));

            // Not once, and not now: the EventSystem that is about to be destroyed is still
            // alive at this point, so the gap this covers opens a moment later.
            if (Input == null)
            {
                Input = CrashScreenInput.Attach();
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    /// <summary>
    /// Copies one of the game's own buttons rather than building one.
    ///
    /// The crash screen is a prefab: its buttons carry the game's styling, which is the
    /// difference between matching the look and reimplementing it. What a clone does *not*
    /// inherit is a position of its own - see <see cref="Place"/>.
    /// </summary>
    private static Button Clone(HUDCrashOverlay overlay, Button template)
    {
        GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
        clone.name = ButtonName;
        clone.SetActive(true);

        Place(overlay, clone);

        Button button = clone.GetComponent<Button>();

        // Instantiate carries over inspector-wired listeners, and RemoveAllListeners does
        // not touch those - it only drops the ones added at runtime. Setup wires both
        // vanilla buttons at runtime, so there should be none to disable here; a clone that
        // still opened Discord on click would be the tell that there is.
        for (int i = button.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
        {
            button.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        foreach (TMP_Text text in clone.GetComponentsInChildren<TMP_Text>(includeInactive: true))
        {
            text.text = "Reload Mods";
        }

        return button;
    }

    /// <summary>
    /// Moves the clone off the button it was copied from.
    ///
    /// A clone of an absolutely positioned RectTransform keeps the original's
    /// anchoredPosition, so it lands exactly on top of its template - which is why the
    /// Reload button first appeared to *replace* Report on Discord rather than join it. The
    /// crash screen has no layout group driving its buttons, so nothing moves it afterwards.
    ///
    /// The step is measured from the two buttons the game already has, so the third one
    /// continues whatever row or column they form, in whatever direction, at their own
    /// spacing. If a layout group *is* present the position is left alone and the sibling
    /// order decides, because a layout group would overwrite anything set here.
    /// </summary>
    private static void Place(HUDCrashOverlay overlay, GameObject clone)
    {
        Transform parent = clone.transform.parent;

        if (parent != null && parent.GetComponent<LayoutGroup>() != null)
        {
            return;
        }

        RectTransform placed = clone.transform as RectTransform;
        RectTransform first = overlay.UIBtnCopyToClipboard == null
            ? null
            : overlay.UIBtnCopyToClipboard.transform as RectTransform;
        RectTransform second = overlay.UIBtnOpenDiscord == null
            ? null
            : overlay.UIBtnOpenDiscord.transform as RectTransform;

        if (placed == null || second == null)
        {
            return;
        }

        if (first == null)
        {
            return;
        }

        Vector2 step = second.anchoredPosition - first.anchoredPosition;

        // Both buttons in the same place: fall back to stacking by the button's own height,
        // which is the one measurement always to hand.
        if (step.sqrMagnitude < 1f)
        {
            step = new Vector2(0f, placed.rect.height + 8f);
        }

        // Placed *before* the first button rather than after the last. Continuing the row
        // outwards put it off the right edge of the screen - the row is sized for the two
        // buttons the game ships, and nothing here can widen it. Going the other way uses
        // the space the row is already centred in.
        placed.anchoredPosition = first.anchoredPosition - step;
    }

    private static Button Find(Button template)
    {
        Transform parent = template.transform.parent;
        Transform existing = parent == null ? null : parent.Find(ButtonName);

        return existing == null ? null : existing.GetComponent<Button>();
    }

    /// <summary>
    /// Swaps every staged build, then hands the game back to its own main menu.
    /// </summary>
    private void Reload(HUDCrashOverlay overlay)
    {
        List<string> report = new List<string> { "Reloading from the crash screen." };

        try
        {
            List<string> staged = StagedBuildWatcher.StagedFolders();

            if (staged.Count == 0)
            {
                report.Add("  nothing is staged in mods-dev, so there is no new code to load");
            }
            else
            {
                report.AddRange(Reloader.Reload(staged));
            }

            Dismiss(overlay);

            if (GameBootstrapper.GameOrchestrator is IGameFlowNavigator navigator)
            {
                // Not awaited, for the reason SessionRecycler gives: LoadMainMenu shows the
                // loading screen before it unloads anything, so the teardown lands a frame
                // later - after this click handler has returned.
                navigator.LoadMainMenu();
                report.Add("  returning to the main menu - re-enter the save to apply new content");
            }
            else
            {
                report.Add("  the game's navigator was not reachable, so a restart is still needed");
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.Add("  the reload itself threw - see Player.log. A restart is the way out.");
        }

        foreach (string line in report)
        {
            Logger.Info?.Log(line);
        }

        // The console does not exist at this point and the session that would rebuild it is
        // gone, so Player.log and mrl.copy are the only places this report can be read.
        Tap.Capture(report);
    }

    /// <summary>
    /// Puts the overlay back the way <see cref="GameView"/>'s constructor leaves it.
    ///
    /// Nothing in the game does this. <c>GameView</c> hides the overlay once, at
    /// construction, and <c>Setup</c> never clears <c>CurrentError</c> - it has no reason
    /// to, because vanilla never comes back from a crash. Leaving either behind would paint
    /// a dead crash screen over the main menu, and silently swallow the *next* crash, which
    /// is exactly the crash a second attempt at the same fix produces.
    ///
    /// The vanilla listeners go too. Setup adds them with AddListener each time it runs, so
    /// without this a second crash would copy to the clipboard twice and open two Discord
    /// tabs.
    /// </summary>
    private void Dismiss(HUDCrashOverlay overlay)
    {
        overlay.UIBtnCopyToClipboard?.onClick.RemoveAllListeners();
        overlay.UIBtnOpenDiscord?.onClick.RemoveAllListeners();
        overlay.CurrentError = null;

        if (overlay.UIErrorParent != null)
        {
            overlay.UIErrorParent.SetActive(false);
        }

        overlay.gameObject.SetActive(false);

        // The main menu builds its own EventSystem, and two enabled at once is a state Unity
        // warns about and resolves arbitrarily.
        Input?.Detach();
        Input = null;
    }
}
