using ShapezShifter.Flow.Toolbar;
using UnityEngine;

namespace QuinnBast.Shapez2.ToolbarKit
{
    /// Toolbar placements that say where they mean.
    ///
    /// Drop-in replacements for `ToolbarElementLocator.Root().ChildAt(..).InsertAfter()` at any
    /// `.InToolbar(..)` call. The trick is that IToolbarEntryInsertLocation.AddEntry is handed
    /// the whole live ToolbarData, so an implementation is free to search it, extend it, or
    /// both - none of which the index-only IToolbarElementLocator can express.
    ///
    /// <code>
    /// ToolbarKit.Log = logger;                                  // once, in the mod ctor
    ///
    /// .InToolbar(ToolbarSlot.InGroup("toolbar.category.belts"))                  // existing
    /// .InToolbar(ToolbarSlot.InNewCategory("mymod.toolbar.title", icon))         // new
    /// .InToolbar(ToolbarSlot.InNewGroup("toolbar.category.belts", "x", icon))    // nested
    /// </code>
    public static class ToolbarSlot
    {
        /// Append to an existing group or category, found anywhere in the tree by the
        /// translation id of its title.
        ///
        /// If nothing matches, the entry is appended at the top level and the full tree is
        /// logged so the right id can be read off. See MissingGroupPolicy below for why that
        /// rather than throwing.
        public static IToolbarEntryInsertLocation InGroup(string titleTranslationId)
        {
            return new InExistingGroup(titleTranslationId);
        }

        /// Into one of the game's own categories.
        ///
        /// Prefer this over the string overload wherever it applies. The ids are easy to get
        /// subtly wrong by hand - every one ends in `.title`, and `converters` is lowercase
        /// where its neighbours are not - and a wrong id is not a compile error, it is a
        /// runtime miss.
        public static IToolbarEntryInsertLocation InGroup(ToolbarCategory category)
        {
            return new InExistingGroup(category.ToTranslationId());
        }

        /// Into one of the game's own named groups.
        ///
        /// Some group ids repeat across categories - BeltBuilding exists under four of them -
        /// and this takes the shallowest-leftmost. Where that is not the one you want, query a
        /// ToolbarNode snapshot instead.
        public static IToolbarEntryInsertLocation InGroup(ToolbarGroup group)
        {
            return new InExistingGroup(group.ToTranslationId());
        }

        /// Append to a new top-level category, created the first time it is needed.
        ///
        /// Idempotent: five islands asking for the same category get one category. So do two
        /// separate mods asking for the same id, since the match is against the live tree
        /// rather than anything either mod remembers.
        ///
        /// The icon is required rather than optional. ToolbarBuilder wraps it in a
        /// ToolbarSlotSpriteIcon unconditionally, and a category with no icon is a hole in the
        /// build menu.
        public static IToolbarEntryInsertLocation InNewCategory(
            string titleTranslationId, Sprite icon, string descriptionTranslationId = null)
        {
            return new InCreatedCategory(titleTranslationId, icon, descriptionTranslationId);
        }

        /// Append to a new group nested inside an existing group or category.
        ///
        /// The parent is resolved the same way InGroup resolves, with the same fallback.
        public static IToolbarEntryInsertLocation InNewGroup(
            string parentTitleTranslationId, string titleTranslationId, Sprite icon,
            string descriptionTranslationId = null)
        {
            return new InCreatedGroup(
                parentTitleTranslationId, titleTranslationId, icon, descriptionTranslationId);
        }

        /// A new group of your own, nested inside one of the game's categories.
        public static IToolbarEntryInsertLocation InNewGroup(
            ToolbarCategory parent, string titleTranslationId, Sprite icon,
            string descriptionTranslationId = null)
        {
            return new InCreatedGroup(
                parent.ToTranslationId(), titleTranslationId, icon, descriptionTranslationId);
        }

