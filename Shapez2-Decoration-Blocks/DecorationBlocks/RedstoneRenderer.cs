using Core.Localization;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using Game.Core.Rendering.MeshGeneration;
using Game.Core.Simulation;
using JetBrains.Annotations;
using UnityEngine;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// The meshes one redstone component needs, built once at load and picked between per frame.
///
/// A component's look depends on its state, and its state lives in RedstoneWorld rather than on
/// the definition - so this carries every variant and the renderer chooses. Dust is the awkward
/// one: its shape depends on its neighbours (sixteen connection masks) and its colour on its
/// signal strength (sixteen levels), and 256 meshes would be silly. So shape is a mesh and
/// **strength is a tint**, applied through a material property block.
public sealed class RedstoneDrawData : IBuildingCustomDrawData, IBuildingMirrorableCustomDrawData
{
    public RedstoneDrawData(RedstoneDefinition component, BlockAtlas atlas)
    {
        Component = component;

        if (component.Kind == RedstoneKind.Dust)
        {
            Dust = new IMeshReference[16];

            for (int mask = 0; mask < 16; mask++)
            {
                Dust[mask] = new TemporaryMeshReference(RedstoneMeshes.Dust(mask, atlas));
            }

            return;
        }

        // A repeater needs one mesh per delay as well as per state, because the delay is read off
        // the gap between its two torches - that is how a Minecraft player tells a 1-tick
        // repeater from a 4-tick one without clicking it.
        int variants = component.Kind == RedstoneKind.Repeater ? 4
            : component.Kind == RedstoneKind.Torch ? 2
            : component.Kind == RedstoneKind.Comparator ? 2
            : 1;

        Variants = new IMeshReference[variants * 2];

        for (int variant = 1; variant <= variants; variant++)
        {
            for (int state = 0; state < 2; state++)
            {
                Variants[VariantIndex(variant, state == 1)] = new TemporaryMeshReference(
                    RedstoneMeshes.Build(component, atlas, state == 1, variant));
            }
        }
    }

    public RedstoneDefinition Component { get; }

    /// Indexed by VariantIndex. Two entries for most components, eight for a repeater.
    public IMeshReference[] Variants { get; }

    public IMeshReference[] Dust { get; }

    /// Variant means the repeater's delay (1-4), the torch's mounting (1 free, 2 mounted) or the
    /// comparator's mode (1 compare, 2 subtract).
    public static int VariantIndex(int variant, bool active)
    {
        return (Mathf.Clamp(variant, 1, 4) - 1) * 2 + (active ? 1 : 0);
    }

    public IMeshReference For(RedstoneComponent state)
    {
        if (state == null)
        {
            return Variants[0];
        }

        int variant = Component.Kind == RedstoneKind.Repeater ? state.RepeaterDelay
            : Component.Kind == RedstoneKind.Torch ? (state.Mounted ? 2 : 1)
            : Component.Kind == RedstoneKind.Comparator ? (state.Subtracting ? 2 : 1)
            : 1;

        int index = VariantIndex(variant, state.IsActive);

        return index < Variants.Length ? Variants[index] : Variants[0];
    }

    public IBuildingCustomDrawData Mirror(IMeshCache meshCache)
    {
        return this;
    }
}

/// The dust tints, in a class of their own rather than as statics on the renderer.
///
/// They used to live on `RedstoneRenderer`, which was fine while that was one non-generic class.
/// It is now a generic base with two instantiations, and **a static field in a generic type
/// exists once per instantiation** - so the palette would be built twice, the second copy for
/// the converter renderer, which never draws dust and would never touch it. Harmless but untrue,
/// and the kind of thing that is confusing to find later.
internal static class RedstoneDustPalette
{
    /// One block per signal strength, built once. `MaterialPropertyHelpers.CreateBaseColorBlock`
    /// would be the obvious call and is wrong here: it mutates and returns a **shared static**
    /// block, which is fine for set-then-draw-immediately and useless when sixteen of them have
    /// to coexist across a frame's worth of batches.
    /// Declared first on purpose. Static field initialisers run in declaration order, so a
    /// property id used by a later initialiser has to be resolved before it - the same ordering
    /// rule that turns an innocent-looking static field into a mod that fails to load.
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

