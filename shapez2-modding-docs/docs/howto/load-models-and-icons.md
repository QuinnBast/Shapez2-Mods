# Load models and icons

**Problem.** Your building needs a mesh, an icon, and possibly a Unity asset bundle —
all loaded from your own mod folder at runtime.

**Solution.** `ShapezShifter.Kit` has a loader for each, and `ModDirectoryLocator`
finds your files without you hard-coding paths.

## Find your own files

```csharp
using ShapezShifter.Kit;

ModFolderLocator resources = ModDirectoryLocator.CreateLocator<MyMod>().SubLocator("Resources");

string iconPath = resources.SubPath("MyCutter_Icon.png");
string meshPath = resources.SubPath("MyCutter.fbx");
```

`CreateLocator<T>()` resolves the folder of the assembly that `T` lives in, so it works
wherever the player's mod folder is. Never build paths from `SPZ2_PERSISTENT` at runtime
— that is a build-time variable.

> [!WARNING]
> **This throws under a hot-reloader.** `CreateLocator<T>()` is
> `Directory.GetParent(typeof(T).Assembly.Location)`, and a reloader has to load with
> `Assembly.Load(byte[])` — `LoadFrom` binds by assembly identity and would just hand back
> the copy already loaded, which is precisely why the byte-array overload is needed. An
> assembly with no file behind it reports `Location == ""`, so `GetParent("")` throws
> `ArgumentException: Path cannot be the empty string`. The throw lands in your mod's
> constructor, so the reload reports the mod as dead with no obvious cause.
>
> This affects **every mod that loads an icon off disk**, which is every mod with a
> toolbar entry. Guard it:
>
> ```csharp
> string location = typeof(MyMod).Assembly.Location;
> ModFolderLocator resources = !string.IsNullOrEmpty(location)
>     ? ModDirectoryLocator.CreateLocator<MyMod>().SubLocator("Resources")
>     : new ModFolderLocator(Path.Combine(
>         Application.persistentDataPath, "mods-dev", "MyMod")).SubLocator("Resources");
> ```
>
> `Application.persistentDataPath` is the same folder `SPZ2_PERSISTENT` points at, and is
> safe to read at runtime — unlike the build-time variable. Prefer `mods-dev` over `mods`
> in the fallback, since that is where a reloader stages from.

Copy resources to the output:

```xml
<None Update="Resources/*">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

## Icons

```csharp
Sprite icon = FileTextureLoader.LoadTextureAsSprite(iconPath, out _);

BuildingGroup.Create(groupId).WithIcon(icon);
```

The `out` parameter hands back the underlying texture, which the samples discard with
`out _`. Keep it if you need to dispose or reuse the texture yourself.

### What an icon should look like

There is no API for this and no documentation in the game, but the shipped sample icons
agree with each other closely enough to read as a house style. All of
`BiggerPlatforms/Resources/Foundation_*.png`, `SandboxIslands/Resources/FluidTrash.png`
and `DiagonalCutter/Resources/DiagonalCutter_Icon.png` are:

- **512 x 512, RGBA, transparent background**, with the drawing inset roughly 20px.
- **Flat and schematic — not a render of the model.** `Foundation_4x4` is a top-down grid
  of rounded squares; `DiagonalCutter` is two quarter-discs and two crosses. Rendering
  your `.fbx` from a nice angle is the obvious idea and it produces an icon that looks
  like it came from somewhere else, and that turns to mush at toolbar size.
- **White-to-light-grey for structure, one saturated accent for meaning.** The game uses
  blue for fluid (`FluidTrash`) and orange for shape operations (`DiagonalCutter`).
- **Heavily outlined,** with a soft dark halo underneath. Note that the outline is
  *per shape*, not just around the silhouette — every square in `Foundation_4x4` has its
  own dark edge, which is what stops the grid reading as one blob.

Coverage runs roughly 25–80% of the canvas. Much below that and the icon is lost in the
toolbar; much above and the silhouette stops carrying information.

If you are generating icons rather than drawing them, two things are worth knowing: draw
at 4x and downsample with `LANCZOS`, because PIL-style primitives are hard-edged; and
dilate outline masks by blurring and thresholding rather than with a true max filter,
which at these radii is orders of magnitude slower for an indistinguishable result.

### Draw your icon out of the game's own

A hand-drawn icon sits in a toolbar next to tobspr's rendered ones and looks like what it
is. The game's icons are ordinary sprites in `resources.assets`, so the better starting
point is usually to pull the relevant ones out and build yours from them.

They are named predictably. Island layout icons are `IslandLayout<Name>` —
`IslandLayoutSpaceBelt`, `IslandLayoutSpacePipe` — and building icons follow their
building id. [UnityPy](https://github.com/K0lb3/UnityPy) reads them without Unity:

```python
import UnityPy

