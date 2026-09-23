using System.Collections.Generic;
using Core;
using Unity.Mathematics;
using UnityEngine;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// The scroll wheel walks the whole toolbar as one flat list, changing view as it goes.
///
/// One notch is one item, wherever that item lives: belt, rotator, cutter, stacker, miner,
/// pin pusher, label, trash, straight on into the fluids category, and eventually out of the
/// machine view entirely and into space platforms and trade stations. A hotbar is a flat
/// thing to a player even though it is a tree underneath, and the wheel should agree with
/// the player rather than with the data structure.
///
/// The order is not invented. <c>ToolbarQuery.GetElementsInTopDownOrder</c> is a breadth-first
/// walk from the root, so everything at depth 2 comes out grouped by its category and in
/// category order - exactly the reading order of the toolbar on screen.
///
/// <b>Depth 3 is deliberately not in it.</b> Those are variants - the belt's corners and
/// lifts - and paging through nine belt variants on the way from the belt to the rotator is
/// not what anyone wants from a wheel. Variants have their own controls: the game's own
/// <c>toolbar.next-variant</c> on Tab, and Shift with the wheel here.
/// </summary>
public static class FirstPersonHotbar
{
    /// <summary>
    /// The flat list, rebuilt per scroll rather than cached.
    ///
    /// It has to be: research unlocks entries mid-session, and a stale list would scroll onto
    /// something that is no longer there. Rebuilding is a breadth-first walk of a tree with a
    /// few hundred nodes, once per wheel notch - which is nothing, and a cache with an
    /// invalidation rule would be the more expensive thing to own.
    /// </summary>
    private static readonly List<IToolbarElement> Items = new List<IToolbarElement>();

    /// <summary>
    /// Which view each top-level category belongs to, or null for one that belongs to
    /// neither and so can be selected from either. Filled by the same walk that fills
    /// <see cref="Items"/> - breadth-first order puts every category before any of its
    /// items, so one pass does both.
    /// </summary>
    private static readonly Dictionary<IToolbarElement, PlayerInteractionBaseState?> CategoryScope =
        new Dictionary<IToolbarElement, PlayerInteractionBaseState?>();

    /// <summary>
    /// The first index in <see cref="Items"/> holding each distinct placement, keyed by its
    /// <c>PlacementInitiator</c>.
    ///
    /// The toolbar offers the same tool from several categories - Trash appears in Belts,
    /// Fluids, Logic and Sandbox; Space Belts in Platforms, Trains and Converters - and those
    /// are **different elements sharing one initiator**. Selecting any of them activates
    /// whichever the game treats as canonical, so asking for `4/0` and being handed `6/0` is
    /// not a failure: it is the same tool answering to a different name.
    ///
    /// Every copy stays in the list. Dropping the duplicates fixed the stepping and was the
    /// wrong trade - a player scrolling through the trains category and finding the space
    /// belt simply absent, while looking straight at it on screen, is worse than any jump.
    /// This map is only a fallback, for finding the player's position when they have clicked
    /// a copy the wheel was not tracking.
    /// </summary>
    private static readonly Dictionary<object, int> IndexByKey = new Dictionary<object, int>();

    /// <summary>
    /// The index this class last selected, and the tool that was there.
    ///
    /// The cursor has to be remembered rather than re-derived every notch, precisely because
    /// the game may answer a selection with a different copy of the same tool. Re-deriving
    /// would read that copy's position - halfway across the toolbar - and step from there,
    /// which is the jump. Holding our own position keeps the walk in order, and the key is
    /// what proves the position is still ours to hold: if the selection no longer matches the
    /// tool we put there, the player moved it themselves and the cursor defers to them.
    /// </summary>
    private static int LastIndex = -1;

    private static object LastKey;

    private static readonly List<IToolbarElement> Scratch = new List<IToolbarElement>();

    /// <summary>
    /// How many items the last dumped list had, so the full ordered dump goes out once per
    /// composition rather than once per notch. Research unlocking something changes the count
    /// and earns a fresh dump.
    /// </summary>
    private static int LoggedCount = -1;

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
        int step = delta > 0f ? 1 : -1;

