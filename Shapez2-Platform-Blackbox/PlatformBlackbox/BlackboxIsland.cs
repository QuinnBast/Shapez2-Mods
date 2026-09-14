using System;
using System.Collections.Generic;
using Core.Collections;
using Core.Localization;
using Game.Content.Features.SpacePaths.IslandIO;
using Game.Core.Content.Islands;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using ShapezShifter.Flow;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Flow.Research;
using ShapezShifter.Flow.Toolbar;
using ShapezShifter.Kit;
using ShapezShifter.Textures;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// Registers the blackbox platforms: one square definition per size, so a box can be as big
/// as the blueprint it stands in for needs.
///
/// A definition per size and no more. Every notch on every one of them carries a belt and a
/// pipe connector in **both** directions, which the game allows because all four are different
/// types - so a single definition serves any arrangement of ins and outs, and the alternative
/// (a definition per arrangement, two to the power of the notch count) never arises.
///
/// Which notches are inputs and which are outputs is the player's to decide, by attaching a
/// belt. The box pools whatever arrives and hands its products to whichever notch has room, so
/// no side has a fixed role and no arrangement has to be discovered.
/// </summary>
public static class BlackboxIsland
{
    /// <summary>One registered size.</summary>
    public struct Size
    {
        public int Width;
        public int Height;
        public IslandDefinitionId DefinitionId;

        /// Outward-facing chunk sides, which is what limits how wide a boundary it can carry.
        public int Notches
        {
            get { return 2 * (Width + Height); }
        }

        public override string ToString()
        {
            return Width + "x" + Height;
        }
    }

    /// A belt and a pipe per notch in each direction, so a box carries shapes and fluid alike.
    /// This is the count *per direction*, which is what fixes the number of lane bundles the
    /// simulation has to build.
    private const int BundlesPerNotch = 2;

    /// Squares from 1x1 to 4x4, giving 4, 8, 12 and 16 notches. Wider boundaries than that
    /// are rare, and every definition costs a toolbar entry.
    private static readonly int[] Sides = { 1, 2, 3, 4 };

    private static readonly List<Size> Registered = new List<Size>();

    public static IReadOnlyList<Size> Sizes
    {
        get { return Registered; }
    }

    /// <summary>Shared by every size, and what a placer id is matched against.</summary>
    public const string NamePrefix = "Blackbox";

    /// <summary>
    /// The smallest registered platform whose notches can carry this boundary. False when
    /// none can, in which case the largest is handed back so something still happens.
    /// </summary>
    public static bool TryFit(int notches, out Size size)
    {
        size = default(Size);

        foreach (Size candidate in Registered)
        {
            if (candidate.Notches >= notches)
            {
                size = candidate;
                return true;
            }
        }

        if (Registered.Count == 0)
        {
            return false;
        }

        size = Registered[Registered.Count - 1];
        return false;
    }

    /// <summary>
    /// Adds every size. A failure is reported rather than thrown - the reading and measuring
    /// commands work without any of this.
    /// </summary>
    public static void Register(ILogger logger)
    {
        foreach (int side in Sides)
        {
            try
            {
                Registered.Add(Add(side, side, logger));
            }
            catch (Exception exception)
            {
                logger.Exception?.LogException(exception);
                logger.Error?.Log("Blackbox: could not register the " + side + "x" + side
                    + " platform. See the exception above.");
            }
        }

        logger.Info?.Log("Blackbox: registered " + Registered.Count + " platform sizes.");
    }