    internal static readonly MaterialPropertyBlock[] Blocks = BuildPowerBlocks();

    internal static readonly PropertyBlockHash[] Hashes = BuildPowerHashes();

    private static MaterialPropertyBlock[] BuildPowerBlocks()
    {
        MaterialPropertyBlock[] blocks = new MaterialPropertyBlock[RedstoneWorld.MaxPower + 1];

        for (int power = 0; power < blocks.Length; power++)
        {
            blocks[power] = new MaterialPropertyBlock();
            blocks[power].SetColor(BaseColor, RedstoneCatalog.DustColour(power));
        }

        return blocks;
    }

    /// The hash is what the instanced renderer batches on, so it has to be stable per power level
    /// and distinct between them - a fresh hash each frame would put every dust in its own batch.
    /// `AcquirePropertyBlockHash(string)` caches by key, which is exactly that guarantee.
    private static PropertyBlockHash[] BuildPowerHashes()
    {
        PropertyBlockHash[] hashes = new PropertyBlockHash[RedstoneWorld.MaxPower + 1];

        for (int power = 0; power < hashes.Length; power++)
        {
            hashes[power] = InstancingIdManager.AcquirePropertyBlockHash("DecorationBlocks/Dust/" + power);
        }

        return hashes;
    }
}

/// Draws every redstone component, in whatever state the world says it is in.
///
/// Same argument as BlockRenderer for being a simulation renderer rather than an IMapSubDrawer:
/// the base class already caches entities per chunk, walks only the culled chunks, applies the
/// layer predicate and frustum-tests each entity.
///
/// **Generic over the simulation type, with a concrete subclass per type, because a renderer is
/// matched to its buildings by that type and nothing else.** `SimulationsDrawer` collects every
/// `ISimulationRenderer` in the loaded assemblies and keys them on
/// `(LocalizedSimulationType, SimulationType)`; a building whose simulation matches no key is
/// simply never drawn. Nothing is logged, because from the drawer's side nothing is wrong. So
/// the moment the converter stopped sharing `RedstoneSimulation` - it has to, it implements
/// `ISignalSimulation` and `RedstoneSimulation` is a sealed inert marker - it needed a renderer
/// of its own, and the whole body here is shared rather than duplicated.
///
/// The base is abstract *and* still has an open type parameter, either of which is enough for the
/// scan to skip it (`ReflectionUtils.CreateInstancesForInterfaceImplementations` filters on
/// `IsAbstract` and `ContainsGenericParameters`), so only the two concrete ones below register.
/// Two renderers claiming one simulation type would not: the second loses and logs
/// "An ISimulationRenderer that handles ... was already added".
///
/// State comes from the world, keyed on the entity's tile, rather than from the entity's
/// simulation. For everything but the converter the simulation is a shared singleton with no
/// per-instance identity - it exists only because Shifter's chain insists on one - so the
/// position is what identifies a component.
public abstract class RedstoneRendererBase<TSimulation> :
    StatelessBuildingSimulationRenderer<TSimulation, RedstoneDrawData>
    where TSimulation : ISimulation
{
    protected RedstoneRendererBase(IMapModel map)
        : base(map) { }

    public override bool ShouldDraw(LODRenderConfig lod)
    {
        return lod.ShouldRenderBuildings;
    }

    public override void OnDrawDynamic(in Entity entity, FrameDrawOptions options)
    {
        RedstoneDrawData drawData = entity.DrawData;

        if (drawData == null)
        {
            return;
        }

#pragma warning disable CS0618
        BlockMaterials materials = BlockMaterials.Shared(options.Theme.BaseResources, BlockRenderer.Log);
#pragma warning restore CS0618

        // The opaque material, because nothing here has a transparent texel left in it: dust is
        // geometry on a solid tile, and the torch and lever sample the solid part of their
        // textures. Alpha clipping does not work in the shipped build - see CubeMesh.Frame.
        IMaterialReference material = materials.Opaque;

        if (material == null || material.Empty)
        {
            return;
        }

        GlobalTileCoordinate tile = entity.Transform.Position;
        Matrix4x4 transform = FastMatrix.ByTransform(entity.Transform);

        if (drawData.Component.Kind == RedstoneKind.Dust)
        {
            // **Translation only, no rotation.** A dust's shape comes from its connection mask,
            // which is computed in world directions - north is north whatever the player happened
            // to be holding when they placed it. Drawing it through the building's rotation spun
            // the arms away from the neighbours they describe, so a rotated dust drew its
            // connections pointing at nothing. Every other component wants the rotation, because
            // every other component's shape means something relative to the way it faces.
            DrawDust(
                options, drawData, tile,
                FastMatrix.Translate(entity.Transform.Position.ToCenter_W()), material);
            return;
        }

        RedstoneWorld.Instance.TryGet(tile, out RedstoneComponent component);

        options.Renderers.Buildings.Add(
            drawData.For(component),
            material,
            transform,
            options.LOD.Shadows,
            options.LOD.Shadows);
    }

    private static void DrawDust(
        FrameDrawOptions options, RedstoneDrawData drawData, GlobalTileCoordinate tile,
        Matrix4x4 transform, IMaterialReference material)
    {
        int mask = RedstoneWorld.Instance.ConnectionMask(tile);
        int power = RedstoneWorld.Instance.TryGet(tile, out RedstoneComponent component)
            ? Mathf.Clamp(component.Power, 0, RedstoneWorld.MaxPower)
            : 0;

        options.Renderers.Buildings.AddWithProperties(
            drawData.Dust[mask],
            material,
            transform,
            RedstoneDustPalette.Blocks[power],
            RedstoneDustPalette.Hashes[power],
            options.LOD.Shadows,
            options.LOD.Shadows);
    }
}