env = UnityPy.load(r"...\shapez 2_Data\resources.assets")
esources.assets")

for obj in env.objects:
    if obj.type.name == "Sprite":
        data = obj.read()
        if data.m_Name == "IslandLayoutSpaceBelt":
            data.image.save("SpaceBelt.png")
```

`data.image` is the sprite already cropped out of its atlas, alpha intact, so there is no
rect arithmetic to do. Both of the space path icons come out around 100x125 and are
full-bleed: a vertical run filling the tile, flat vector shapes with a dark outline.

That last part is the thing to design around. Two of those tiles laid over each other do
not read as a crossing — the top one simply hides the bottom one. Narrowing each to about
half the canvas before crossing them leaves the far ends of both visible, which is what
makes the shape legible at the 48-64px the toolbar actually draws. **Check your icon at
that size on a dark background, not at full resolution** — silhouette survives the
downscale and interior detail does not.

Do this offline and ship the result as a PNG. The icons could be composed at runtime
instead, but the sprites are atlas-packed without a read/write flag, so `GetPixels`
throws and it takes a `RenderTexture` blit and `ReadPixels` to get at them — a lot of
machinery, run on every session, for a picture that never changes.

## Meshes

```csharp
Mesh baseMesh = FileMeshLoader.LoadSingleMeshFromFile(meshPath);
LOD6Mesh lod = MeshLod.Create().AddLod0Mesh(baseMesh).BuildLod6Mesh();
```

Loading goes through AssimpNet, which ships with ShapezShifter, so `.fbx`, `.obj`, `.dae`
and the rest of Assimp's list all work. `.obj` is worth knowing about: it is plain text,
so it can be generated by a script and diffed in review.

The game renders at six levels of detail. `AddLod0Mesh(mesh).BuildLod6Mesh()` supplies
one mesh and lets it stand in for every level — fine for a small building, wasteful for
anything large or numerous. `ILodMeshBuilder` has `AddLod1Mesh` … `AddLod5Mesh` for
supplying progressively simpler meshes.

> [!WARNING]
> **`BuildLod6Mesh` mismaps the upper slots.** It assigns `MeshAt(3)` to `LODMinimal`,
> `MeshAt(4)` to `LODOverview` and `MeshAt(5)` to `LODReduced`, which is not the order
> `LOD6Mesh.ComputeMeshForLOD` reads them back in (3 is `LODReduced`, 4 `LODMinimal`,
> 5 `LODOverview`). Supplying one mesh hides this completely — `MeshAt` falls back to the
> last entry, so every slot gets the same mesh — and supplying a real ladder walks into
> it. Check what you get at distance before trusting `AddLod3Mesh` and up.

### What the importer does to your mesh

`AssimpToUnityMeshConverter` is not a straight copy, and three of its choices decide how
you have to author:

- **One mesh per file.** `LoadSingleMeshFromFile` ends in `.Meshes().Single()`, so a file
  that Assimp splits into two — most often because it has two objects or two materials —
  throws rather than drawing half. Export one object with one material.
- **X is negated and winding is reversed.** Every vertex and normal is written as
  `float3(-x, y, z)` and every triangle's indices are emitted `2, 1, 0`. Together that is
  a consistent handedness flip, which is correct and leaves faces pointing outward — but
  the model does arrive **mirrored along X**. For anything symmetrical that is invisible;
  for a machine with an intake at one end and an outlet at the other it is exactly
  backwards. Mirror it in your exporter, and if you mirror it by negating X yourself,
  reverse your triangle order too or you ship a file that is inside-out as geometry.
- **Only positions, normals and UVs survive.** There is no vertex colour channel in the
  converter at all — which rules the importer out entirely for
  [shape parts](add-a-shape-part.md#the-mesh-is-the-hard-part), whose vertex colours *are*
  their material assignment — and only triangles are kept (`IndexCount == 3`; Assimp's
  `TargetRealTimeMaximumQuality` preset triangulates first, so quads are fine going in).

### Colour comes from a texture atlas, not from your material

This is the one that wastes an afternoon. The shipped sample `DiagonalCutter.fbx` carries
a `base_color_texture` and its UV0 occupies a tight sub-rectangle —
`U[0.095, 0.476]`, `V[0.587, 0.919]`, 319 distinct pairs on a 1061-vertex mesh. Those are
not surface UVs in any ordinary sense; the mesh is **picking colours out of one shared
image by pointing at spots in it**.

So a mesh exported without UVs, or with UVs from an unwrap, does not come out untextured —
it comes out sampling whatever happens to sit at those coordinates. Give every face of a
part the same UV and that part is one flat colour, which is a good place to start.

The atlas is authored Unity asset data and is not in the decompiled assemblies, so there
is no way to read the coordinates statically. Get them off the running game instead: the
material's textures can be dumped to PNG from a console command, blitting through a
`RenderTexture` because a shipped texture is not CPU-readable —

```csharp
RenderTexture target = RenderTexture.GetTemporary(
    texture.width, texture.height, 0, RenderTextureFormat.ARGB32,
    RenderTextureReadWrite.sRGB);
