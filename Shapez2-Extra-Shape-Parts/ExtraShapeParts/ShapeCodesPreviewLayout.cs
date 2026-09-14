using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.UI;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// Makes the shape viewer's "Parts" and "Colors" legend wrap instead of running off the side.
    ///
    /// `HUDShapeCodesPreview.Construct` instantiates one entry per entry in
    /// `ShapesConfiguration.Parts` into `UIPartsParent`, and one per colour into `UIColorsParent`.
    /// The parents lay their children out in a single row, which is fine for the eight parts the
    /// quad configuration ships and is not fine for the eighteen it has with this mod installed:
    /// the extra entries run off to the right, over the controls beside them.
    ///
    /// This is squarely the mod's mess to clean up. Registering ten new parts is the whole point,
    /// and there is no way to be in `Parts` - which is what makes a shape code parse - without
    /// being in that legend.
    ///
    /// The fix is to swap the single-row layout for a `GridLayoutGroup` with a flexible constraint,
    /// which wraps to however many columns the panel is wide, and a `ContentSizeFitter` so the
    /// panel grows a row instead of clipping one.
    internal static class ShapeCodesPreviewLayout
    {
        private delegate void ConstructDelegate(HUDShapeCodesPreview self, GameMode mode,
            IShapeIdManager shapeIdManager);

        /// A postfix, because the entries have to exist before they can be re-laid out.
        /// `DetourHelper` only offers prefixes, so this is a plain MonoMod hook.
        ///
        /// `HUDShapeCodesPreview` is not generic, which is the thing worth checking before hooking
        /// anything - MonoMod cannot hook a method on a generic type and fails at startup rather
        /// than at the call site.
        public static Hook Create(ILogger logger)
        {
            MethodInfo target = typeof(HUDShapeCodesPreview).GetMethod("Construct",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (target == null)
            {
                logger.Warning?.Log(
                    "HUDShapeCodesPreview.Construct not found; the shape code legend will overflow.");
                return null;
            }

            return new Hook(target,
                (Action<ConstructDelegate, HUDShapeCodesPreview, GameMode, IShapeIdManager>)
                ((original, self, mode, shapeIdManager) =>
                {
                    original(self, mode, shapeIdManager);

                    try
                    {
                        Reflow(self.UIPartsParent, "parts", logger);
                        Reflow(self.UIColorsParent, "colors", logger);
                    }
                    catch (Exception exception)
                    {
                        // A legend that overflows is a blemish; a HUD component that throws while
                        // building takes the dialog with it.
                        logger.Error?.Log("Could not re-lay out the shape code legend: " + exception);
                    }
                }));
        }

        private static void Reflow(RectTransform parent, string which, ILogger logger)
        {
            if (parent == null || parent.childCount == 0)
            {
                return;
            }

            if (parent.GetComponent<GridLayoutGroup>() != null)
            {
                // Already a grid - either vanilla changed, or this ran twice. Either way a grid
                // already wraps, so there is nothing to do and nothing to log about.
                return;
            }

            LayoutGroup existing = parent.GetComponent<LayoutGroup>();

            // The entries were instantiated a moment ago and no layout pass has run, so their rects
            // are whatever the prefab last left behind. Force one with the layout that is still
            // attached, or the cell size measured below is meaningless.
            LayoutRebuilder.ForceRebuildLayoutImmediate(parent);

            RectTransform first = parent.GetChild(0) as RectTransform;
            Vector2 cell = MeasureCell(first);

            logger.Info?.Log(
                $"Shape code legend '{which}': {parent.childCount} entries, " +
                $"layout={existing?.GetType().Name ?? "none"}, cell={cell.x:0.#}x{cell.y:0.#}, " +
                $"panel width={parent.rect.width:0.#}");

            if (cell.x <= 1.0f || cell.y <= 1.0f)
            {
                // Zero-sized cells would collapse every entry. Leaving the overflow alone is the
                // better failure: it is ugly, and it is not invisible.
                logger.Warning?.Log(
                    $"Shape code legend '{which}': could not measure an entry, leaving the layout alone.");
                return;
            }

            float spacing = existing is HorizontalOrVerticalLayoutGroup line ? line.spacing : 4.0f;
            RectOffset padding = existing != null ? existing.padding : new RectOffset();

            if (existing != null)
            {
                // It has to be destroyed, and destroyed *immediately*.
                //
                // Unity refuses to add a second LayoutGroup to a GameObject at all - "Can't add
                // 'GridLayoutGroup' to Parts because a 'HorizontalLayoutGroup' is already added" -
                // and AddComponent then returns null rather than throwing, so the next line is what
                // blows up. Disabling the old one is not enough: the refusal is about the component
                // being present, not about it being enabled. Plain Destroy is deferred to the end of
                // the frame, which is far too late for an AddComponent on the next line.
                UnityEngine.Object.DestroyImmediate(existing);
            }

            GridLayoutGroup grid = parent.gameObject.AddComponent<GridLayoutGroup>();
            if (grid == null)
            {
                logger.Warning?.Log(
                    $"Shape code legend '{which}': could not add a GridLayoutGroup, leaving the " +
                    "layout alone.");
                return;
            }

            grid.cellSize = cell;
            grid.spacing = new Vector2(spacing, spacing);
            grid.padding = padding;

            // childAlignment is left at LayoutGroup's own default, which is already UpperLeft.
            // Setting it explicitly would drag in UnityEngine.TextRenderingModule for the
            // TextAnchor enum, which is a reference to carry for no behaviour.

            // Flexible is the one constraint that reads the panel's own width, so the column count
            // follows the UI scale and the window size rather than a number guessed here.
            grid.constraint = GridLayoutGroup.Constraint.Flexible;

            ContentSizeFitter fitter = parent.GetComponent<ContentSizeFitter>();
            if (fitter == null)
            {
                fitter = parent.gameObject.AddComponent<ContentSizeFitter>();
            }

            // Height only, and horizontal explicitly unconstrained. A horizontal fitter would
            // defeat the wrap entirely: the row would grow sideways to hold every entry again, and
            // a flexible grid asked to lay out inside an infinitely wide parent produces one row.
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            LayoutRebuilder.ForceRebuildLayoutImmediate(parent);

            // The width is what decides the column count, and it was 0 before the rebuild because
            // no layout pass had run. Log what it settled at: if the wrap still comes out wrong,
            // this is the number that says whether the panel has a width to wrap against at all.
            logger.Info?.Log(
                $"Shape code legend '{which}': wrapped, panel width now {parent.rect.width:0.#}, " +
                $"about {Mathf.Max(1, Mathf.FloorToInt(parent.rect.width / (cell.x + spacing)))} columns.");
        }

        /// An entry's size, preferring what the layout system would use over what the transform
        /// happens to say.
        private static Vector2 MeasureCell(RectTransform child)
        {
            if (child == null)
            {
                return Vector2.zero;
            }

            LayoutElement element = child.GetComponent<LayoutElement>();

            float width = element != null && element.preferredWidth > 0.0f
                ? element.preferredWidth
                : child.rect.width;
            float height = element != null && element.preferredHeight > 0.0f
                ? element.preferredHeight
                : child.rect.height;

            if (width <= 1.0f)
            {
                width = child.sizeDelta.x;
            }

            if (height <= 1.0f)
            {
                height = child.sizeDelta.y;
            }

            return new Vector2(width, height);
        }
    }
}