/// Every component except the converter: they all share the inert `RedstoneSimulation` marker.
[UsedImplicitly]
public sealed class RedstoneRenderer : RedstoneRendererBase<RedstoneSimulation>
{
    public RedstoneRenderer(IMapModel map)
        : base(map) { }
}

/// The converter alone, because it alone has a real simulation.
[UsedImplicitly]
public sealed class RedstoneConverterRenderer : RedstoneRendererBase<RedstoneConverterSimulation>
{
    public RedstoneConverterRenderer(IMapModel map)
        : base(map) { }
}

/// The shapes. Boxes, in tile space, matching what the components look like in Minecraft closely
/// enough to be recognised at shapez's camera distance - which is further out than Minecraft's,
/// so fine detail would be wasted.
internal static class RedstoneMeshes
{
    /// Where the solid pixels are in Minecraft own textures, in 0-1 tile space.
    ///
    /// A torch texture is a torch shape on a **transparent field**, and mapping a box to the whole
    /// tile puts that field on the box faces - where it shows as black unless the shader discards
    /// it. That cannot be relied on: URP strips shader variants it believes unused, and a stripped
    /// `_ALPHATEST_ON` falls back silently to a variant that renders the transparent texels rather
    /// than clipping them. Minecraft own torch model does not depend on it either; it maps the box
    /// to the stick pixels. So does this.
    ///
    /// Rows are counted from the top of the PNG and v from the bottom, hence the subtraction.
    private static readonly Rect TorchHead = Window(6, 10, 5, 9);

    private static readonly Rect TorchStick = Window(7, 9, 9, 16);

    private static readonly Rect LeverStick = Window(7, 9, 6, 16);

