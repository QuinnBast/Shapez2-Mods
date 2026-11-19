using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using TMPro;
using ShapezShifter.SharpDetour;
using UnityEngine;
using UnityEngine.UI;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Keeps the HUD's layer strip on screen once there are a lot of layers.
///
/// The strip is not a prefab with a fixed number of slots - <c>HUDLayersView</c> builds it
/// procedurally from <c>MaxUnlockedLayer - MinUnlockedLayer + 1</c> and rebuilds it whenever
/// research changes, so extra layers appear in it with no work. What it does not do is fit:
/// the layout is linear in the layer count, at nine pixels a layer plus a taller first one.
///
/// <code>
/// // HUDLayersView.InitializeLayersUI
/// component.anchoredPosition = new Vector2(0f, -5 - ((num == 0) ? 12 : (24 + (num - 1) * 9)));
/// // HUDLayersView.UpdateLayersState
/// int num = 32 + 10 * maxUnlockedLayer;
/// UILayerBtnLayoutElement.preferredHeight = num;
/// </code>
///
/// Three layers is 60 pixels. Fifty is over 500, which runs off a 1080p screen and off the
/// bottom of anything smaller well before that. This rescales the spacing so the whole strip
/// fits a budget, and leaves it alone entirely when it already does - so nobody playing with
/// a couple of extra layers sees any change.
///
/// Past a few dozen layers, though, fitting is not the problem - a stack of four-pixel slots is
/// unreadable however well it fits. So above <c>LayerStripNumberAbove</c> layers the handles are
/// hidden and a single label shows the current layer's number, with the top layer under it. The
/// up/down buttons are separate controls on the view and keep working unchanged. The number is
/// the layer index, the same one the floor plane ("03") and the space grid ("3") paint.
///
/// Hooked on <c>UpdateLayersState</c> rather than on <c>InitializeLayersUI</c> because the
/// former both runs at the end of the latter and re-asserts the layout height on every layer
/// change, so one hook covers the build and every subsequent update. That matters for the
/// number: <c>HUDBuildingLayersView</c> registers it on <c>Viewport.OnBuildingLayerChanged</c>,
/// and the island view on its own equivalent, so the label is rewritten on every step.
/// </summary>
internal sealed class CompactLayerStrip : IDisposable
{
    private const string LabelName = "ExtraBuildLayers.LayerNumber";
    private const float LabelHeight = 58f;

    private readonly float MaxHeight;
    private readonly int NumberAbove;
    private readonly ILogger Logger;
    private readonly Hook UpdateHook;

    /// <summary>
    /// One label per view - there are two, the building and the island strip. Weak, so a view
    /// torn down with its session takes its entry with it.
    /// </summary>
    private readonly ConditionalWeakTable<HUDLayersView, TextMeshProUGUI> Labels =
        new ConditionalWeakTable<HUDLayersView, TextMeshProUGUI>();

    private readonly List<TextMeshProUGUI> Created = new List<TextMeshProUGUI>();

    private bool Complained;
    private bool NoFont;

    public CompactLayerStrip(float maxHeight, int numberAbove, ILogger logger)
    {
        MaxHeight = Mathf.Max(60f, maxHeight);
        NumberAbove = numberAbove;
        Logger = logger;
        UpdateHook = DetourHelper.CreatePostfixHook(
            (HUDLayersView view) => view.UpdateLayersState(), Rescale);
    }

    public void Dispose()
    {
        UpdateHook?.Dispose();

        // The handles come back on the next UpdateLayersState, which sets every one active.
        foreach (TextMeshProUGUI label in Created)
        {
            if (label != null)
            {
                UnityEngine.Object.Destroy(label.gameObject);
            }
        }

        Created.Clear();
    }

