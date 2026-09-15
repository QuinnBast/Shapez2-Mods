using System;
using System.Collections.Generic;
using Game.Core.Coordinates;
using Game.Core.Rendering.Islands;
using Game.Orchestration;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

#pragma warning disable CS0618 // IIslandPlatformDrawer is obsolete, but it is how space paths draw.

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Gives the cargo islands something to look like.
    ///
    /// Before this they all drew as the same bare platform deck: AddIsland asked for
    /// HomogeneousChunkDrawing and nothing else, and a definition with no mesh data is a
    /// definition the renderers walk straight past. A cargo belt, a packager and a store were
    /// literally indistinguishable.
    ///
    /// The twelve split into two groups that are drawn in completely different ways, and the
    /// split is not a style choice - it is where the game keeps the meshes:
    ///
    /// - The six **belts** have no mesh of their own and want none. Their track lives on the
    ///   visual theme, in SpaceBeltResources and SpacePipeResources, keyed by
    ///   PathNodeClassification, and is drawn by SpacePathPlatformDrawer - which drops the mesh
    ///   2.07314 world units and submits it to Renderers.SpacePaths, neither of which the modular
    ///   drawer does. So a cargo belt is given vanilla's own drawer over vanilla's own track and
    ///   comes out pixel-identical to the belt beside it, which is exactly right: it is a belt.
    ///
    /// - The six **machines** are new objects, so they carry loaded meshes as
    ///   ModularIslandMeshDrawer.Data on the definition, paired with the theme's IslandMaterial.
    ///
    /// Only the first group needs a hook. ModularIslandMeshDrawer is registered unconditionally
    /// in CreateMapSubDrawers and reads its Data off whatever definitions have it, so attaching
    /// is enough; platform drawers live in a session-wide dictionary built by
    /// GameSessionOrchestrator.CreateIslandPlatformDrawers, which walks GameIslands.SpaceBelts
    /// and SpacePipes. A modded island is in neither list, so there is no extension point - only
    /// that one method to postfix. Crossover Platforms hit the same wall and solved it the same
    /// way; this is the second mod in the repo to need it.
    ///
    /// The hook is also simply the earliest place this mod is handed both a GameIslands and a
    /// Theme, which is why the machine meshes are attached from here too rather than at build
    /// time - at mod construction there is no theme to take a material from.
    internal sealed class CargoAppearance : IDisposable
    {
        /// The one belt family: the straight run and both turns. The placer picks between them.
        ///
        /// There used to be a pipe-tagged twin of each, because a fluid station's input would not
        /// snap to a belt-tagged output. These carry both connector types instead, so one family
        /// serves both lines - see TrainCargoToolsMod.Connectors and CargoBeltSimulation.
        private static readonly string[] BeltIds =
        {
            "CargoBelt", "CargoBelt_LeftTurn", "CargoBelt_RightTurn",

            // The legacy fluid ids, kept registered so old saves load - see
            // TrainCargoToolsMod. They are the same island, so they draw the same.
            "FluidCargoBelt", "FluidCargoBelt_LeftTurn", "FluidCargoBelt_RightTurn",

            // The junctions. Easy to miss when adding a path piece: registering the island and
            // generating its mesh is not enough, because a path-track island has no platform
            // frame to fall back on - left out of this list it places, connects and simulates
            // while drawing nothing at all.
            "CargoBelt_LeftFwdSplitter", "CargoBelt_RightFwdSplitter",
            "CargoBelt_YSplitter", "CargoBelt_TripleSplitter",
            "CargoBelt_LeftFwdMerger", "CargoBelt_RightFwdMerger",
            "CargoBelt_YMerger", "CargoBelt_TripleMerger",

            // The lifts. Same reason as the junctions: left out of this list a path-track island
            // places and simulates while drawing nothing at all.
            "CargoBelt_Lift1UpForward", "CargoBelt_Lift1UpRight",
            "CargoBelt_Lift1UpBackward", "CargoBelt_Lift1UpLeft",
            "CargoBelt_Lift1DownForward", "CargoBelt_Lift1DownRight",
            "CargoBelt_Lift1DownBackward", "CargoBelt_Lift1DownLeft",
            "CargoBelt_Lift2UpForward", "CargoBelt_Lift2UpRight",
            "CargoBelt_Lift2UpBackward", "CargoBelt_Lift2UpLeft",
            "CargoBelt_Lift2DownForward", "CargoBelt_Lift2DownRight",
            "CargoBelt_Lift2DownBackward", "CargoBelt_Lift2DownLeft",
        };

        /// Lift id -> its ramp mesh. A lift cannot be classified - its ends sit on different
        /// layers, which `PathNodeClassification` has no value for - so it is looked up by name
        /// rather than by shape, and the two lists are kept in step by construction: both are
        /// built from the same four exits and four rises.
        private static readonly Dictionary<string, string> LiftMeshes = BuildLiftMeshes();

        private static Dictionary<string, string> BuildLiftMeshes()
        {
            Dictionary<string, string> meshes = new();

            foreach (string exit in new[] { "Forward", "Right", "Backward", "Left" })
            {
                foreach (int layers in new[] { 1, 2 })
                {
                    foreach (string climb in new[] { "Up", "Down" })
                    {
                        meshes[$"CargoBelt_Lift{layers}{climb}{exit}"] =
                            $"CargoTrackLift{layers}{climb}{exit}";
                    }
                }
            }

            return meshes;
        }

        /// Island id -> mesh file, because the two are no longer the same string.
        ///
        /// `CargoStoreAny` is the combined store and the only one that can be built; it wears the
        /// same rack mesh the per-kind stores use. Keying meshes by island id alone missed it
        /// entirely - the buildable store had no mesh at all and drew as a bare black platform,
        /// while the two hidden legacy stores that nothing can place were the ones getting art.
        internal static readonly (string Island, string Mesh)[] MachineMeshes =
        {
            ("CargoPackager", "CargoPackager"),
            ("CargoUnpackager", "CargoUnpackager"),
            ("FluidCargoPackager", "FluidCargoPackager"),
            ("FluidCargoUnpackager", "FluidCargoUnpackager"),

            ("CargoStoreAny", "CargoStore"),
            ("CargoStore", "CargoStore"),
            ("FluidCargoStore", "FluidCargoStore"),
        };

        /// Every distinct mesh file to load, which is fewer than the islands that use them.
        /// Classification -> the mod's own track mesh. See CargoTrackDrawer for why a cargo
        /// belt no longer borrows vanilla's.
        internal static readonly (PathNodeClassification Run, string Mesh)[] TrackMeshes =
        {
            (PathNodeClassification.Forward, "CargoTrack"),
            (PathNodeClassification.LeftTurn, "CargoTrackLeft"),
            (PathNodeClassification.RightTurn, "CargoTrackRight"),

            // Junctions. PathNodeClassification already names all four, and
            // PlatformPathDrawingClassifier works them out from the connectors, so a splitter
            // needs no classification of its own - only a mesh to hang on the one it gets.
            // Eight meshes, not five. While the arms were straight a splitter and the merger on
            // the same spokes were the same shape and shared; now that they curve, one bends
            // about the near chunk corner and the other about the far one - see merger_arm.
            (PathNodeClassification.LeftForwardSplitter, "CargoTrackSplitLeftFwd"),
            (PathNodeClassification.RightForwardSplitter, "CargoTrackSplitRightFwd"),
            (PathNodeClassification.LeftRightSplitter, "CargoTrackSplitY"),
            (PathNodeClassification.TripleSplitter, "CargoTrackSplitTriple"),
            (PathNodeClassification.LeftForwardMerger, "CargoTrackMergeLeftFwd"),
            (PathNodeClassification.RightForwardMerger, "CargoTrackMergeRightFwd"),
            (PathNodeClassification.LeftRightMerger, "CargoTrackMergeY"),
            (PathNodeClassification.TripleMerger, "CargoTrackMergeTriple"),
        };

        internal static string[] MeshIds
        {
            get
            {
                List<string> names = new();

                foreach (string mesh in LiftMeshes.Values)
                {
                    names.Add(mesh);
                }

                foreach ((PathNodeClassification _, string mesh) in TrackMeshes)
                {
                    names.Add(mesh);
                }

                foreach ((string _, string mesh) in MachineMeshes)
                {
                    if (!names.Contains(mesh))
                    {
                        names.Add(mesh);
                    }
                }

                return names.ToArray();
            }
        }

        private readonly CargoMeshes Meshes;
        private readonly ILogger Log;

        /// Resolved once the theme exists, then applied to the loaded meshes. See CargoPalette
        /// for why the meshes cannot simply carry their own colours.
        public CargoPalette Palette { get; }

        private Hook DrawerHook;

        /// The live theme, kept from the hook.
        ///
        /// Nothing else in this mod is handed one. IGameSessionManagers - what GameHelper.Core
        /// returns - exposes the player, the mode, the registries and the viewport, but no
        /// theme and no session, so there is no second way to ask for it later. Stashing the
        /// reference as it goes past is what lets cargotools.dumpatlas reach the island
        /// material at all.
        public VisualThemeBaseResources ThemeResources { get; private set; }

        /// Also kept from the hook, and for the same reason: it is the only place this mod is
        /// handed the island catalogue, which cargotools.dumpicons needs to read vanilla icons.
        public GameIslands Islands { get; private set; }

        /// Re-applies the palette to the loaded meshes, after cargotools.uv has moved a role.
        public void RecolourMeshes()
        {
            Meshes.ApplyPalette(Palette, Log);
        }

        public CargoAppearance(CargoMeshes meshes, ILogger logger)
        {
            Meshes = meshes;
            Log = logger;
            Palette = new CargoPalette(logger);

            try
            {
                DrawerHook = DetourHelper.CreatePostfixHook(
                    (GameSessionOrchestrator orchestrator, GameIslands islands) =>
                        orchestrator.CreateIslandPlatformDrawers(islands),
                    (orchestrator, islands, drawers) => Apply(orchestrator, islands, drawers));
            }
            catch (Exception exception)
            {
                // Plain platforms are a blemish. A session that cannot build its drawer table is
                // a broken game, so this never escapes.
                logger.Exception?.LogException(exception);
            }
        }

        public void Dispose()
        {
            DrawerHook?.Dispose();
            DrawerHook = null;
        }

        private Dictionary<IslandDefinitionId, IIslandPlatformDrawer> Apply(
            GameSessionOrchestrator orchestrator, GameIslands islands,
            Dictionary<IslandDefinitionId, IIslandPlatformDrawer> drawers)
        {
            try
            {
                VisualThemeBaseResources resources = orchestrator.Theme?.BaseResources;
                ThemeResources = resources;
                Islands = islands;
                if (resources == null)
                {
                    Log.Warning?.Log("No theme base resources; cargo islands stay plain");
                    return drawers;
                }

                // Before anything is drawn: the meshes ship with sentinel UVs and are only
                // the right colour once these have been sampled off vanilla geometry.
                Palette.Resolve(resources);
                Meshes.ApplyPalette(Palette, Log);

                // Belt track, not pipe, for every cargo belt. It carries discrete containers
                // whichever line it is on, and the corner posts are what say it is a cargo belt.
                AddTrack(drawers, islands, BeltIds, resources.SpaceBelts, "belt",
                    resources.IslandMaterial);
                AttachMachineMeshes(islands, resources.IslandMaterial);
            }
            catch (Exception exception)
            {
                Log.Exception?.LogException(exception);
            }

            return drawers;
        }

        /// Points the belt family at vanilla's space path drawer.
        /// The mod's own track mesh for a classification, paired with the theme's island
        /// material so it shades like everything else on the map.
        /// A mesh by file name, for the pieces that have no classification to look one up by.
        private bool TryNamedMesh(
            string name, LODMaterialAsset islandMaterial, out ILODMeshMaterial mesh)
        {
            mesh = null;

            if (islandMaterial == null || !Meshes.TryGet(name, out LOD6Mesh lod))
            {
                return false;
            }

            mesh = new CargoMeshes.ThemeMeshMaterial(lod, islandMaterial);
            return true;
        }

        private bool TryTrackMesh(
            PathNodeClassification run, LODMaterialAsset islandMaterial, out ILODMeshMaterial mesh)
        {
            mesh = null;
            if (islandMaterial == null)
            {
                return false;
            }

            foreach ((PathNodeClassification candidate, string name) in TrackMeshes)
            {
                if (candidate == run && Meshes.TryGet(name, out LOD6Mesh lod))
                {
                    mesh = new CargoMeshes.ThemeMeshMaterial(lod, islandMaterial);
                    return true;
                }
            }

            return false;
        }

        private void AddTrack(
            Dictionary<IslandDefinitionId, IIslandPlatformDrawer> drawers, GameIslands islands,
            IReadOnlyList<string> ids, ISpacePathResources track, string what,
            LODMaterialAsset islandMaterial)
        {
            if (track == null)
            {
                Log.Warning?.Log($"No space {what} theme resources; those cargo belts stay plain");
                return;
            }

            foreach (string id in ids)
            {
                if (!islands.TryGetDefinition(new IslandDefinitionId(id), out IIslandDefinition definition))
                {
                    // A scenario this mod did not extend.
                    continue;
                }

                // Classified from the connector data rather than hardcoded per id, so it keeps
                // working if a connector ever moves - but *not* with
                // PlatformPathDrawingClassifier.TryClassifySpacePathNode, which cannot cope with
                // these islands. It pairs every input with every output, and a cargo belt has two
                // of each at one pivot (a belt tag and a pipe tag), so one straight run produces
                // four identical West-to-East connections. FixedList8Bytes.Add does not dedupe,
                // TryClassifyNode only handles counts of 1 to 3, and the whole thing would come
                // back false - leaving every belt drawing nothing at all, since a path-track
                // island has no platform frame to fall back on.
                //
                // So the directions are deduped here and handed to the game's own TryClassifyNode,
                // which is public. The classification logic stays vanilla's; only the double
                // counting is removed.
                // A lift spans layers, and is not a flat node at all: its ends differ in z, so
                // the game's own classifier rejects it outright (`TryClassifySpacePathNode`
                // returns None the moment an output's z differs from an input's), and the
                // direction-only test here would call a Backward lift - West in, West out -
                // unclassifiable too. So layers are read off the layout and a lift is simply
                // drawn as straight track, one deck per layer it spans.
                // A lift is drawn as one ramp climbing the whole way, not as a deck per layer.
                // It has no classification to look up - its ends are on different layers, which
                // the game's own classifier rejects outright and the direction-only test here
                // cannot make sense of either, a Backward lift going West in and West out.
                bool isLift = LiftMeshes.TryGetValue(id, out string liftMesh);

                PathNodeClassification classification = PathNodeClassification.Forward;

                if (!isLift
                    && (!definition.CustomData.TryGet(out IIslandConnectorData connectors)
                        || !TryClassify(connectors, out classification)))
                {
                    Log.Warning?.Log($"Could not classify {id} as a space path node; it stays plain");
                    continue;
                }

                // Indexer, not Add. Vanilla's loops use Add and would throw on a repeat; nothing
                // else should claim these ids, but a reloaded mod could, and a duplicate key here
                // takes the whole session's drawer setup with it.
                ILODMeshMaterial custom;

                if (isLift)
                {
                    if (!TryNamedMesh(liftMesh, islandMaterial, out custom))
                    {
                        Log.Warning?.Log($"No ramp mesh '{liftMesh}'; {id} stays plain");
                        continue;
                    }
                }
                else if (!TryTrackMesh(classification, islandMaterial, out custom))
                {
                    Log.Warning?.Log($"No cargo track mesh for {classification}; {id} stays plain");
                    continue;
                }

                drawers[new IslandDefinitionId(id)] =
                    new CargoTrackDrawer(track, classification, custom);
            }
        }

        /// Classifies a path node from distinct connector directions.
        ///
        /// See the call site: the game's own TryClassifySpacePathNode double-counts an island
        /// that carries two connector types at one pivot.
        /// The distinct chunk layers an island occupies, lowest first.
        ///
        /// One entry for everything but a lift. Read from the layout rather than from the id,
        /// so a lift that is ever reshaped keeps drawing correctly.
        private static int[] Layers(IIslandDefinition definition)
        {
            SortedSet<int> layers = new();

            foreach (ChunkVector chunk in definition.Layout.GetChunkPositions())
            {
                layers.Add(chunk.z);
            }

            int[] ordered = new int[layers.Count];
            layers.CopyTo(ordered);
            return ordered;
        }

        private static bool TryClassify(
            IIslandConnectorData connectors, out PathNodeClassification classification)
        {
            FixedInputOutputConnections4Set<ChunkDirection> data = default;

            foreach (ChunkDirection input in DistinctDirections(
                         connectors.ConnectorsOfType<ISpacePathInputConnector>()))
            {
                foreach (ChunkDirection output in DistinctDirections(
                             connectors.ConnectorsOfType<ISpacePathOutputConnector>()))
                {
                    data.AddConnection(new IOConnection<ChunkDirection>(input, output));
                }
            }

            return PlatformPathDrawingClassifier.TryClassifyNode(data, out classification);
        }

        private static List<ChunkDirection> DistinctDirections<TConnector>(
            IReadOnlyList<EntityIO<LocalChunkPivot, TConnector>> connectors)
            where TConnector : class, IEntityConnector
        {
            List<ChunkDirection> directions = new();
            for (int i = 0; i < connectors.Count; i++)
            {
                ChunkDirection direction = connectors[i].Location.Direction;
                if (!directions.Contains(direction))
                {
                    directions.Add(direction);
                }
            }

            return directions;
        }

        /// Hangs a loaded mesh on each machine definition.
        private void AttachMachineMeshes(GameIslands islands, LODMaterialAsset islandMaterial)
        {
            if (islandMaterial == null)
            {
                Log.Warning?.Log("Theme has no island material; cargo machines stay plain");
                return;
            }

            foreach ((string island, string mesh) in MachineMeshes)
            {
                if (!Meshes.TryGet(mesh, out LOD6Mesh lod))
                {
                    // Already warned about at load time; do not warn once per session as well.
                    continue;
                }

                AttachMesh(islands, island, lod, islandMaterial);
            }
        }

        /// Attaches one mesh to one island definition as its modular draw data.
        private void AttachMesh(
            GameIslands islands, string id, LOD6Mesh mesh, LODMaterialAsset islandMaterial)
        {
            if (!islands.TryGetDefinition(new IslandDefinitionId(id), out IIslandDefinition definition))
            {
                return;
            }

            // IEntityDefinition.CustomData is only ICustomDataReader. Attaching needs the
            // concrete IslandDefinition, which is what the builder actually made.
            if (!(definition is IslandDefinition concrete))
            {
                Log.Warning?.Log($"{id} is not an IslandDefinition; cannot attach its mesh");
                return;
            }

            // One module, unrotated, at the island's own origin: these are all single-chunk
            // islands and the meshes are authored centred on that chunk. Module.Transform is
            // multiplied by the island transform, so identity means "wherever the island is,
            // whichever way it is turned" - which is what makes one mesh serve all four
            // rotations. Note the offset is an integer ChunkVector, so the belt marker's drop to
            // track height is baked into its vertices instead.
            ModularIslandMeshDrawer.Module module = new(
                LocalChunkTransform.Identity,
                new CargoMeshes.ThemeMeshMaterial(mesh, islandMaterial));

            // AttachOrReplace, not Attach: definitions are rebuilt per session but this hook
            // runs per session too, and Attach on an already-attached type is how a second entry
            // into a session turns into a crash.
            concrete.CustomData.AttachOrReplace(
                new ModularIslandMeshDrawer.Data(new[] { module }));
        }
    }
}
