using System;
using ShapezShifter.Hijack;

namespace QuinnBast.Shapez2.ToolbarKit
{
    /// Logs the toolbar tree, with every translation id in it.
    ///
    /// ToolbarSlot already prints this when a lookup misses, which covers the case where you
    /// guessed wrong. This covers the case where you have not guessed yet: register it and the
    /// ids to pass to InGroup are in the log.
    ///
    /// <code>
    /// DumpHandle = GameRewirers.AddRewirer(new ToolbarDump(logger));
    /// </code>
    ///
    /// Register it *last* if you want to see your own entries in the output - rewirers are
    /// applied in registration order, so a dump registered first reports the vanilla toolbar
    /// and says nothing about where your entries went.
    public sealed class ToolbarDump : IToolbarDataRewirer
    {
        /// The last tree seen, as text. Kept because the rewire happens long before anyone asks.
        public string Captured { get; private set; }

        /// The same tree, queryable. Enumerate Categories(), Groups(), Descendants(), or look
        /// an id up with Find(..) - including ids this kit's enums have never heard of, which
        /// is the point of having it as well as them.
        public ToolbarNode Tree { get; private set; }

        public ToolbarData ModifyToolbarData(ToolbarData toolbarData)
        {
            try
            {
                Tree = ToolbarNode.Snapshot(toolbarData);
                Captured = ToolbarTree.Describe(toolbarData);
                ToolbarKit.Log?.Info?.Log(Captured);
            }
            catch (Exception exception)
            {
                // Never break the toolbar over a diagnostic.
                ToolbarKit.Log?.Exception?.LogException(exception);
            }

            return toolbarData;
        }
    }
}
