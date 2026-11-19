using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.UI;
using ILogger = Core.Logging.ILogger;
using Object = UnityEngine.Object;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Wraps a side panel's action buttons onto two rows when there are too many for one.
///
/// <c>HUDSidePanelModuleActionButtons.InitFromData</c> puts one button per action into
/// <c>UIButtonsTransform</c>, which lays them out in a single row. Vanilla's selection panels have
/// up to six and fit; the View action (<see cref="ViewSelectionAction"/>) makes seven and the last
/// one hangs off the panel's edge.
///
/// A postfix swaps the row's layout for a <c>GridLayoutGroup</c> with <c>ceil(count / 2)</c>
/// columns - seven become four on top and three below - using the buttons' own size and the row's
/// own spacing, padding and alignment, so the buttons look exactly as before, just wrapped. A panel
/// with <see cref="MaxPerRow"/> or fewer is set back to one row. The module views are pooled and
/// reused, which is why the grid is configured every time rather than once.
/// </summary>
internal sealed class ActionButtonRows : IDisposable
{
    /// <summary>What fits across the panel: vanilla's own maximum.</summary>
    private const int MaxPerRow = 6;

    private delegate void Init(HUDSidePanelModuleActionButtons self, IHUDSidePanelModuleData data);

    private delegate void InitHook(Init orig, HUDSidePanelModuleActionButtons self, IHUDSidePanelModuleData data);

    private readonly ILogger Logger;
    private readonly Hook Hook;
    private bool Complained;

    public ActionButtonRows(ILogger logger)
    {
        Logger = logger;

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            MethodInfo target = typeof(HUDSidePanelModuleActionButtons).GetMethod(
                nameof(HUDSidePanelModuleActionButtons.InitFromData), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (target != null)
            {
                Hook = new Hook(target, new InitHook(Detour));
            }
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
        }
    }

    private void Detour(Init orig, HUDSidePanelModuleActionButtons self, IHUDSidePanelModuleData data)
    {
        orig(self, data);

        try
        {
            Arrange(self.UIButtonsTransform);
        }
        catch (Exception exception)
        {
            if (!Complained)
            {
                Complained = true;
                Logger.Exception?.LogException(exception);
            }
        }
    }

    private static void Arrange(RectTransform row)
    {
        if (row == null)
        {
            return;
        }

        int count = 0;
        RectTransform first = null;
        foreach (Transform child in row)
        {
            if (child.gameObject.activeSelf)
            {
                count++;
                first ??= child as RectTransform;
            }
        }

        GridLayoutGroup grid = row.GetComponent<GridLayoutGroup>();
        if (grid == null)
        {
            if (count <= MaxPerRow || first == null)
            {
                return;
            }

            HorizontalOrVerticalLayoutGroup line = row.GetComponent<HorizontalOrVerticalLayoutGroup>();
            float spacing = line != null ? line.spacing : 4f;
            RectOffset padding = line != null ? line.padding : new RectOffset();
            TextAnchor alignment = line != null ? line.childAlignment : TextAnchor.MiddleCenter;
            // The button's preferred size if it states one, otherwise the size it is drawn at.
            float width = LayoutUtility.GetPreferredWidth(first);
            float height = LayoutUtility.GetPreferredHeight(first);
            Vector2 cell = new Vector2(
                width > 1f ? width : Math.Max(1f, first.rect.width),
                height > 1f ? height : Math.Max(1f, first.rect.height));
            if (line != null)
            {
                // Immediate: a row can carry only one layout group, and AddComponent below would
                // otherwise fail while this one waits for end of frame.
                Object.DestroyImmediate(line);
            }

            grid = row.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = cell;
            grid.spacing = new Vector2(spacing, spacing);
            grid.padding = padding;
            grid.childAlignment = alignment;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        }

        grid.constraintCount = count <= MaxPerRow ? Math.Max(1, count) : (count + 1) / 2;
        LayoutRebuilder.MarkLayoutForRebuild(row);
    }

    public void Dispose()
    {
        Hook?.Dispose();
    }
}
