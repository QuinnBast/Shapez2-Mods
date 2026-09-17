using UnityEngine;
using UnityEngine.UI;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Makes the scenario picker scroll.
///
/// The menu lays its scenario cards straight into a <c>RectTransform</c>
/// (<c>HUDMenuSelectScenarioState.UIScenariosParent</c>) with no scroll view around them,
/// which is fine for the seven the game ships and not fine for the eighth. This mod adds the
/// eighth, so it is the mod's problem: any mod that adds a scenario overflows that row, and
/// the fix belongs with whoever caused it.
///
/// Nothing here is first-person specific, and it is deliberately defensive - it is surgery on
/// the main menu's live hierarchy, so every step that could be wrong is a reason to leave the
/// menu exactly as it was rather than half-converted.
/// </summary>
public static class FirstPersonScenarioMenu
{
    private const string ScrollObjectName = "FirstPerson.ScenarioScroll";

    /// <summary>
    /// Wraps <paramref name="content"/> in a <c>ScrollRect</c> if it is not inside one already.
    ///
    /// Idempotent, and it stands down when someone else got there first: the test is
    /// <c>GetComponentInParent</c>, so a scroll view added by the game in a later version or
    /// by another mod is left alone. That is what makes it safe to call on every entry to the
    /// menu, which is what happens - the state is re-entered rather than rebuilt, and it
    /// clears and re-creates the cards each time.
    /// </summary>
    public static void EnsureScrollable(RectTransform content, ILogger logger)
    {
        if (content == null || content.GetComponentInParent<ScrollRect>() != null)
        {
            return;
        }

        // The cards are positioned by a layout group, not by hand - `PlaceAt` only
        // instantiates under the parent. Without one, a ContentSizeFitter has no preferred
        // size to read and would collapse the row to nothing, so "no group" is a reason to do
        // nothing rather than a case to handle.
        LayoutGroup group = content.GetComponent<LayoutGroup>();
        if (group == null)
        {
            logger?.Info?.Log("First Person: the scenario list has no layout group, leaving it alone.");
            return;
        }

        bool horizontal = IsHorizontal(group);

        Transform parent = content.parent;
        if (parent == null)
        {
            return;
        }

        GameObject host = new GameObject(
            ScrollObjectName, typeof(RectTransform), typeof(ScrollRect), typeof(RectMask2D));

        RectTransform viewport = (RectTransform)host.transform;
        viewport.SetParent(parent, worldPositionStays: false);
        viewport.SetSiblingIndex(content.GetSiblingIndex());

        // Take over the slot the content occupied, so the row stays exactly where it was.
        viewport.anchorMin = content.anchorMin;
        viewport.anchorMax = content.anchorMax;
        viewport.pivot = content.pivot;
        viewport.anchoredPosition = content.anchoredPosition;
        viewport.sizeDelta = content.sizeDelta;
        viewport.localScale = content.localScale;

        content.SetParent(viewport, worldPositionStays: false);

        // The content is pinned to one edge and free to grow along the scrolling axis, and
        // stretched to the viewport across it.
        if (horizontal)
        {
            content.anchorMin = new Vector2(0f, 0f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 0.5f);
        }
        else
        {
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
        }

        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;

        ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
        if (fitter == null)
        {
            fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        }

        fitter.horizontalFit = horizontal
            ? ContentSizeFitter.FitMode.PreferredSize
            : ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = horizontal
            ? ContentSizeFitter.FitMode.Unconstrained
            : ContentSizeFitter.FitMode.PreferredSize;

        ScrollRect scroll = host.GetComponent<ScrollRect>();
        scroll.content = content;
        scroll.viewport = viewport;
        scroll.horizontal = horizontal;
        scroll.vertical = !horizontal;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        scroll.scrollSensitivity = 60f;

        AttachScrollbar(scroll, viewport, horizontal);

        logger?.Info?.Log("First Person: scenario list is now scrollable ("
                          + (horizontal ? "horizontally" : "vertically") + ").");
    }

    /// <summary>
    /// Which way the cards run. A grid constrained to a fixed row count grows sideways;
    /// any other grid grows downwards.
    /// </summary>
    private static bool IsHorizontal(LayoutGroup group)
    {
        if (group is VerticalLayoutGroup)
        {
            return false;
        }

        if (group is GridLayoutGroup grid)
        {
            return grid.constraint == GridLayoutGroup.Constraint.FixedRowCount;
        }

        // HorizontalLayoutGroup, and anything unrecognised: the scenario cards are a row.
        return true;
    }

    /// <summary>
    /// A plain two-rectangle scrollbar, built rather than loaded.
    ///
    /// The wheel and dragging both work without one -
    /// <c>GameInputManager.RaytraceUIHoverState</c> already looks for a <c>ScrollRect</c>
    /// under the pointer and hands it the wheel - but a card off the edge of the screen with
    /// nothing to say it is there is the bug being fixed, so the affordance has to be visible.
    /// No sprite: a stock <c>Image</c> with a colour needs no asset and so cannot fail to
    /// load.
    /// </summary>
    private static void AttachScrollbar(ScrollRect scroll, RectTransform viewport, bool horizontal)
    {
        const float thickness = 8f;

        GameObject barObject = new GameObject(
            "Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
        RectTransform bar = (RectTransform)barObject.transform;
        bar.SetParent(viewport, worldPositionStays: false);

        if (horizontal)
        {
            bar.anchorMin = new Vector2(0f, 0f);
            bar.anchorMax = new Vector2(1f, 0f);
            bar.pivot = new Vector2(0.5f, 0f);
            bar.sizeDelta = new Vector2(0f, thickness);
        }
        else
        {
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(1f, 0.5f);
            bar.sizeDelta = new Vector2(thickness, 0f);
        }

        bar.anchoredPosition = Vector2.zero;
        barObject.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);

        GameObject slidingArea = new GameObject("Sliding Area", typeof(RectTransform));
        RectTransform area = (RectTransform)slidingArea.transform;
        area.SetParent(bar, worldPositionStays: false);
        area.anchorMin = Vector2.zero;
        area.anchorMax = Vector2.one;
        area.sizeDelta = Vector2.zero;
        area.anchoredPosition = Vector2.zero;

        GameObject handleObject = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        RectTransform handle = (RectTransform)handleObject.transform;
        handle.SetParent(area, worldPositionStays: false);
        handle.sizeDelta = Vector2.zero;
        handleObject.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.45f);

        Scrollbar scrollbar = barObject.GetComponent<Scrollbar>();
        scrollbar.handleRect = handle;
        scrollbar.targetGraphic = handleObject.GetComponent<Image>();
        scrollbar.direction = horizontal
            ? Scrollbar.Direction.LeftToRight
            : Scrollbar.Direction.TopToBottom;

        if (horizontal)
        {
            scroll.horizontalScrollbar = scrollbar;
            scroll.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }
        else
        {
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }
    }
}