    private static Size Add(int width, int height, ILogger logger)
    {
        string name = Name(width, height);

        IslandDefinitionId definitionId = new IslandDefinitionId(name);
        IslandDefinitionGroupId groupId = new IslandDefinitionGroupId(name + "Group");

        ChunkLayoutLookup<ChunkVector, IslandChunkData> layout = Layout(width, height);
        List<LocalChunkPivot> notches = Perimeter(width, height);

        IIslandGroupBuilder group = IslandGroup.Create(groupId)
            .WithTitle(("island-group." + name + "Group.title").T())
            .WithDescription(("island-group." + name + "Group.description").T())
            .WithIcon(FileTextureLoader.LoadTextureAsSprite(IconPath(logger), out _))
            // Items pass through it and nothing is built on it, so it belongs with the
            // transport platforms rather than the buildable ones.
            .AsTransportableIsland()
            .WithPreferredPlacement(DefaultPreferredPlacementMode.Single);

        IIslandBuilder island = Island.Create(definitionId)
            .WithLayout(layout)
            // Per-chunk, not bounding: ShapezShifter's WithBoundingCollider sizes the box as
            // (max - min) * 20 over the chunk positions, which is a chunk short on every axis -
            // and since every island is one layer deep, the height is always zero. A flat
            // collider is why these could not be hovered, selected or deleted.
            .WithPerChunkColliders()
            .WithConnectorData(Connectors(layout, notches, width, height))
            // canHoldBuildings stays false. Turning it on hands the island to NotchConnectors,
            // which deep-copies the definition per instance and rewrites its connectors - and on
            // a map that already held boxes built against the old definition, that left platforms
            // that could not be deleted and took the game with them. Not to be retried without a
            // throwaway save and a way back.
            .WithInteraction(flippable: false, canHoldBuildings: false)
            .WithDefaultChunkCost()
            .WithRenderingOptions(
                new HomogeneousChunkDrawing(ChunkPlatformDrawingContext.DrawAll()),
                drawPlayingField: true);

        AtomicIslands.Extend()
            .AllScenarios()
            .WithIsland(island, group)
            .UnlockedAtMilestone(new ByIndexMilestoneSelector(0))
            .WithDefaultPlacement()
            // Same place as before - a sibling of the platform groups - but named rather than
            // counted, so a game update that reorders the toolbar no longer silently moves
            // these somewhere else. pbx.toolbar is still how the id was found.
            .InToolbar(QuinnBast.Shapez2.ToolbarKit.ToolbarSlot.InGroup(
                QuinnBast.Shapez2.ToolbarKit.ToolbarCategory.RegularPlatform))
            // The stateful overload, so a placed box is saved and comes back knowing what
            // it stands in for and what it was measured to do.
            .WithSimulation<BlackboxIslandSimulation, BlackboxIslandState,
                BlackboxIslandSimulationFactory.Configuration>(
                new BlackboxIslandSimulationFactory(notches.Count * BundlesPerNotch,
                    NotchNames(notches)))
            .WithoutModules()
            .Build();

        return new Size { Width = width, Height = height, DefinitionId = definitionId };
    }

    /// <summary>
    /// A readable name per notch, in the order the connectors are declared, so a box can report
    /// which of its connections the game wired rather than only how many.
    /// </summary>
    private static string[] NotchNames(List<LocalChunkPivot> notches)
    {
        string[] names = new string[notches.Count];

        for (int i = 0; i < notches.Count; i++)
        {
            names[i] = notches[i].Direction + " of " + notches[i].Position;
        }

        return names;
    }

    /// <summary>
    /// A definition id, and once chosen it can never change.
    ///
    /// Definition ids are the serialised identity of a placed island: a save records the id,
    /// and a save whose id no longer exists refuses to load with "island definition was not
    /// migrated correctly". So the 1x1 keeps the bare name it was first released with, and
    /// only the larger sizes carry a suffix. Renaming any of these breaks every save holding
    /// one.
    /// </summary>
    public static string Name(int width, int height)
    {
        return width == 1 && height == 1 ? NamePrefix : NamePrefix + width + "x" + height;
    }

    /// Goes through ModResources rather than ModDirectoryLocator directly, so that a hot
    /// reload does not kill the mod in its constructor. See ModResources.
    private static string IconPath(ILogger logger)
    {
        return ModResources.Locate(logger).SubPath("Blackbox.png");
    }

    /// <summary>
    /// Every outward-facing chunk side, in a fixed order. That order is the contract: the nth
    /// input connector and the nth output connector are declared from it, and the simulation
    /// wires its nth input bundle to its nth output bundle.
    /// </summary>
    private static List<LocalChunkPivot> Perimeter(int width, int height)
    {
        List<ChunkVector> all = Positions(width, height);
        List<LocalChunkPivot> notches = new List<LocalChunkPivot>();

        // Derived from the same test the chunk data uses - a side is a notch when there is no
        // chunk beyond it - rather than from an assumption about which way north points.
        // Naming the sides by hand put the north and south pivots on inward-facing edges,
        // which a 1x1 hides because every side of a single chunk faces out.
        foreach (ChunkVector chunk in all)
        {
            foreach (GridRotation rotation in GridRotation.RotationsInClockwiseOrder)
            {
                ChunkDirection direction = rotation.ToChunkDirection();
                if (!all.Contains(chunk + direction))
                {
                    notches.Add(new LocalChunkPivot(chunk, direction));
                }
            }
        }

        return notches;
    }

