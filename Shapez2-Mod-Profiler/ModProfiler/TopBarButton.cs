using System;
using Core.Localization;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using UnityEngine;
using UnityEngine.UI;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModProfiler;

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

            Brand(button);

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

        return button;
    }

    /// <summary>
    /// Gives the button its own icon and tooltip.
    ///
    /// Applied on every attach rather than only when the button is first cloned: a button
    /// inherited from a previous generation is carrying that generation's sprite, and the
    /// tooltip text the old assembly allocated.
    ///
    /// <c>_Icon</c> is written as well as <c>UIIcon.sprite</c> for the reason the tooltip fields
    /// are - <c>HUDIconButton.Run()</c> copies the serialized field over the live one, so setting
    /// only the live one leaves the Statistics icon to come back the next time anything runs the
    /// view.
    /// </summary>
    private void Brand(HUDIconButton button)
    {
        try
        {
            Sprite glyph = PanelIcon.FlameGraph();

            button._Icon = glyph;

            Image icon = button.UIIcon;

            if (icon != null)
            {
                icon.sprite = glyph;

                // The bar's icons rest at 0.75 alpha and animate to 1 on hover, which is
                // Construct's work and does not happen on a clone. Splitting the difference
                // keeps it from reading as disabled beside the real ones.
                icon.color = new Color(1f, 1f, 1f, 0.9f);
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }

        Describe(button);
    }

    /// <summary>
    /// Gives the clone its own tooltip.
    ///
    /// **Why the clone had an empty one.** <c>HUDIconButton</c> resolves its serialized
    /// translation ids into <c>_TooltipTitle</c> and <c>_TooltipText</c> in <c>[Construct]</c>,
    /// which never runs on a runtime clone - and <c>Run()</c> then copies those two nulls over
    /// the <c>HUDTooltipTarget</c>'s own serialized ids. So the tooltip opened and said nothing:
    /// not a missing translation, a null written over a good value.
    ///
    /// **Why the fields and not the properties.** The <c>TooltipTitle</c> and <c>TooltipText</c>
    /// setters are safe, but they are the only two that are. <c>HasTooltip</c>,
    /// <c>TooltipKeybinding</c>, <c>Interactable</c> and <c>Highlighted</c> all end in
    /// <c>OnHighlightChanged</c>, which dereferences <c>TutorialHighlightProvider</c> - another
    /// thing <c>[Construct]</c> would have set and did not. Writing the backing fields and
    /// calling <c>UpdateTooltipConfig</c> touches only the tooltip target.
    ///
    /// The keybinding is cleared for the same reason it has to be set at all: it is Statistics',
    /// and this button does not have one.
    /// </summary>
    private void Describe(HUDIconButton button)
    {
        try
        {
            button._TooltipKeybinding = string.Empty;
            button._TooltipTitle = new RawText("Mod Profiler");
            button._TooltipText = new RawText(
                "Frame time, what the loaded mods are holding on the heap, and a flame graph for "
                + "one mod's own methods. Escape closes it.");

            button.UpdateTooltipConfig();
        }
        catch (Exception exception)
        {
            // A button with no tooltip is the behaviour this replaces, not a reason to lose the
            // button.
            Logger.Exception?.LogException(exception);
        }
    }
}