    private void Rescale(HUDLayersView view)
    {
        try
        {
            List<Image> handles = view.UILayerHandles;
            if (handles == null || handles.Count <= 1)
            {
                return;
            }

            if (NumberAbove > 0 && handles.Count > NumberAbove && !NoFont && ShowNumber(view, handles))
            {
                return;
            }

            if (Labels.TryGetValue(view, out TextMeshProUGUI stale) && stale != null)
            {
                stale.gameObject.SetActive(false);
            }

            float natural = NaturalHeight(handles.Count);
            if (natural <= MaxHeight)
            {
                return;   // the game's own layout already fits; do not touch it
            }

            float scale = MaxHeight / natural;

            for (int index = 0; index < handles.Count; index++)
            {
                Image handle = handles[index];
                if (handle == null)
                {
                    continue;
                }

                // InitializeLayersUI walks num from count-1 down to 0 and appends as it goes,
                // so the handle at list position `index` was built with this `num`. Recomputing
                // it from the original formula rather than reading the current position keeps
                // this idempotent across the many calls UpdateLayersState makes.
                int num = handles.Count - 1 - index;
                float y = -5f - ((num == 0) ? 12f : (24f + (num - 1) * 9f));

                RectTransform rect = handle.rectTransform;
                rect.anchoredPosition = new Vector2(0f, y * scale);
                rect.sizeDelta = new Vector2(45f, Mathf.Max(5f, 22f * scale));
            }

            LayoutElement layout = view.UILayerBtnLayoutElement;
            if (layout != null)
            {
                layout.preferredHeight = natural * scale;
                layout.minHeight = natural * scale;
            }
        }
        catch (Exception exception)
        {
            if (!Complained)
            {
                Complained = true;
                Logger.Exception?.LogException(exception);
                Logger.Error?.Log(
                    "Extra Build Layers: could not rescale the layer strip; it will be its natural " +
                    "height and may run off screen. Set CompactLayerStrip to false to silence this.");
            }
        }
    }

    /// <summary>
    /// Replaces the handles with the current layer's number. False when no font could be found, in
    /// which case the caller falls back to rescaling - a label without a font draws nothing, and an
    /// empty strip would be worse than a cramped one.
    /// </summary>
    private bool ShowNumber(HUDLayersView view, List<Image> handles)
    {
        if (!Labels.TryGetValue(view, out TextMeshProUGUI label) || label == null)
        {
            label = CreateLabel(view);
            if (label == null)
            {
                return false;
            }

            Labels.Remove(view);
            Labels.Add(view, label);
        }

        // UpdateLayersState has just set every handle active; this runs after it, every time.
        foreach (Image handle in handles)
        {
            if (handle != null)
            {
                handle.gameObject.SetActive(false);
            }
        }

        int current = view.CurrentLayer;
        int top = view.MaxUnlockedLayer;
        label.gameObject.SetActive(true);
        label.color = view.UIActiveLayerColor;
        label.text = current + "\n<size=45%><alpha=#99>/ " + top + "</size>";

        LayoutElement layout = view.UILayerBtnLayoutElement;
        if (layout != null)
        {
            layout.preferredHeight = LabelHeight;
            layout.minHeight = LabelHeight;
        }

        return true;
    }

    private TextMeshProUGUI CreateLabel(HUDLayersView view)
    {
        TMP_FontAsset font = FindFont(view.transform);
        if (font == null)
        {
            NoFont = true;
            Logger.Error?.Log("Extra Build Layers: no font found for the layer number; keeping the layer handles.");
            return null;
        }

        GameObject holder = new GameObject(LabelName, typeof(RectTransform));
        RectTransform rect = holder.GetComponent<RectTransform>();
        rect.SetParent(view.UILayerBtnParent, false);
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -4f);
        rect.sizeDelta = new Vector2(64f, LabelHeight - 4f);

        TextMeshProUGUI label = holder.AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.fontSize = 30f;
        label.alignment = TextAlignmentOptions.Top;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.raycastTarget = false;
        label.lineSpacing = -20f;

        Created.Add(label);
        return label;
    }

    /// <summary>
    /// Borrowed from text already on screen, walking up from the strip: a mod has no font asset of
    /// its own. Same approach as the efficiency overlay's <c>FindFont</c>.
    /// </summary>
    private static TMP_FontAsset FindFont(Transform from)
    {
        for (Transform current = from; current != null; current = current.parent)
        {
            foreach (TMP_Text text in current.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.font != null)
                {
                    return text.font;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The height the game's own layout would take: the top sprite's offset, then nine pixels
    /// per layer below it, plus one sprite's height at the bottom.
    /// </summary>
    private static float NaturalHeight(int handleCount)
    {
        return 5f + 24f + (handleCount - 2) * 9f + 22f;
    }
}