        if (Held(FirstPersonTuning.ToolbarVariantModifier, FirstPersonTuning.ToolbarVariantModifierAlt))
        {
            CycleVariant(view, step);
            return;
        }

        CycleItems(view, step);
    }

    /// <summary>
    /// One notch, one item, across every category and both views.
    /// </summary>
    private static void CycleItems(HUDToolbarView view, int step)
    {
        if (!Rebuild(view))
        {
            return;
        }

        IToolbarElement selected = view.GetMostSpecificActiveElement();
        int depth = selected?.TreeDepth() ?? 0;

        if (depth == 1)
        {
            // A category is open with nothing chosen inside it - which is what clicking a tab
            // leaves behind. Land on that category's own first or last item rather than
            // jumping to the end of the whole list, so the wheel continues from where the
            // player just clicked.
            if (TryEdgeOfCategory(selected, step > 0, out int edge))
            {
                Log(view, step, selected, -1, Items[edge], edge, "category-edge");
                Select(view, Items[edge]);
                Remember(edge);
                LogLanded(view);
            }

            return;
        }

        int index = -1;

        if (depth >= 2)
        {
            // A variant stands in for its item, so scrolling away from the belt's corner
            // piece lands on the rotator rather than on nothing.
            index = Anchor(selected.GetAncestorAtDepth(2));
        }

        // Nothing selected - a fresh session, or the player deselected - so a notch starts at
        // whichever end they scrolled towards rather than refusing.
        int from = index;
        index = index < 0
            ? (step > 0 ? 0 : Items.Count - 1)
            : FastMath.SafeMod(index + step, Items.Count);

        Log(view, step, selected, from, Items[index], index, from < 0 ? "no-anchor" : "step");
        Select(view, Items[index]);
        Remember(index);
        LogLanded(view);
    }

    /// <summary>
    /// Where the walk continues from, given whatever the game currently has selected.
    ///
    /// Three answers, in order of authority:
    ///
    /// <list type="number">
    /// <item><b>Our own cursor</b>, when the selection is still the tool we put there.
    /// Whichever copy of it the game chose to highlight, the player has not moved - so
    /// neither do we. This is what stops a duplicate throwing the walk across the toolbar.
    /// </item>
    /// <item><b>The exact element</b>, when the player clicked one. That is them saying where
    /// they are, and it wins over anything we remember.</item>
    /// <item><b>The first copy of that tool</b>, when they clicked a copy in a category the
    /// walk has not reached. Better than starting over.</item>
    /// </list>
    /// </summary>
    private static int Anchor(IToolbarElement item)
    {
        object key = KeyOf(item) ?? item;

        if (LastIndex >= 0 && LastIndex < Items.Count && LastKey != null
            && Equals(key, LastKey)
            && Equals(KeyOf(Items[LastIndex]) ?? Items[LastIndex], LastKey))
        {
            return LastIndex;
        }

        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i] == item)
            {
                return i;
            }
        }

        return IndexByKey.TryGetValue(key, out int first) ? first : -1;
    }

    private static void Remember(int index)
    {
        LastIndex = index;
        LastKey = index >= 0 && index < Items.Count
            ? KeyOf(Items[index]) ?? Items[index]
            : null;
    }

    // ---- Diagnostics ----------------------------------------------------------------
    //
    // On while the wheel is still being worked out; see FirstPersonControl.LogToolbar. The
    // toolbar answers a selection with side effects the calling code cannot see - a category
    // callback that moves the interaction state, which force-selects something else - so the
    // only honest way to know what happened is to ask afterwards and write it down.

    private static void Log(
        HUDToolbarView view, int step, IToolbarElement selected, int from,
        IToolbarElement target, int to, string why)
    {
        if (!FirstPersonControl.LogToolbar)
        {
            return;
        }

        DumpList();

        DebugLogger.Info?.Log(
            "FP hotbar: " + (step > 0 ? "+1" : "-1") + " " + why
            + " | scope " + view.Player.InteractionState.BaseState
            + " | from [" + from + "] " + Describe(selected)
            + " | to [" + to + "] " + Describe(target));
    }

    /// <summary>
    /// What the selection actually is once the game has had its say. A line where this
    /// disagrees with the `to` above is the bug, and it names whatever took the selection.
    /// </summary>
    private static void LogLanded(HUDToolbarView view)
    {
        if (!FirstPersonControl.LogToolbar)
        {
            return;
        }

        DebugLogger.Info?.Log(
            "FP hotbar:    landed on " + Describe(view.GetMostSpecificActiveElement())
            + " | scope " + view.Player.InteractionState.BaseState);
    }

    /// <summary>
    /// The whole ordered list, once per composition. Bounded and worth having: if the order
    /// itself is wrong, no amount of per-notch logging will show it.
    /// </summary>
    private static void DumpList()
    {
        if (LoggedCount == Items.Count)
        {
            return;
        }

        LoggedCount = Items.Count;
        DebugLogger.Info?.Log("FP hotbar: list of " + Items.Count + " items:");

        for (int i = 0; i < Items.Count; i++)
        {
            DebugLogger.Info?.Log("FP hotbar:   [" + i + "] " + Describe(Items[i]));
        }
    }

    /// <summary>
    /// An element as a child-index path plus whatever title it carries - `2/5 Rotator`.
    ///
    /// The path is the reliable half: <c>IToolbarElement</c> has no id, and a title is an
    /// <c>IText</c> whose <c>ToString</c> is a formatter's business rather than a contract,
    /// so it is read defensively and dropped if it throws or says nothing.
    /// </summary>
    private static string Describe(IToolbarElement element)
    {
        if (element == null)
        {
            return "<none>";
        }

        string path = "";
        IToolbarElement walk = element;

        while (walk.TryGetParent(out IParentToolbarElement parent))
        {
            path = "/" + parent.GetChildren().IndexOf(walk, EqualityComparer<IToolbarElement>.Default) + path;
            walk = parent;
        }

        path = path.Length > 0 ? path.Substring(1) : "root";

        string title = null;

        if (element is IPresentableToolbarElement presentable)
        {
            try
            {
                title = presentable.PresentationData.Title?.ToString();
            }
            catch
            {
                title = null;
            }
        }

        return string.IsNullOrEmpty(title)
            ? path + " (" + element.GetType().Name + ")"
            : path + " " + title;
    }

    /// <summary>
    /// Selects an item, taking the player to the view that item belongs to first.
    ///
    /// **The order is the whole fix, and it is not obvious.**
    /// <c>ToolbarScopeSynchronizer</c> registers an <c>OnSelect</c> callback on every
    /// top-level category:
    ///
    /// <code>
    /// element.OnSelect.Register(() => OnIslandCategorySelected(element));
    /// //   -> PlayerInteraction.TryMoveIntoBaseState(Islands)
    /// //   -> OnPlayerScopeChange -> LastIslandCategory?.Select();
    /// </code>
    ///
    /// So selecting an out-of-view item moves the view as a *side effect*, and the view
    /// change then force-selects that view's **remembered** category rather than the thing
    /// that was asked for. Select first and the selection is immediately overwritten; the
    /// wheel then reads its next position out of the place it was snapped to, and orbits a
    /// handful of entries forever. That is what "the scrolling gets stuck" was.
    ///
    /// Moving the view *first* puts the clobber before the selection instead of after it.
    /// By the time <c>Select</c> runs, <c>BaseState</c> already matches, so the category's
    /// callback calls <c>TryMoveIntoBaseState</c> with the state it is already in - which
    /// returns false at its first line without firing <c>OnStateChanged</c> - and nothing
    /// touches the selection again.
    ///
    /// A category with no view of its own, such as blueprints, moves nothing.
    ///
    /// **Then the category is selected before the item**, which is what puts the highlight on
    /// the copy the player scrolled to rather than on a copy of the same tool somewhere else.
    ///
    /// A space belt is offered from Platforms, Trains and Converters, and those are three
    /// `PlacementToolbarElement`s sharing one `PlacementInitiator`. Only one of them is ever
    /// active, and which one is decided by
    /// `PrioritizeToolbarElementFromCurrentCategorySelector`:
    ///
    /// <code>
    /// var active = Root.GetChildren().FilterCast&lt;CategoryGroupToolbarElement&gt;()
    ///                  .Single(x =&gt; x.IsSelfActive);
    /// foreach (var item in values) if (item.IsAncestor(active)) return item;
    /// // …and the whole thing is wrapped in try/catch, falling through to:
    /// return values[0];
    /// </code>
    ///
    /// `Single` throws when **no** category is self-active or when **two** are, and the catch
    /// then hands back `values[0]` - the first registered copy, which is the one in the
    /// earliest category. That is the "jumping to the trade platforms space belt" exactly: on
    /// the notch that enters a new category, the old one has gone and the new one has not
    /// arrived, so the count is not one and the first copy wins.
    ///
    /// `CategoryGroupToolbarElement.Select()` is what sets `IsSelfActive`, and
    /// `HUDToolbarView.Select` on a depth-1 element always reaches it. So selecting the
    /// category first makes the count exactly one - ours - and the selector then finds the
    /// copy underneath it.
    /// </summary>
    private static void Select(HUDToolbarView view, IToolbarElement item)
    {
        IToolbarElement category = item.GetAncestorAtDepth(1);

        if (CategoryScope.TryGetValue(category, out PlayerInteractionBaseState? scope) && scope.HasValue)
        {
            view.Player.InteractionState.TryMoveIntoBaseState(scope.Value);
        }

        // Select the category first, then the item. This is what decides *which copy* of a
        // shared tool the game shows, and it is not a nicety - see the summary above.
        view.Select(category);
        view.Select(item);
    }

    /// <summary>
    /// The first or last item belonging to one category, as an index into
    /// <see cref="Items"/>.
    /// </summary>
    private static bool TryEdgeOfCategory(IToolbarElement category, bool first, out int index)
    {
        index = -1;

        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].GetAncestorAtDepth(1) != category)
            {
                continue;
            }

            index = i;

            if (first)
            {
                return true;
            }
        }

        return index >= 0;
    }

    /// <summary>
    /// Shift and the wheel, for the variants of whatever is held.
    ///
    /// This is <c>toolbar.next-variant</c> by another route. The game binds that to Tab and
    /// it works normally - first person no longer takes Tab for anything - but a player whose
    /// hand is on the mouse picking a building should not have to reach for the keyboard to
    /// pick which *kind* of it, so the wheel offers the same thing one modifier away.
    ///
    /// Cycles at the selection's own depth, which is what the game's handler does, so it
    /// reaches a fourth level if a mod adds one. No view change is possible here: a variant
    /// shares its category with the item it belongs to.
    /// </summary>
    private static void CycleVariant(HUDToolbarView view, int step)
    {
        IToolbarElement selected = view.GetMostSpecificActiveElement();
        int depth = selected?.TreeDepth() ?? 0;

        if (depth <= 2)
        {
            // Nothing held, or something with no variants. Silent: a wheel notch that finds
            // nothing to do is not worth a message sixty times a second.
            return;
        }

        if (view.TryCycleChildSlots(step, depth, selected, out IToolbarElement next))
        {
            view.Select(next);
        }
    }

    /// <summary>
    /// Fills <see cref="Items"/> with every unlocked element at depth 2 in the toolbar's own
    /// order, and <see cref="CategoryScope"/> with the view each category belongs to. False
    /// when there is nothing to scroll through at all.
    /// </summary>
    private static bool Rebuild(HUDToolbarView view)
    {
        Items.Clear();
        CategoryScope.Clear();
        IndexByKey.Clear();

        IToolbar toolbar = view.Toolbar;
        if (toolbar == null)
        {
            return false;
        }

        Scratch.Clear();
        toolbar.Query.GetElementsInTopDownOrder(Scratch);

        foreach (IToolbarElement element in Scratch)
        {
            int depth = element.TreeDepth();

            if (depth == 1)
            {
                CategoryScope[element] = ClassifyCategory(element);
                continue;
            }

            // Locked entries are skipped rather than selected, the same test
            // `TryCycleChildSlots` applies when it builds its own cycle list - otherwise the
            // wheel stops on things the player cannot place and looks broken.
            if (depth != 2 || !element.IsUnlocked())
            {
                continue;
            }

            // Every copy is kept - the player can see them all on screen, so the wheel stops
            // on them all. The map records only the first, as a fallback for finding a
            // position when the player has clicked a copy the walk was not tracking.
            object key = KeyOf(element) ?? element;

            if (!IndexByKey.ContainsKey(key))
            {
                IndexByKey[key] = Items.Count;
            }

            Items.Add(element);
        }

        Scratch.Clear();
        return Items.Count > 0;
    }

    /// <summary>
    /// What makes two toolbar entries the same tool: the placement they start.
    ///
    /// Taken from the element itself when it is a placement, and from its first leaf when it
    /// is a group - a group's identity is the thing it places. Null for anything that places
    /// nothing, such as a blueprint folder, and the caller falls back to the element itself
    /// so those stay distinct.
    /// </summary>
    private static object KeyOf(IToolbarElement element)
    {
        if (element is IPlacementToolbarElement placement)
        {
            return placement.PlacementInitiator;
        }

        IReadOnlyList<IToolbarElement> children = element.GetChildren();

        if (children != null)
        {
            foreach (IToolbarElement child in children)
            {
                object key = KeyOf(child);

                if (key != null)
                {
                    return key;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Which view a top-level category belongs to, or null for one that belongs to neither.
    ///
    /// This repeats <c>ToolbarScopeSynchronizer.TryClassifyCategoryPlacementType</c> rather
    /// than calling it, because that method belongs to an object the mod has no handle on -
    /// the synchronizer is constructed and kept by the session, and is not exposed anywhere.
    /// The rule is short enough to restate: a category's type is the one type shared by every
    /// leaf under it, and a category whose leaves are not all placements, or do not agree, has
    /// no type.
    ///
    /// Null matters as much as the two values. Those are exactly the categories the
    /// synchronizer skips when it registers its callbacks, so selecting one moves no view -
    /// and moving the view *for* one would be this mod inventing a rule the game does not
    /// have.
    /// </summary>
    private static PlayerInteractionBaseState? ClassifyCategory(IToolbarElement category)
    {
        EntityType? type = null;

        if (!TryClassifyLeaves(category, ref type) || !type.HasValue)
        {
            return null;
        }

        return type.Value == EntityType.Island
            ? PlayerInteractionBaseState.Islands
            : PlayerInteractionBaseState.Buildings;
    }

    /// <summary>
    /// Walks one category's leaves, returning false the moment they disagree or one of them
    /// is not a placement at all. Recursive rather than a queue: the toolbar is three or four
    /// deep, and this way it needs no scratch collection of its own.
    /// </summary>
    private static bool TryClassifyLeaves(IToolbarElement element, ref EntityType? type)
    {
        IReadOnlyList<IToolbarElement> children = element.GetChildren();

        if (children == null || children.Count == 0)
        {
            if (!(element is IPlacementToolbarElement placement))
            {
                return false;
            }

            if (type.HasValue && type.Value != placement.EntityType)
            {
                return false;
            }

            type = placement.EntityType;
            return true;
        }

        foreach (IToolbarElement child in children)
        {
            if (!TryClassifyLeaves(child, ref type))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Drops the cached lists. Nothing here survives a scroll, so this is housekeeping rather
    /// than state - the camera calls it on entry.
    /// </summary>
    public static void Reset()
    {
        Items.Clear();
        Scratch.Clear();
        CategoryScope.Clear();
        IndexByKey.Clear();
        LastIndex = -1;
        LastKey = null;
        LoggedCount = -1;
    }

    /// <summary>
    /// Either of a modifier pair. A hand already on the mouse reaches for whichever is
    /// nearer, and checking only the left one made the modifier look like it did nothing.
    /// </summary>
    private static bool Held(KeyCode left, KeyCode right)
    {
        return Input.GetKey(left) || Input.GetKey(right);
    }
}