    /// A white arrow lying on top of a component, pointing the way its **signal flows**.
    ///
    /// Signal flow rather than facing, because that is the one reading that is the same for every
    /// component. A repeater and a comparator send their output the way they face, so their arrows
    /// point at `Facing`. An observer is the odd one: it *watches* the tile it faces and pulses out
    /// of its **back**, so an arrow at `Facing` points at its input and reads backwards to anyone
    /// wiring it up. Its arrow is reversed, and the side panel names the tile it is watching.
    ///
    /// Asked for directly, and the right answer regardless: a component's direction was readable
    /// only from its texture, and a texture can be rotated wrongly - which it was, twice. An arrow
    /// built from geometry cannot be, because it is made of the same +X the logic uses. Four bars
    /// of decreasing width make a head, and one makes the shaft; axis-aligned boxes cannot do a
    /// diagonal, and at this size they do not need to.
    private static IEnumerable<BoxMesh.Box> Arrow(float y, float scale = 1.0f, bool backwards = false)
    {
        float thickness = 0.03f;
        float direction = backwards ? -1.0f : 1.0f;

        BoxMesh.Box Bar(float minX, float maxX, float halfZ)
        {
            float a = minX * scale * direction;
            float b = maxX * scale * direction;

            return new BoxMesh.Box(
                new Vector3(Mathf.Min(a, b), y, -halfZ * scale),
                new Vector3(Mathf.Max(a, b), y + thickness, halfZ * scale),
                RedstoneCatalog.Solid);
        }

        yield return Bar(-0.20f, 0.14f, 0.045f);
        yield return Bar(0.14f, 0.22f, 0.165f);
        yield return Bar(0.22f, 0.30f, 0.110f);
        yield return Bar(0.30f, 0.38f, 0.055f);
    }

    private static Rect Window(int minX, int maxX, int minRow, int maxRow)
    {
        const float Size = 16.0f;
        return new Rect(
            minX / Size, 1.0f - maxRow / Size, (maxX - minX) / Size, (maxRow - minRow) / Size);
    }

    /// Dust, built out of geometry rather than out of a texture transparency: a centre pad and one
    /// arm per connection, all on the flat white tile and coloured by the power tint.
    ///
    /// Bit order matches RedstoneWorld.ConnectionMask - N=1, E=2, S=4, W=8 - and North is +Z while
    /// East is +X, because game-space North is `LocalVector(0, -1, 0)` and the game-to-Unity cast
    /// is `(x, z, -y)`.
    public static Mesh Dust(int mask, BlockAtlas atlas)
    {
        const float Half = 0.09f;
        const float Pad = 0.19f;

        List<BoxMesh.Box> boxes = new List<BoxMesh.Box>
        {
            BoxMesh.Quad(-Pad, -Pad, Pad, Pad, RedstoneCatalog.Solid),
        };

        if ((mask & 1) != 0)
        {
            boxes.Add(BoxMesh.Quad(-Half, Pad, Half, 0.5f, RedstoneCatalog.Solid));
        }

        if ((mask & 2) != 0)
        {
            boxes.Add(BoxMesh.Quad(Pad, -Half, 0.5f, Half, RedstoneCatalog.Solid));
        }

        if ((mask & 4) != 0)
        {
            boxes.Add(BoxMesh.Quad(-Half, -0.5f, Half, -Pad, RedstoneCatalog.Solid));
        }

        if ((mask & 8) != 0)
        {
            boxes.Add(BoxMesh.Quad(-0.5f, -Half, -Pad, Half, RedstoneCatalog.Solid));
        }

        return BoxMesh.Build(boxes, atlas, "RedstoneDust_" + mask);
    }

