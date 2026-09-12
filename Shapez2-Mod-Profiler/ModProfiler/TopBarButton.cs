using System;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using UnityEngine;
using UnityEngine.UI;
using ILogger = Core.Logging.ILogger;

/// <summary>
/// Puts a Mod Profiler button in the top bar, beside Statistics.
///
/// Cloned from the Statistics button itself, which is the nearest thing in the bar to what
/// this opens and carries the bar's own styling and hover behaviour.
///
/// Hooked on <c>UIUpdateState</c> rather than <c>Construct</c>: <c>Construct</c> takes nine
/// dependencies and <c>DetourHelper</c>'s expression overloads stop at eight. It is a better
/// hook point anyway - it runs on construction and again on every state change, so the button
/// is restored if anything rebuilds the bar.
/// </summary>
public class TopBarButton : IDisposable
{
    private const string ButtonName = "UIBtnModProfiler";

    private readonly ILogger Logger;
    private readonly DebugPanel Panel;
    private readonly Hook UpdateHook;

    public TopBarButton(ILogger logger, DebugPanel panel)
    {
        Logger = logger;
        Panel = panel;

        UpdateHook = DetourHelper.CreatePostfixHook<HUDSystemButtonsView>(
            view => view.UIUpdateState(), Attach);
    }

    public void Dispose()
    {
        UpdateHook?.Dispose();
    }

    private void Attach(HUDSystemButtonsView view)
    {
        try
        {
            if (view == null || view.UIStatisticsButton == null)
            {
                return;
            }

            HUDIconButton template = view.UIStatisticsButton;
            Transform parent = template.transform.parent;
            Transform existing = parent == null ? null : parent.Find(ButtonName);

            // Re-pointed rather than left alone. A button left over from a previous generation
            // is still wired to that generation's panel, which was destroyed when the old mod
            // instance was disposed - so it would sit there looking fine and do nothing.
            HUDIconButton button = existing == null
                ? Clone(template)
                : existing.GetComponent<HUDIconButton>();

            if (button == null)
            {
                return;
            }

            button.OnClick.RemoveAllListeners();
            button.OnClick.AddListener(button.PlayClickAnimation);

            if (Panel != null)
            {
                button.OnClick.AddListener(Panel.Toggle);
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    /// <summary>
    /// Copies the Statistics button and makes the copy usable.
    ///
    /// A runtime clone is never constructed - the game's DI walks a prefab's serialized child
    /// list, which a clone is not in - so the pieces <c>[Construct]</c> would have set have to
    /// be set here. Each of the three below is a crash or a wrong behaviour otherwise.
    /// </summary>
    private HUDIconButton Clone(HUDIconButton template)
    {
        GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
        clone.name = ButtonName;
        clone.SetActive(true);
        clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

        HUDIconButton button = clone.GetComponent<HUDIconButton>();

        // OnPointerEnter calls UISoundPlayer.PlayButtonHover() unguarded, so without this the
        // button throws the first time the mouse crosses it.
        button.UISoundPlayer = template.UISoundPlayer;

        // The clone carries Statistics' own icon and tooltip translation ids, and there is no
        // icon of our own to swap in. Tinting is the honest minimum: it makes the button
        // findable and obviously not the one beside it. The tooltip is left alone because
        // setting it goes through HUDLocalizedText, which throws on an unconstructed clone.
        Image icon = button.UIIcon;

        if (icon != null)
        {
            icon.color = new Color(0.55f, 0.9f, 1f, 0.95f);
        }

        return button;
    }
}
