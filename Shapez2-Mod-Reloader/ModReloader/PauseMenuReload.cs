using System;
using System.Collections.Generic;
using Core.Localization;
using DG.Tweening;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using TMPro;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// A "Reload Mods" button in the pause menu, doing exactly what <c>mrl.reload</c> does:
/// save the session, swap every staged build, and re-enter the save so the new code's
/// content comes back with it.
///
/// The console can already do this, but it costs opening F1 and typing a mod name. The
/// pause menu is where you already are when you tab back in from a rebuild, and it is the
/// one screen that pauses the simulation while you decide.
///
/// Unlike <see cref="CrashScreenReload"/> there is a live session here, so this is the full
/// round trip rather than a code-only reload.
/// </summary>
public class PauseMenuReload : IDisposable
{
    /// <summary>
    /// The clone's object name, and how a later <c>Show</c> finds the button it already
    /// made. The pause menu is shown and hidden repeatedly within one session.
    /// </summary>
    private const string ButtonName = "UIBtnReloadMods";

    private readonly ILogger Logger;
    private readonly Reloader Reloader;
    private readonly ConsoleTap Tap;
    private readonly Hook ShowHook;
    private readonly Hook DisposeHook;

    /// <summary>
    /// The button this mod added, kept only so its tweens can be killed when the menu goes
    /// away. See <see cref="OnMenuDisposed"/> for why the game will not do it for us.
    /// </summary>
    private HUDMenuButton Button;

    public PauseMenuReload(ILogger logger, Reloader reloader, ConsoleTap tap)
    {
        Logger = logger;
        Reloader = reloader;
        Tap = tap;

        // Show rather than Construct: Construct takes ten dependencies and DetourHelper's
        // expression overloads stop at eight. Show also runs after every field the clone
        // needs is populated, and re-running it is harmless.
        ShowHook = DetourHelper.CreatePostfixHook<HUDPauseMenu>(menu => menu.Show(), OnShown);
        DisposeHook = DetourHelper.CreatePostfixHook<HUDPauseMenu>(menu => menu.OnDispose(), OnMenuDisposed);
    }

    public void Dispose()
    {
        DisposeHook?.Dispose();
        ShowHook?.Dispose();
    }

    private void OnShown(HUDPauseMenu menu)
    {
        try
        {
            if (menu == null || menu.UISaveBtn == null)
            {
                return;
            }

            Button = Find(menu.UISaveBtn) ?? Clone(menu.UISaveBtn);

            Button.OnClick.RemoveAllListeners();

            // Instantiate did not copy this: HUDMenuButton adds it in Construct, which the
            // clone never gets. Without it the button neither clicks nor punches like the
            // ones beside it.
            Button.OnClick.AddListener(Button.PlayOnClickAnimation);
            Button.OnClick.AddListener(() => Reload(menu));
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    /// <summary>
    /// Copies the Save button, which is the closest thing to what this one does and sits
    /// where it belongs in the list.
    ///
    /// A clone is never constructed - the game's DI runs over a prefab's serialized child
    /// list, which a runtime clone is not in - so two pieces of its state have to be filled
    /// in by hand, and a third has to be avoided entirely. See the comments below; each is
    /// a throw or a null dereference waiting for the first person who hovers the button.
    /// </summary>
    private static HUDMenuButton Clone(HUDMenuButton template)
    {
        GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
        clone.name = ButtonName;
        clone.SetActive(true);
        clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

        HUDMenuButton button = clone.GetComponent<HUDMenuButton>();

        // UpdateActiveState calls UISoundPlayer.PlayButtonHover() on any hover, guarded only
        // by interactable. An unconstructed clone has a null one, so this is the difference
        // between a button and a NullReferenceException the first time the mouse crosses it.
        button.UISoundPlayer = template.UISoundPlayer;

        // The label is set straight on the TMP_Text rather than through HUDMenuButton.Text.
        // That property goes to HUDLocalizedText.UpdateView, which throws outright -
        // "Accessing HUDLocalizedText ... which has not been constructed yet" - whenever its
        // resolver is null, which on an unconstructed clone it always is. Nothing else calls
        // UpdateView on the clone, so the text set here is the text that stays.
        foreach (TMP_Text text in clone.GetComponentsInChildren<TMP_Text>(includeInactive: true))
        {
            text.text = "Reload Mods";
        }

        return button;
    }

    private static HUDMenuButton Find(HUDMenuButton template)
    {
        Transform parent = template.transform.parent;
        Transform existing = parent == null ? null : parent.Find(ButtonName);

        return existing == null ? null : existing.GetComponent<HUDMenuButton>();
    }

    /// <summary>
    /// Saves, swaps every staged build, and re-enters the save.
    ///
    /// Everything is <see cref="Reloader"/>'s: this is the same call <c>mrl.reload</c>
    /// makes, so the save-swap-rebuild ordering and all of its refusals are shared rather
    /// than reimplemented here.
    /// </summary>
    private void Reload(HUDPauseMenu menu)
    {
        List<string> report = new List<string> { "Reloading from the pause menu." };

        try
        {
            List<string> staged = StagedBuildWatcher.StagedFolders();

            if (staged.Count == 0)
            {
                report.Add("  nothing is staged in mods-dev, so there is no new code to load");
                Notify(menu, HUDNotificationType.Error, "Nothing staged in mods-dev");
            }
            else
            {
                report.AddRange(Reloader.Reload(staged));

                // The notification will not outlive the session rebuild that follows it, but
                // it is the only feedback when the reload declines to rebuild - a refused
                // mod, a failed save - and the menu stays up in exactly those cases.
                Notify(menu, HUDNotificationType.Save, "Reloading " + staged.Count + " staged mod(s)");
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.Add("  the reload itself threw - see Player.log");
            Notify(menu, HUDNotificationType.Error, "Reload failed - see Player.log");
        }

        foreach (string line in report)
        {
            Logger.Info?.Log(line);
        }

        // The rebuilt session takes the console's history with it, so leave the report where
        // mrl.copy can still reach it - the same reason StagedBuildWatcher does.
        Tap.Capture(report);
    }

    private void Notify(HUDPauseMenu menu, HUDNotificationType type, string message)
    {
        try
        {
            menu.Events?.ShowNotification.Invoke(
                new HUDNotificationData(type, new RawText(message), null, null, 3f));
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    /// <summary>
    /// Kills the clone's tweens when the pause menu goes away.
    ///
    /// <c>HUDMenuButton.OnDispose</c> does this for the game's own buttons, but it is driven
    /// from a prefab's serialized child list that a runtime clone is not in - so the clone is
    /// never disposed. A hover tween still running when the session tears down is left
    /// pointing at a destroyed transform, which is precisely the moment this button creates:
    /// click it and the session is rebuilt a frame later.
    /// </summary>
    private void OnMenuDisposed(HUDPauseMenu menu)
    {
        try
        {
            if (Button == null)
            {
                return;
            }

            DOTween.Kill(Button.UIMainTransform, complete: true);
            DOTween.Kill(Button.UIActiveIndicatorGroup, complete: true);
            DOTween.Kill(Button.UIHoverIndicatorGroup, complete: true);

            if (Button.UIText != null)
            {
                DOTween.Kill(Button.UIText.transform, complete: true);
            }

            Button = null;
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }
}