    /// **Everything directional is built along X.** Rotation is about Unity Y, rotation zero is
    /// identity, and `TileDirection.East` at rotation zero is +X - which is what
    /// `RedstoneWorld.Facing` returns. An earlier version authored the repeater along Z, so it was
    /// drawn ninety degrees from the direction it actually read and powered, and looked like a
    /// repeater that refused to repeat.
    ///
    /// -X is the back (`Behind`: the input, and what a torch mounts on) and +X the front.
    public static Mesh Build(RedstoneDefinition component, BlockAtlas atlas, bool active, int variant = 1)
    {
        string texture = active ? component.ActiveTexture : component.Texture;

        switch (component.Kind)
        {
            case RedstoneKind.Torch when variant >= 2:
                // Mounted: shoved against the block behind it and jutting out, the way a wall
                // torch does. This is the only way to see which block a torch is inverting -
                // rotation is otherwise invisible on a symmetric post.
                return BoxMesh.Build(new[]
                {
                    new BoxMesh.Box(new Vector3(-0.42f, 0.15f, -0.05f), new Vector3(-0.30f, 0.55f, 0.05f), texture, uv: TorchStick),
                    new BoxMesh.Box(new Vector3(-0.38f, 0.5f, -0.09f), new Vector3(-0.18f, 0.7f, 0.09f), texture, uv: TorchHead),
                }, atlas, "RedstoneTorchMounted");

            case RedstoneKind.Torch:
                // Free-standing: upright in the middle of its tile, and a power source.
                return BoxMesh.Build(new[]
                {
                    new BoxMesh.Box(new Vector3(-0.05f, 0.0f, -0.05f), new Vector3(0.05f, 0.5f, 0.05f), texture, uv: TorchStick),
                    new BoxMesh.Box(new Vector3(-0.09f, 0.45f, -0.09f), new Vector3(0.09f, 0.65f, 0.09f), texture, uv: TorchHead),
                }, atlas, "RedstoneTorch");

            case RedstoneKind.Repeater:
            {
                // The fixed torch sits near the output end; the other slides toward the back as
                // the delay grows, one notch per tick, exactly as in Minecraft. Reading the delay
                // off the gap is why this has a mesh per setting at all. The repeater own texture
                // is fully opaque, so it needs no window.
                float movable = 0.1f - 0.12f * Mathf.Clamp(variant, 1, 4);

                // The slab wears the repeater's own texture, which is a **top view** - the groove
                // the movable torch slides along is drawn on it. Painting it smooth stone threw
                // away the only thing that says which way the repeater points.
                return BoxMesh.Build(new[]
                {
                    // Three turns *and* the flip. Together they reproduce exactly what was on
                    // screen while the sign bug was collapsing turns 1 and 3 together, which is the
                    // orientation confirmed correct - so fixing the bug changes nothing here.
                    new BoxMesh.Box(new Vector3(-0.5f, 0.0f, -0.5f), new Vector3(0.5f, 0.12f, 0.5f), "smooth_stone", top: texture, topTurns: 3, topMirror: true),
                    new BoxMesh.Box(new Vector3(0.2f, 0.12f, -0.06f), new Vector3(0.32f, 0.32f, 0.06f), texture),
                    new BoxMesh.Box(new Vector3(movable, 0.12f, -0.06f), new Vector3(movable + 0.12f, 0.32f, 0.06f), texture),
                }.Concat(Arrow(0.12f, 0.55f)).ToArray(), atlas, "RedstoneRepeater_" + variant);
            }

            case RedstoneKind.Lever:
                // Base of stone, handle leaning back when off and forward when on, along the same
                // X axis as everything else.
                return BoxMesh.Build(new[]
                {
                    new BoxMesh.Box(new Vector3(-0.25f, 0.0f, -0.19f), new Vector3(0.25f, 0.1f, 0.19f), "smooth_stone"),
                    active
                        ? new BoxMesh.Box(new Vector3(0.04f, 0.1f, -0.05f), new Vector3(0.22f, 0.42f, 0.05f), texture, uv: LeverStick)
                        : new BoxMesh.Box(new Vector3(-0.22f, 0.1f, -0.05f), new Vector3(-0.04f, 0.42f, 0.05f), texture, uv: LeverStick),
                }, atlas, "Lever");

            case RedstoneKind.Lamp:
                // A full block, like the decoration blocks - and like them, drawn as a whole cube
                // rather than a small model, so its texture is used whole.
                return BoxMesh.Build(new[]
                {
                    new BoxMesh.Box(new Vector3(-0.5f, 0.0f, -0.5f), new Vector3(0.5f, 1.0f, 0.5f), texture),
                }, atlas, "RedstoneLamp");

            case RedstoneKind.Converter:
            {
                // The one component with no Minecraft counterpart, so it is built to read as a
                // *machine* rather than as a block: a pad, an inset body, a lit core and a cap.
                // That is the shape language the cargo mod's buildings use, and the reason they
                // sit next to vanilla machines without looking hand-made.
                //
                // The core carries the state - dark obsidian when idle, redstone when either side
                // is carrying - and stands a little proud of the body so it reads at a distance.
                // The blue nubs are the wire ports and sit on the wire axis only, input at the
                // back and output at the front; the redstone half has no direction, it reaches all
                // four sides like a lever, so nothing marks those. The arrow runs along the same
                // axis as the ports, the way it does on a repeater.
                string core = active ? "redstone_block" : "obsidian";

                return BoxMesh.Build(new[]
                {
                    new BoxMesh.Box(new Vector3(-0.5f, 0.0f, -0.5f), new Vector3(0.5f, 0.1f, 0.5f), "smooth_stone"),
                    new BoxMesh.Box(new Vector3(-0.38f, 0.1f, -0.38f), new Vector3(0.38f, 0.62f, 0.38f), "iron_block"),
                    new BoxMesh.Box(new Vector3(-0.42f, 0.26f, -0.42f), new Vector3(0.42f, 0.42f, 0.42f), core),
                    new BoxMesh.Box(new Vector3(-0.3f, 0.62f, -0.3f), new Vector3(0.3f, 0.72f, 0.3f), "iron_block"),
                    new BoxMesh.Box(new Vector3(-0.5f, 0.16f, -0.14f), new Vector3(-0.36f, 0.4f, 0.14f), "lapis_block"),
                    new BoxMesh.Box(new Vector3(0.36f, 0.16f, -0.14f), new Vector3(0.5f, 0.4f, 0.14f), "lapis_block"),
                }.Concat(Arrow(0.72f, 0.6f)).ToArray(), atlas, "RedstoneConverter");
            }

            case RedstoneKind.Comparator:
            {
                // Three torches on a repeater's slab. The **pair sits at the back**, either side
                // of the input, and the single one at the front rises when the comparator is
                // subtracting - which is how Minecraft shows the mode, and the only way to read it
                // without clicking. The first version had the pair at the front, which put the
                // whole thing back to front against the arrow and the top texture.
                float mode = variant >= 2 ? 0.36f : 0.28f;

                // Same as the repeater: the comparator's texture is a top view, with the back slot
                // drawn at the end its input comes from.
                return BoxMesh.Build(new[]
                {
                    new BoxMesh.Box(new Vector3(-0.5f, 0.0f, -0.5f), new Vector3(0.5f, 0.12f, 0.5f), "smooth_stone", top: texture, topTurns: 3, topMirror: true),
                    new BoxMesh.Box(new Vector3(-0.34f, 0.12f, -0.22f), new Vector3(-0.22f, 0.3f, -0.1f), texture),
                    new BoxMesh.Box(new Vector3(-0.34f, 0.12f, 0.1f), new Vector3(-0.22f, 0.3f, 0.22f), texture),
                    new BoxMesh.Box(new Vector3(0.2f, 0.12f, -0.06f), new Vector3(0.32f, mode, 0.06f), texture),
                }.Concat(Arrow(0.12f, 0.45f)).ToArray(), atlas, "RedstoneComparator_" + variant);
            }

            case RedstoneKind.Observer:
                // A cube with its face on the front and its output end on the back, so which way
                // it is watching is visible. The back plate is the piece that lights.
                return BoxMesh.Build(new[]
                {
                    // The same three turns as the slabs, **without** the flip - which is a half turn
                    // from what has been on screen, and a half turn is what this needed all along.
                    // It could not be asked for until now: the sign bug in BoxMesh made turns 1 and
                    // 3 the same mesh, so every setting tried landed on one of two orientations and
                    // the other two were unreachable.
                    new BoxMesh.Box(new Vector3(-0.5f, 0.0f, -0.5f), new Vector3(0.5f, 1.0f, 0.5f), "observer_side", top: "observer_top", topTurns: 3),
                    new BoxMesh.Box(new Vector3(0.46f, 0.04f, -0.46f), new Vector3(0.5f, 0.96f, 0.46f), "observer_front"),
                    new BoxMesh.Box(new Vector3(-0.5f, 0.04f, -0.46f), new Vector3(-0.46f, 0.96f, 0.46f), active ? "observer_back_on" : "observer_back"),
                }.Concat(Arrow(1.0f, 0.8f, backwards: true)).ToArray(), atlas, "Observer");

            case RedstoneKind.Button:
                // Stone, so fully opaque and needing no window. Pressed is the same plate, flatter.
                return BoxMesh.Build(new[]
                {
                    new BoxMesh.Box(
                        new Vector3(-0.12f, 0.0f, -0.19f),
                        new Vector3(0.12f, active ? 0.05f : 0.1f, 0.19f),
                        texture),
                }, atlas, "StoneButton");

            default:
                return BoxMesh.Plane(texture, atlas, "Redstone_" + component.Id);
        }
    }
}