Graphics.Blit(texture, target);            // GetPixels on the texture itself would throw
RenderTexture.active = target;
Texture2D readable = new(texture.width, texture.height, TextureFormat.RGBA32, false);
readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
readable.Apply();
File.WriteAllBytes(path, readable.EncodeToPNG());
```

`material.GetTexturePropertyNames()` enumerates what there is to dump. Remember UV origin
is bottom-left while PNG row 0 is the top, so `V = 1 - (row / height)`.

### What scale to author at

One tile is **one unit**, and Y is up — `DiagonalCutter.fbx`, a single-tile building, is
`X[-0.5, 0.5] Y[0, 0.26] Z[-0.479, 0.375]` and sits on `Y = 0`.

A chunk is 20 tiles, so a one-chunk island platform is **20 × 20 units**, centred on the
island's origin, with its deck at `Y = 0`. That falls out of
`GlobalChunkCoordinate.ToCenter_W` returning `(x*20 + 9.5, y*20 + 9.5, z*20 + offset)` and
`WorldCoordinate`'s cast to `Vector3` being `(x, z, -y)` — the game's own space is Z-up and
only becomes Y-up at the renderer, which is why the vertical offset lands in the third
slot. See [Coordinate Systems](../coordinates.md).

## Wiring a mesh into draw data

```csharp
private static BuildingDrawData CreateDrawData(ModFolderLocator resources)
{
    Mesh baseMesh = FileMeshLoader.LoadSingleMeshFromFile(resources.SubPath("MyCutter.fbx"));
    LOD6Mesh lod = MeshLod.Create().AddLod0Mesh(baseMesh).BuildLod6Mesh();

    return new BuildingDrawData(
        renderVoidBelow: false,
        new ILODMesh[] { lod, lod, lod },     // per-variant meshes
        lod,                                   // …and the remaining slots
        lod,
        lod.LODClose,
        new LODEmptyMesh(),
        BoundingBoxHelper.CreateBasicCollider(baseMesh),
        new MyDrawData(),
        false,
        null,
        false);
}
```

`BuildingDrawData` takes a long positional argument list with no named-parameter help
beyond the first — copy the shape from `DiagonalCutterMod.CreateDrawData` and substitute
your meshes rather than working out each slot from scratch.

`BoundingBoxHelper.CreateBasicCollider(mesh)` derives a collider from the mesh, which is
what you want unless the visual mesh is a poor proxy for the footprint.

## Asset bundles

For content authored in Unity (prefabs, materials, shaders) rather than a bare mesh:

```csharp
using var assetBundleHelper =
    AssetBundleHelper.CreateForAssetBundleEmbeddedWithMod<MyMod>("Resources/MyBundle");
```

Note the `using` — it is disposable, and the samples scope it to the constructor where
the content is registered. The path is relative to your mod folder and omits the
extension; a `.manifest` file sits alongside the bundle.

`MaterialHelper` and `ShaderHelper` are the companions for pulling materials and shaders
out of a bundle or off the game's own theme.

## Gotchas

- **Load in the constructor, not per frame.** Mesh and texture loading is expensive and
  the results are meant to live for the process.
- Missing files throw at mod load, which is the good failure — a mod that fails loudly
  at startup beats one that renders nothing. Do not swallow these exceptions.
- Asset bundles are Unity-version-sensitive. A bundle built against a different Unity
  version than the game ships will fail to load, and the error will not be obvious.
- `Resources/*` in the csproj is not recursive. Sub-folders need their own entry.
