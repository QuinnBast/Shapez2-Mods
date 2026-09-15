using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Scroll wheel drives the toolbar, at whatever depth the player has stepped into.
///
/// The toolbar is a tree of arbitrary depth - categories, slots, variants, and whatever a
/// mod adds below that - and <c>HUDToolbarView</c> is already parameterised by depth:
///
/// <code>
/// private bool TryCycleChildSlots(int amount, int depth, IToolbarElement activeElement, out IToolbarElement nextSlot)
/// </code>
///
/// It walks the selection up to <c>depth</c>, then cycles the unlocked siblings there. The
/// game's own hotkeys are three fixed calls into it - `next-toolbar` at depth 1,
/// `next-toolbar-slot` at depth 2, `next-variant` at the selection's own depth - so
/// synthesising those keybindings, which is what this class used to do, could only ever
/// reach three levels.
///
/// Calling the helper directly instead means the wheel works at any depth, and a mod that
/// adds a fourth level gets it for free. Ctrl moves the cursor between levels - descending
/// into the selection when it has to, because opening a category selects nothing inside it -
/// and the plain wheel cycles at whichever level you are on.
/// </summary>
public static class FirstPersonHotbar
{
    /// <summary>
    /// Which level of the tree the plain wheel cycles. Kept between frames so stepping into
    /// a submenu stays stepped in, and clamped against the current selection's own depth
    /// every time - the tree is not uniform, so a level that exists under one category may
    /// not exist under the next.
    /// </summary>
    private static int Depth = 1;

    public static void Scroll(HUDToolbarView view, InputDownstreamContext context, FirstPersonNotifier notifier)
    {
        if (!context.HasWheelDelta())
        {
            return;
        }

        float delta = context.ConsumeWheelDelta();
        if (math.abs(delta) < 0.001f)
        {
            return;
        }

        // The stock camera would have zoomed with this, but we skip its update entirely in
        // first person, so nothing else wants the wheel.
        bool forward = delta > 0f;

        IToolbarElement selected = view.GetMostSpecificActiveElement();
        int available = selected?.TreeDepth() ?? 0;

        if (available <= 0)
        {
            return;
        }

        if (Held(KeyCode.LeftControl, KeyCode.RightControl))
        {
            Step(view, selected, available, forward, notifier);
            return;
        }

        // Clamped rather than trusted: the player may have stepped into a three-deep
        // category and then wheeled onto a shallow one, and asking to cycle deeper than the
        // selection goes simply fails.
        int depth = math.clamp(Depth, 1, available);

        if (view.TryCycleChildSlots(forward ? 1 : -1, depth, selected, out IToolbarElement next))
        {
            view.Select(next);
        }
    }

    /// <summary>
    /// Moves the depth cursor a level, descending into the selection if it is not already
    /// deep enough.
    ///
    /// That descent is the whole subtlety. <c>TryCycleChildSlots</c> refuses outright when
    /// the selection's <c>TreeDepth()</c> is shallower than the depth asked for, and opening
    /// a category does *not* select anything inside it - so a cursor that only counted up
    /// could never leave level one, and the wheel looked dead until something in a submenu
    /// had been clicked by hand. Selecting the first unlocked child is what makes the level
    /// exist to move into.
    /// </summary>
    private static void Step(
        HUDToolbarView view, IToolbarElement selected, int available, bool forward, FirstPersonNotifier notifier)
    {
        if (!forward)
        {
            // Going back up needs nothing selected or deselected: the cycle helper walks up
            // from wherever the selection is.
            if (Depth <= 1)
            {
                return;
            }

            Depth--;
            notifier?.Show("Toolbar level " + Depth);
            return;
        }

        if (available > Depth)
        {
            // The selection already reaches deeper than the cursor - usually because the
            // player clicked something - so there is a level to move onto already.
            Depth++;
            notifier?.Show("Toolbar level " + Depth);
            return;
        }

        if (!TryFirstUnlockedChild(selected, out IToolbarElement child))
        {
            // The end of this branch. Not an error, so nothing is said.
            return;
        }

        view.Select(child);
        Depth++;
        notifier?.Show("Toolbar level " + Depth);
    }

    /// <summary>
    /// The first child the player is allowed to have. Locked entries are skipped rather than
    /// selected, the same test <c>TryCycleChildSlots</c> uses when it builds its cycle list.
    /// </summary>
    private static bool TryFirstUnlockedChild(IToolbarElement element, out IToolbarElement child)
    {
        IReadOnlyList<IToolbarElement> children = element.GetChildren();

        if (children != null)
        {
            foreach (IToolbarElement candidate in children)
            {
                if (candidate.IsUnlocked())
                {
                    child = candidate;
                    return true;
                }
            }
        }

        child = null;
        return false;
    }

    /// <summary>
    /// Forgets the depth cursor, so entering first person starts on the categories rather
    /// than wherever the last visit left off.
    /// </summary>
    public static void Reset()
    {
        Depth = 1;
    }

    /// <summary>
    /// Either of a modifier pair. A hand already on the mouse reaches for whichever is
    /// nearer, and checking only the left one made the scope change look like it did
    /// nothing at all.
    /// </summary>
    private static bool Held(KeyCode left, KeyCode right)
    {
        return Input.GetKey(left) || Input.GetKey(right);
    }
}