/// The side panel for a component: what it is doing, and the button that works it.
internal sealed class RedstoneModules : IBuildingModules
{
    public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IBuildingDefinition definition)
    {
        yield break;
    }

    public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IMapModel map, BuildingModel building)
    {
        GlobalTileCoordinate tile = building.Tile_G;

        if (!RedstoneWorld.Instance.TryGet(tile, out RedstoneComponent component))
        {
            yield break;
        }

        yield return new HUDSidePanelModuleInfoText.Data(new RawText(Status(component)));

        // The observer is the one component whose input side is invisible while it is quiet, so
        // the panel names the tile it is looking at. A player who has pointed it the wrong way
        // otherwise sees exactly what a broken observer looks like.
        if (component.Definition.Kind == RedstoneKind.Observer
            && RedstoneWorld.Instance.TryGetWatchedTile(tile, out GlobalTileCoordinate watched))
        {
            yield return new HUDSidePanelModuleInfoText.Data(
                new RawText("Watching the tile at " + watched + " (the face end)."));
        }

        // A lever has to be operable, and shapez has no notion of clicking a building in the
        // world - selection opens the side panel instead, so the panel is where the switch goes.
        // HUDSidePanelModuleGenericButton.Data is (text, action), and the action closes over the
        // tile rather than the component so it keeps working if the component is rebuilt.
        switch (component.Definition.Kind)
        {
            // Every label names the **action**, never the state. A side panel module's text is set
            // once when the panel is built and there is no way to make it live - the same
            // constraint that means no stock module can render a changing number - so a label like
            // "Delay: 2 ticks" is correct exactly until the player presses it, and then lies until
            // they reselect. The state is on the model instead, which does update every frame:
            // the repeater's torch gap, the comparator's back torch, the lever's handle.
            case RedstoneKind.Lever:
                yield return Button("Flip the lever", tile);
                break;

            case RedstoneKind.Button:
                yield return Button("Press", tile);
                break;

            case RedstoneKind.Repeater:
                yield return Button("Increase Delay", tile);
                break;

            case RedstoneKind.Comparator:
                yield return Button("Compare / Subtract Mode", tile);
                break;
        }
    }

    private static IHUDSidePanelModuleData Button(string text, GlobalTileCoordinate tile)
    {
        return new HUDSidePanelModuleGenericButton.Data(
            new RawText(text), () => RedstoneWorld.Instance.Press(tile));
    }

    private static string Status(RedstoneComponent component)
    {
        switch (component.Definition.Kind)
        {
            case RedstoneKind.Dust:
                return "Signal strength: " + component.Power + " / " + RedstoneWorld.MaxPower;
            case RedstoneKind.Torch:
                return component.TorchLitNextTick ? "Lit - powering" : "Held off by the block behind it";
            case RedstoneKind.Repeater:
                return (component.Output > 0 ? "Powered" : "Unpowered")
                       + ", delay " + component.RepeaterDelay;
            case RedstoneKind.Lever:
                return component.LeverOn ? "On" : "Off";
            case RedstoneKind.Button:
                return component.ButtonTicksLeft > 0
                    ? "Pressed - " + component.ButtonTicksLeft + " ticks left"
                    : "Not pressed";
            case RedstoneKind.Lamp:
                return component.Lit ? "Lit" : "Dark";
            case RedstoneKind.Comparator:
                return (component.Subtracting ? "Subtracting" : "Comparing")
                       + ", output " + component.Output;
            case RedstoneKind.Observer:
                return component.PulseTicksLeft > 0 ? "Pulsing" : "Watching - no change";
            case RedstoneKind.Converter:
                return "Wire in: " + (component.WireIsOn ? "on" : "off")
                       + "   Redstone in: " + (component.Lit ? "on" : "off");
            default:
                return string.Empty;
        }
    }
}
