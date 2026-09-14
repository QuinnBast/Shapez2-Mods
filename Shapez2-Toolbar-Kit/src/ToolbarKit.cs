using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ToolbarKit
{
    /// Shared settings for the toolbar kit.
    ///
    /// The kit is compiled into each mod as source rather than shipped as its own assembly, so
    /// this static is per-mod: every mod logs as itself, and no mod can be broken by another
    /// mod's copy. That is also why the kit keeps no registry of what it has created - the only
    /// state it trusts is the live ToolbarData it is handed, which is genuinely shared.
    public static class ToolbarKit
    {
        /// Set once from the mod constructor. Optional - the kit works without it, silently.
        ///
        /// Worth setting. Almost everything interesting the kit has to say happens during
        /// rewiring, long after the call that asked for it, and a toolbar entry that lands in
        /// the wrong place looks exactly like one that landed in the right place.
        public static ILogger Log { get; set; }
    }
}