    /// Every chunk of a w by h platform.
    private static List<ChunkVector> Positions(int width, int height)
    {
        List<ChunkVector> all = new List<ChunkVector>();

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                all.Add(new ChunkVector(x, y, 0));
            }
        }

        return all;
    }

    /// <summary>
    /// Belt in, pipe in, belt out and pipe out on every notch.
    ///
    /// Four connectors end up sharing each pivot, which the game permits so long as they are of
    /// different types - its check is on duplicate *types* per pivot, not on count. That is what
    /// collapses the port-arrangement problem: one definition per size serves any arrangement of
    /// ins and outs, and the alternative - a definition per arrangement, two to the power of the
    /// notch count - never arises.
    ///
    /// A pipe is not a separate mechanism. The game wires a pipe connector to the same item
    /// bundle as a belt, just carrying FluidPackageItem instead of a shape, so one simulation
    /// handles shapes and fluid alike.
    ///
    /// Nothing here says which notches are inputs and which are outputs, because nothing here
    /// can know: the player decides by attaching a belt, and the simulation reads that off the
    /// lanes the game wires up. Earlier versions paired each input notch with the notch opposite
    /// it, which mattered only because items used to travel through the box in their own lane;
    /// they now go into a shared pool, so an ingredient can enter anywhere and a product leave
    /// anywhere.
    /// </summary>
    private static IIslandConnectorData Connectors(
        ChunkLayoutLookup<ChunkVector, IslandChunkData> layout,
        List<LocalChunkPivot> notches, int width, int height)
    {
        List<EntityIO<LocalChunkPivot, IIslandConnector>> connectors =
            new List<EntityIO<LocalChunkPivot, IIslandConnector>>();

        foreach (LocalChunkPivot notch in notches)
        {
            connectors.Add(new EntityIO<LocalChunkPivot, IIslandConnector>(
                notch, new BlackboxConnectors.BeltInput()));
            connectors.Add(new EntityIO<LocalChunkPivot, IIslandConnector>(
                notch, new BlackboxConnectors.PipeInput()));
            connectors.Add(new EntityIO<LocalChunkPivot, IIslandConnector>(
                notch, new BlackboxConnectors.BeltOutput()));
            connectors.Add(new EntityIO<LocalChunkPivot, IIslandConnector>(
                notch, new BlackboxConnectors.PipeOutput()));
        }

        return new IslandConnectorData(connectors, layout.ChunkPositions);
    }

    /// <summary>
    /// A square of chunks with no buildable tiles. The box has no inside worth standing on -
    /// that is the whole point of it.
    /// </summary>
    private static ChunkLayoutLookup<ChunkVector, IslandChunkData> Layout(int width, int height)
    {
        return new ChunkLayoutLookup<ChunkVector, IslandChunkData>(Chunks(width, height));
    }

    private static IEnumerable<KeyValuePair<ChunkVector, IslandChunkData>> Chunks(int width, int height)
    {
        List<ChunkVector> all = Positions(width, height);

        foreach (ChunkVector chunk in all)
        {
            IslandChunkData data = IslandLayoutFactory.CreateIslandChunkData(
                chunkTile: chunk,
                notchDirections: External(chunk, all),
                neighborChunks: all,
                isBuildable: true,
                flipped: false,
                out _);

            for (int i = 0; i < data.TileVoidFlags_L.Length; i++)
            {
                data.TileVoidFlags_L[i] = true;
            }

            yield return new KeyValuePair<ChunkVector, IslandChunkData>(chunk, data);
        }
    }

    /// A chunk's sides that face out of the platform, which is where notches go.
    private static ChunkDirection[] External(ChunkVector chunk, List<ChunkVector> all)
    {
        List<ChunkDirection> outward = new List<ChunkDirection>();

        foreach (GridRotation rotation in GridRotation.RotationsInClockwiseOrder)
        {
            ChunkDirection direction = rotation.ToChunkDirection();
            if (!all.Contains(chunk + direction))
            {
                outward.Add(direction);
            }
        }

        return outward.ToArray();
    }
}
