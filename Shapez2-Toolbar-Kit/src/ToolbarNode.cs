using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QuinnBast.Shapez2.ToolbarKit
{
    /// What a toolbar element is, without needing to know the element classes.
    public enum ToolbarNodeKind
    {
        Root,

        /// A top-level category. Always a child of the root.
        Category,

        /// A folder. Any depth.
        Group,

        /// A placeable building or island - a leaf.
        Placement,

        /// A visual divider. Occupies a slot but is not addressable.
        Separator,

        Other
    }

    /// A snapshot of one toolbar element, and the way to walk the live tree.
    ///
    /// The enums cover what shipped with the game; this covers what is actually there, which
    /// includes anything other mods have added and anything a game update has changed. Take a
    /// snapshot inside a rewirer (ToolbarDump does exactly that) and query it afterwards.
    public sealed class ToolbarNode
    {
        private readonly List<ToolbarNode> ChildNodes = new();

        /// The translation id of this element's title, `.title` suffix included, or null for
        /// the root, separators, and placement leaves that carry a non-lazy title.
        public string TranslationId { get; private set; }

        public ToolbarNodeKind Kind { get; private set; }

        /// The game's class name, for when Kind is not specific enough.
        public string TypeName { get; private set; }

        /// The index Shifter's ChildAt() would use: separators are **not** counted.
        ///
        /// -1 for the root and for separators, which have no addressable index.
        public int Index { get; private set; }

        /// The element's real position in its parent's Children array, separators included.
        ///
        /// Differs from Index wherever a separator precedes it. Both are here because Shifter
        /// itself is inconsistent about which it means - see Path.
        public int RawIndex { get; private set; }

        /// `Root().ChildAt(..)...` as Shifter's FindElementParent would resolve it.
        ///
        /// Correct for addressing a *parent*, which is what a locator path is for. Be careful
        /// with the last hop: FindElementParent skips separators when walking down, but
        /// ToolbarEntryLocation's Before/After compute the final insert index from
        /// `parent.Children.Count()`, which counts them. So a path whose leaf sits after a
        /// separator inserts in a different place than the path reads. That inconsistency is
        /// the strongest argument for addressing by name and never writing these by hand.
        public string Path { get; private set; }

        public IReadOnlyList<ToolbarNode> Children => ChildNodes;

        public bool IsContainer => Kind == ToolbarNodeKind.Root
            || Kind == ToolbarNodeKind.Category
            || Kind == ToolbarNodeKind.Group;

        /// Every node beneath this one, depth first.
        public IEnumerable<ToolbarNode> Descendants()
        {
            foreach (ToolbarNode child in ChildNodes)
            {
                yield return child;

                foreach (ToolbarNode nested in child.Descendants())
                {
                    yield return nested;
                }
            }
        }

        /// Every node beneath this one that holds other things.
        public IEnumerable<ToolbarNode> Containers()
        {
            return Descendants().Where(node => node.IsContainer);
        }

        /// The top-level categories.
        public IEnumerable<ToolbarNode> Categories()
        {
            return ChildNodes.Where(node => node.Kind == ToolbarNodeKind.Category);
        }

        /// Every group anywhere beneath this one. Note that the same id can occur more than
        /// once - the building toolbar repeats BeltBuilding under four categories.
        public IEnumerable<ToolbarNode> Groups()
        {
            return Descendants().Where(node => node.Kind == ToolbarNodeKind.Group);
        }

        /// Every match for a translation id, in depth-first order. Empty if there are none;
        /// more than one if the id repeats.
        public IEnumerable<ToolbarNode> FindAll(string translationId)
        {
            return Descendants().Where(node => node.TranslationId == translationId);
        }

        public ToolbarNode Find(string translationId)
        {
            return FindAll(translationId).FirstOrDefault();
        }

        public ToolbarNode Find(ToolbarCategory category)
        {
            return Find(category.ToTranslationId());
        }

        public ToolbarNode Find(ToolbarGroup group)
        {
            return Find(group.ToTranslationId());
        }

        /// Build a snapshot of a live toolbar.
        public static ToolbarNode Snapshot(ToolbarData toolbarData)
        {
            ToolbarNode root = new()
            {
                Kind = ToolbarNodeKind.Root,
                TypeName = nameof(RootToolbarElementData),
                Index = -1,
                RawIndex = -1,
                Path = "Root()"
            };

            if (toolbarData?.RootToolbarElement != null)
            {
                Fill(root, toolbarData.RootToolbarElement, isRoot: true);
            }

            return root;
        }

        private static void Fill(ToolbarNode node, IParentToolbarElementData source, bool isRoot)
        {
            int index = 0;
            int rawIndex = 0;

            foreach (IToolbarElementData child in source.Children)
            {
                ToolbarNode childNode = new()
                {
                    TypeName = child.GetType().Name,
                    RawIndex = rawIndex++
                };

                if (child is ToolbarSlotSeparator)
                {
                    // Occupies a slot but is skipped by Shifter's indexing, so it gets no
                    // Index and does not advance the counter.
                    childNode.Kind = ToolbarNodeKind.Separator;
                    childNode.Index = -1;
                    childNode.Path = node.Path;
                    node.ChildNodes.Add(childNode);
                    continue;
                }

                childNode.Index = index;
                childNode.Path = $"{node.Path}.ChildAt({index})";
                index++;

                if (child is ParentToolbarElementData group)
                {
                    childNode.TranslationId = group.Title?.Id.Id;
                    childNode.Kind = isRoot ? ToolbarNodeKind.Category : ToolbarNodeKind.Group;
                    node.ChildNodes.Add(childNode);
                    Fill(childNode, group, isRoot: false);
                    continue;
                }

                childNode.Kind = child is IPresentableToolbarElementData
                    ? ToolbarNodeKind.Placement
                    : ToolbarNodeKind.Other;

                node.ChildNodes.Add(childNode);
            }
        }

        /// The subtree as text: path, type, and translation id per node.
        public string Describe()
        {
            StringBuilder text = new();
            Write(text, 0);
            return text.ToString();
        }

        private void Write(StringBuilder text, int depth)
        {
            text.Append(new string(' ', depth * 2))
                .Append(Path)
                .Append("  ")
                .Append(TypeName);

            if (TranslationId != null)
            {
                text.Append("  \"").Append(TranslationId).Append('"');
            }

            if (Kind == ToolbarNodeKind.Separator)
            {
                text.Append("  (separator - not addressable, skipped by ChildAt)");
            }

            foreach (ToolbarNode child in ChildNodes)
            {
                text.Append('\n');
                child.Write(text, depth + 1);
            }
        }
    }
}
