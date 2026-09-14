using System.Linq;
using Core.Localization;
using ShapezShifter.Flow.Toolbar;
using UnityEngine;

namespace QuinnBast.Shapez2.ToolbarKit
{
    /// Reading and editing the toolbar tree by name instead of by index.
    ///
    /// Shifter locates toolbar elements positionally and only positionally -
    /// IToolbarElementLocator exposes IndexAtLevel and Depth and nothing else - so
    /// `Root().ChildAt(5).ChildAt(4)` is the whole vocabulary. That is brittle in the worst way:
    /// a path that resolves to the wrong group inserts successfully and silently, and a path
    /// that resolves to nothing throws ToolbarQueryException during mod load and takes the
    /// game's startup with it.
    ///
    /// Names are stable where indices are not. Every group carries a LazyLocalizedText Title
    /// with a public TranslationId, so matching on the translation *id* is both stable across
    /// game updates and independent of the player's language - which matching on displayed text
    /// would not be.
    internal static class ToolbarTree
    {
        /// The first group anywhere in the tree whose title has this translation id.
        ///
        /// Depth-first from the root. Ids are expected to be unique; if they are not, the
        /// shallowest-leftmost wins, which is at least deterministic.
        public static ParentToolbarElementData FindGroup(ToolbarData toolbarData, string translationId)
        {
            if (toolbarData?.RootToolbarElement == null || string.IsNullOrEmpty(translationId))
            {
                return null;
            }

            return FindIn(toolbarData.RootToolbarElement, translationId);
        }

        private static ParentToolbarElementData FindIn(IParentToolbarElementData parent, string translationId)
        {
            foreach (IToolbarElementData child in parent.Children)
            {
                if (!(child is ParentToolbarElementData group))
                {
                    continue;
                }

                if (TitleId(group) == translationId)
                {
                    return group;
                }

                ParentToolbarElementData nested = FindIn(group, translationId);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        /// A new top-level category, or the existing one if this id is already there.
        ///
        /// Find-or-create rather than create, because AddEntry runs once per registered entry:
        /// a mod adding five islands to one new category calls this five times and must get the
        /// same category each time. Matching against the live tree also means two *different*
        /// mods asking for the same category id share it, without either knowing about the other.
        ///
        /// MechanicRequiredToUnlock is left empty on purpose. ToolbarBuilder checks
        /// `!MechanicRequiredToUnlock.IsEmpty` and substitutes AlwaysCompliantRule when it is,
        /// so an empty id means the category is simply always visible.
        public static ParentToolbarElementData FindOrCreateCategory(
            ToolbarData toolbarData, string translationId, Sprite icon, string descriptionId)
        {
            ParentToolbarElementData existing = FindGroup(toolbarData, translationId);
            if (existing != null)
            {
                return existing;
            }

            CategoryToolbarElementData category = new CategoryToolbarElementData
            {
                Children = new IToolbarElementData[0],
                Title = Text(translationId),
                Description = Text(descriptionId),
                Icon = icon
            };

            Append(toolbarData.RootToolbarElement, category);
            ToolbarKit.Log?.Info?.Log($"Toolbar: created top-level category '{translationId}'.");
            return category;
        }

        /// A new group inside a parent, or the existing one if this id is already there.
        public static ParentToolbarElementData FindOrCreateGroup(
            IParentToolbarElementData parent, string translationId, Sprite icon, string descriptionId)
        {
            foreach (IToolbarElementData child in parent.Children)
            {
                if (child is ParentToolbarElementData group && TitleId(group) == translationId)
                {
                    return group;
                }
            }

            GroupToolbarElementData created = new GroupToolbarElementData
            {
                Children = new IToolbarElementData[0],
                Title = Text(translationId),
                Description = Text(descriptionId),
                Icon = icon,
                RememberPreferredChild = true
            };

            Append(parent, created);
            ToolbarKit.Log?.Info?.Log($"Toolbar: created group '{translationId}'.");
            return created;
        }

        /// Add to the end of a parent's children.
        ///
        /// Goes through Shifter's InsertAtIndex, which knows how to write the Children array
        /// back on both RootToolbarElementData and ParentToolbarElementData - they do not share
        /// a settable member.
        public static void Append(IParentToolbarElementData parent, IToolbarElementData element)
        {
            parent.InsertAtIndex(element, parent.Children.Count());
        }

        public static string TitleId(ParentToolbarElementData group)
        {
            return group.Title?.Id.Id;
        }

        private static LazyLocalizedText Text(string translationId)
        {
            return string.IsNullOrEmpty(translationId)
                ? null
                : new LazyLocalizedText(new TranslationId(translationId));
        }

        /// The whole tree as text: path, type, and translation id per node.
        ///
        /// This is what makes name-based placement usable at all - without it there is no way
        /// to discover the ids to pass in. Printed whenever a lookup misses, and available on
        /// demand through ToolbarDump.
        public static string Describe(ToolbarData toolbarData)
        {
            if (toolbarData?.RootToolbarElement == null)
            {
                return "toolbar tree - (no root)";
            }

            return "toolbar tree - ChildAt paths, and title translation ids\n"
                + ToolbarNode.Snapshot(toolbarData).Describe();
        }
    }
}