        /// Register the island, but put nothing in the build menu.
        ///
        /// Shifter's builder chain makes `.InToolbar(..)` mandatory - IDefinedPlaceableIslandExtender
        /// offers no way past it - so an island that should exist as a *definition* without
        /// being individually placeable has nowhere to go. This is that way past it: a location
        /// that is asked to add the entry and declines.
        ///
        /// The case that needs it is a draggable path. The turn pieces of a path family have to
        /// be registered so the placer's definition finder can choose them, but a player never
        /// picks a corner by hand - dragging picks it for them - and three toolbar entries for
        /// one belt is clutter.
        public static IToolbarEntryInsertLocation Hidden()
        {
            return new NotInToolbar();
        }

        /// What a missed lookup does, and why it is not an exception.
        ///
        /// The two failure modes this kit exists to remove are a silent wrong placement and a
        /// hard abort. Shifter's index paths manage both: a stale index quietly puts the entry
        /// somewhere arbitrary, and a too-short tree throws ToolbarQueryException during mod
        /// load, which takes the whole game's startup down with it.
        ///
        /// So a miss here does neither. The entry goes to the top level, where it is
        /// impossible not to notice, and the tree is logged with every id in it, which is
        /// exactly the information needed to fix the call. The mod still loads and is still
        /// testable.
        private static void ReportMiss(ToolbarData toolbarData, string translationId)
        {
            ToolbarKit.Log?.Error?.Log(
                $"Toolbar: no group with title id '{translationId}'. Appending at the top level " +
                $"instead. Pick an id from below.\n{ToolbarTree.Describe(toolbarData)}");
        }

        private sealed class NotInToolbar : IToolbarEntryInsertLocation
        {
            public void AddEntry(ToolbarData toolbarData, IToolbarElementData elementData)
            {
            }

            public override string ToString()
            {
                return "not in the toolbar";
            }
        }

        private sealed class InExistingGroup : IToolbarEntryInsertLocation
        {
            private readonly string TitleId;

            public InExistingGroup(string titleId)
            {
                TitleId = titleId;
            }

            public void AddEntry(ToolbarData toolbarData, IToolbarElementData elementData)
            {
                ParentToolbarElementData group = ToolbarTree.FindGroup(toolbarData, TitleId);

                if (group == null)
                {
                    ReportMiss(toolbarData, TitleId);
                    ToolbarTree.Append(toolbarData.RootToolbarElement, elementData);
                    return;
                }

                ToolbarTree.Append(group, elementData);
            }

            public override string ToString()
            {
                return $"in group \"{TitleId}\"";
            }
        }

        private sealed class InCreatedCategory : IToolbarEntryInsertLocation
        {
            private readonly string TitleId;
            private readonly string DescriptionId;
            private readonly Sprite Icon;

            public InCreatedCategory(string titleId, Sprite icon, string descriptionId)
            {
                TitleId = titleId;
                Icon = icon;
                DescriptionId = descriptionId;
            }

            public void AddEntry(ToolbarData toolbarData, IToolbarElementData elementData)
            {
                ParentToolbarElementData category =
                    ToolbarTree.FindOrCreateCategory(toolbarData, TitleId, Icon, DescriptionId);

                ToolbarTree.Append(category, elementData);
            }

            public override string ToString()
            {
                return $"in new top-level category \"{TitleId}\"";
            }
        }

        private sealed class InCreatedGroup : IToolbarEntryInsertLocation
        {
            private readonly string ParentTitleId;
            private readonly string TitleId;
            private readonly string DescriptionId;
            private readonly Sprite Icon;

            public InCreatedGroup(string parentTitleId, string titleId, Sprite icon, string descriptionId)
            {
                ParentTitleId = parentTitleId;
                TitleId = titleId;
                Icon = icon;
                DescriptionId = descriptionId;
            }

            public void AddEntry(ToolbarData toolbarData, IToolbarElementData elementData)
            {
                IParentToolbarElementData parent = ToolbarTree.FindGroup(toolbarData, ParentTitleId);

                if (parent == null)
                {
                    ReportMiss(toolbarData, ParentTitleId);
                    parent = toolbarData.RootToolbarElement;
                }

                ParentToolbarElementData group =
                    ToolbarTree.FindOrCreateGroup(parent, TitleId, Icon, DescriptionId);

                ToolbarTree.Append(group, elementData);
            }

            public override string ToString()
            {
                return $"in new group \"{TitleId}\" under \"{ParentTitleId}\"";
            }
        }
    }
}
