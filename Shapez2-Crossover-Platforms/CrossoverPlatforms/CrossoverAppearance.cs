using System;
using System.Collections.Generic;
using Game.Orchestration;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

#pragma warning disable CS0618 // IIslandPlatformDrawer is obsolete, but it is how space paths draw.

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Gives every crossing a <see cref="CrossoverPlatformDrawer"/>.
    ///
    /// Island visuals are not all held the same way. Most islands carry their meshes as CustomData
    /// on the definition; space paths do not. Theirs come from the visual theme, and the drawer
    /// that reads them lives in a session-wide dictionary that
    /// <c>GameSessionOrchestrator.CreateIslandPlatformDrawers</c> fills in by walking
    /// <c>GameIslands.SpaceBelts</c> and <c>SpacePipes</c>. A modded island is in neither list, so
    /// there is no extension point to use and nothing to attach - only that one method to hook.
    ///
    /// Shifter has rewirers for islands, placers, toolbars, simulation and prediction, but none for
    /// platform drawers, so this uses Shifter's own detour helper directly. The hook is a postfix:
    /// vanilla builds its dictionary, then the crossings are added to it.
    internal static class CrossoverAppearance
    {
        private static Hook DrawerHook;

        /// Installs the hook. Safe to call once; the handle is kept so Dispose can undo it.
        public static void Install(ILogger logger)
        {
            if (DrawerHook != null)
            {
                return;
            }

            try
            {
                DrawerHook = DetourHelper.CreatePostfixHook(
                    (GameSessionOrchestrator orchestrator, GameIslands islands) =>
                        orchestrator.CreateIslandPlatformDrawers(islands),
                    (orchestrator, islands, drawers) =>
                        AddCrossings(orchestrator, islands, drawers, logger));
            }
            catch (Exception exception)
            {
                // A crossing that draws as a plain platform is a blemish; one that stops the
                // session building its drawers is a broken game.
                logger.Exception?.LogException(exception);
            }
        }

        public static void Uninstall()
        {
            DrawerHook?.Dispose();
            DrawerHook = null;
        }

        private static Dictionary<IslandDefinitionId, IIslandPlatformDrawer> AddCrossings(
            GameSessionOrchestrator orchestrator, GameIslands islands,
            Dictionary<IslandDefinitionId, IIslandPlatformDrawer> drawers, ILogger logger)
        {
            try
            {
                VisualThemeBaseResources resources = orchestrator.Theme?.BaseResources;
                ISpacePathResources belts = resources?.SpaceBelts;
                ISpacePathResources pipes = resources?.SpacePipes;
                if (belts == null || pipes == null)
                {
                    logger.Warning?.Log("No space path theme resources; crossings stay plain");
                    return drawers;
                }

                foreach (CrossoverKind kind in Enum.GetValues(typeof(CrossoverKind)))
                {
                    // Path A is the West-to-East run and is the belt on a mixed crossing; path B
                    // is the perpendicular one. Same assignment CrossoverConnectors makes.
                    ISpacePathResources pathA = kind == CrossoverKind.PipePipe ? pipes : belts;
                    ISpacePathResources pathB = kind == CrossoverKind.BeltBelt ? belts : pipes;

                    Add(drawers, islands, CrossoverIds.Original(kind),
                        new CrossoverPlatformDrawer(pathA, pathB, mirrored: false), logger);
                    Add(drawers, islands, CrossoverIds.Mirrored(kind),
                        new CrossoverPlatformDrawer(pathA, pathB, mirrored: true), logger);
                }
            }
            catch (Exception exception)
            {
                logger.Exception?.LogException(exception);
            }

            return drawers;
        }

        private static void Add(
            Dictionary<IslandDefinitionId, IIslandPlatformDrawer> drawers, GameIslands islands,
            IslandDefinitionId id, IIslandPlatformDrawer drawer, ILogger logger)
        {
            if (!islands.TryGetDefinition(id, out IIslandDefinition _))
            {
                // A scenario this mod did not extend.
                return;
            }

            // The vanilla loops use Add, which would throw on a repeat. Nothing else should be
            // claiming these ids, but a reloaded mod could, and a duplicate key here would take
            // the whole session's drawer setup down.
            drawers[id] = drawer;
        }
    }
}
