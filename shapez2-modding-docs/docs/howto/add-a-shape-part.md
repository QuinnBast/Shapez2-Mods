# Add a shape part

**Problem.** You want a new shape quadrant type — a fifth alongside circle, square, windmill
and star — and you want it to turn up on the map.

**Solution.** Build a `MetaShapeSubPart` at runtime and add it to the lists inside every
`ShapesConfiguration`. There is no registration API, but the lists are mutable.

> [!NOTE]
> Derived from the game's implementations, not from a sample. There is no shipped sample
> that adds a shape part.

## What already exists

Four shape parts, plus two special ones:

| Code | Part |
| --- | --- |
| `C` `R` `W` `S` | circle, square, windmill, star |
| `P` | pin — `IShapesConfiguration.PinShapePart` |
| `c` | crystal — `IShapesConfiguration.CrystalShapePart` |
| `X` `Y` | the two `ConverterQuad` parts — in the quad configuration, never spawned |
| `G` `H` `F` | **hexagonal mode only** — `RectHex`, `CubeHex`, `FlowerHex` |

`G`, `H` and `F` are the trap in that list. They are not in the quad configuration, so a mod
that enumerates only the mode it is being played in sees them as free, and they are only taken
in the one configuration nobody tests in.

The asset names mislead, and it matters if you are designing against them. Dumped and drawn,
`CubeHex` (`H`) is the **filled 60° cell**, so six of them tile a plain hexagon — it is the
hexagonal equivalent of the square, not of the circle. `RectHex` (`G`) is a **six-pointed star**
with concave tapering arms. `FlowerHex` (`F`) is **six round petals**. Between them they occupy the
three most obvious six-fold silhouettes, which is the thing to know before adding a fourth.

**That is the whole vanilla namespace — eleven letters.** Which leaves `A B E J M N Q U V` free,
plus every lower case letter other than `c`. The eleven are not greppable, but they *are* readable
out of `shapez 2_Data/resources.assets`: a `MetaShapeSubPart` serializes as `m_Name`, a 12-byte
`HighDetailMesh` PPtr, then the code as a 2-byte char, so the byte four past the end of the padded
name is the code. Do that once per game version; enumerate at runtime the rest of the time.

**Windmill is already there.** It came over from shapez 1 with the rest of the set, and `W`
appears as `WuWuWuWu` in `HUDChooseBlueprintIconComponent`'s preset list. Reaching for it as
a new shape collides with a live code.

Shape codes are one byte, case sensitive, and a flat namespace shared with the base game and
every other mod. Colour codes are a separate dictionary, so a part `B` does not collide with
`b` for blue.

> [!WARNING]
> **That table is not the whole namespace, and the rest is not greppable.** Those six appear as
> literals in `HUDChooseBlueprintIconComponent`, which is why they are knowable. Every game
> mode has its *own* `MetaShapesConfiguration` with its own parts, and those are authored
> ScriptableObject data — there is nothing in the assemblies to search. Manufacture mode uses
> `X` and `Y`, for instance, and no amount of reading finds that out.
>
> So enumerate the codes from a running game before choosing one:
>
> ```csharp
> foreach (IShapesConfiguration configuration in gameData.ShapesConfigurations)
>     logger.Info?.Log(new string(configuration.Parts.Select(p => p.Code).ToArray()));
> ```
>
> And **log it when a code you wanted is already present** rather than skipping silently — the
> idempotency check that stops a second session from crashing looks identical to a collision
> from inside the code.

## The part itself

```csharp
MetaShapeSubPart part = ScriptableObject.CreateInstance<MetaShapeSubPart>();

// Nothing else references it, and Resources.UnloadUnusedAssets runs on scene changes.
part.hideFlags = HideFlags.HideAndDontSave;

part._Code = 'G';
part._AllowColor = true;
part._AllowChangingColor = true;
part._DestroyOnFallDown = false;   // only the crystal sets this
part.OverrideMaterial = false;

part.Mesh = lodMeshAsset;               // LODMeshAsset — what the world renderer draws
part.HighDetailMesh = unityMeshRef;     // what the shape viewer dialog draws
```

The underscored names are the serialized fields behind the interface properties; the
[publicizer](../publicizer.md) exposes them.

> [!WARNING]
> **It has to be a real `MetaShapeSubPart`.** `IShapeSubPart` is a four-member interface and
> implementing it looks like the clean option. Both `ShapeItemRenderer.GetCachedSubPartMesh`
> and `HUDShapeViewer.GenerateShapePartMeshes` cast the interface straight back to the
> ScriptableObject to reach the meshes:
>
> ```csharp
> value = PartMeshCache[key] = GenerateShapeSubPartMesh((MetaShapeColor)shapeColor, (MetaShapeSubPart)subPart, lod);
> ```
>
> A custom implementation registers, parses, generates on the map, and then throws
> `InvalidCastException` the first time something draws it — a long way from the cause.
>
> `HighDetailMesh` is read without a null check, so leaving it unset moves the failure into
> the shape viewer instead.

## Registering it

`GameData`'s constructor builds one `ShapesConfiguration` per authored
`MetaShapesConfiguration` and caches it for the process. Everything downstream reads its
parts from there: the session's `StrictShapeDefinitionFactory`, `MapShapeGenerator`,
`UniversalShapeRenderer`, `HUDMapResourcesFilterRow`, `RandomResearchShapeGenerator`.

`ShapesConfiguration` builds each of its lists with `.ToList()` and exposes them as
`IReadOnlyList<IShapeSubPart>` — **a read-only view over a mutable list, not a read-only
collection.** Casting back and adding puts the part into every consumer at once, because
they all hold the same list object:

```csharp
using System.Collections;   // the non-generic IList — see the warning below

foreach (IShapesConfiguration configuration in gameData.ShapesConfigurations)
{
    if (configuration.Parts.Any(p => p.Code == part.Code)) continue;   // see below

    ((IList)configuration.Parts).Add(part);

    // …and one map generation bucket, plus the combined list
    ((IList)configuration.MapGenerationRareParts).Add(part);
    ((IList)configuration.MapGenerationAllParts).Add(part);
}
```

Adding to `Parts` alone registers the code — it parses, it can be built, research can ask for
it — without putting it on the map. The generation buckets are what make it minable.
`MapGenerationAllParts` is a separate concatenated list, so it needs its own `Add`.

> [!WARNING]
> **`List<IShapeSubPart>` is the wrong cast, and it fails silently.** Look at what
> `ShapesConfiguration` actually builds:
>
> ```csharp
> Parts = data.Parts.Select(p => p.Part).ToList();          // List<MetaShapeSubPart>
> MapGenerationAllParts = MapGenerationCommonParts.Concat(…).ToList();   // List<IShapeSubPart>
> ```
>
> `GenerationPart.Part` is the concrete `MetaShapeSubPart`, so `Parts` and the three rarity
> buckets are `List<MetaShapeSubPart>` at runtime. They satisfy the declared
> `IReadOnlyList<IShapeSubPart>` only because that interface is **covariant** —
> `IReadOnlyList<out T>`. `List<T>` is invariant, so `parts is List<IShapeSubPart>` is
> `false` and the cast throws. Meanwhile `MapGenerationAllParts`, built by reading those
> properties back through their interface type, genuinely *is* a `List<IShapeSubPart>` — so
> four of the five lists fail the generic cast and the fifth does not, which is a good way to
> get a fix that half works.
>
> The non-generic `System.Collections.IList` covers both, and `Add(object)` type-checks at
> runtime against whatever the list really holds.
>
> The failure mode is the quiet one: the mod loads, logs nothing alarming, registers
> nothing, and the new shapes simply never appear on the map. Check the cast and log when it
> fails, rather than assuming it.

### Where to do it

There is no event for "game data is ready". Prefix
`GameSessionOrchestrator.Init_3_SavegameAndMode(IContent, IGameData, IGameStartOptions)`:

```csharp
DetourHelper.CreatePrefixHook<GameSessionOrchestrator, IContent, IGameData, IGameStartOptions>(
    (orchestrator, content, gameData, options) =>
        orchestrator.Init_3_SavegameAndMode(content, gameData, options),
    (orchestrator, content, gameData, options) =>
    {
        Register(gameData);
        return (content, gameData, options);
    });
```

It is the stage that resolves the `GameMode`, and so the `ShapesConfiguration`, out of
`IGameData`; everything that caches parts runs after it. `Prepare` is public and reads
better, but it returns `UniTask` — [a prefix hook cannot target a method with a return
value](../hooking.md#createprefixhook-only-works-on-methods-that-return-void), and a postfix
on an async method fires when it hands back its task, at the first `await`.

> [!WARNING]
> **The registration must be idempotent.** It runs per session, and both
> `Init_7_Rendering` and `StrictShapeDefinitionFactory` build a `Dictionary` keyed on
> `Code`. Registering the same code twice is not a duplicate entry, it is a crash on the
> second session load.

## A quadrant has no orientation

Worth knowing before designing a set of parts, because it decides how many codes you need:

```csharp
public struct ShapePart  { public IShapeSubPart Shape; public IShapeColor Color; }
public struct ShapeLayer { public ShapePart[] Parts; }
```

That is the entire model. No rotation field, no nesting, no scale. `ShapeHashParser` confirms it
from the other side: exactly **two characters per part**, one shape code and one colour code, so
there is nowhere to record an orientation in the save format either. `ShapeItemRenderer` derives
part `j`'s angle from `j / partCount * 360` and never stores it.

So **a rotated or mirrored variant of a part is a different part**, with its own code.

### A rotation is not a reflection

This bites when a part is meant to span two quadrants — half of a shape that the player assembles,
say. Two quadrants meeting at an axis are related by a 90° rotation:

```csharp
// FastMatrix.QuaternionByRotation[1] is a positive turn about +Y
rot90(x, z) = (z, -x)
```

For one part type to fill both halves, its own rotated copy would have to be its mirror image —
`rot90(U) = mirror(U)` — which requires `(x, z) ∈ U ⟹ (z, x) ∈ U`, i.e. `U` symmetric about the
quadrant's diagonal. Anything asymmetric fails, so it needs **two** parts, and the second is the
first mirrored about that diagonal: `(x, z) → (z, x)`. Turned by the renderer it then lands as the
mirror of the first.

Check the rotation direction rather than assuming it. Two independent reads agree here: the
quaternion above, and the gap offset, which pushes part `j` along `(sin, cos)` of
`(j + 0.5) / partCount * 360` — putting part 1 in +X/−Z.

## The mesh is the hard part

`ShapeItemRenderer.GenerateShapeSubPartMesh` assigns materials per vertex:

```csharp
if (colors[i].r < 0.05f)  array[i] = EncodeShaderMaterial(ShapeShaderMaterialType.Outline, baseColor);
else                      array[i] = EncodeShaderMaterial(shapeColor.Material, baseColor);
```

So **the mesh's vertex colours are the material assignment.** The dark border around a
quadrant is geometry marked with red at zero — not a shader effect and not a texture.

> [!WARNING]
> **You cannot load a shape part mesh from a model file.** `AssimpToUnityMeshConverter` keeps
> positions, normals and UVs and [has no vertex colour channel at
> all](load-models-and-icons.md#what-the-importer-does-to-your-mesh). A part loaded from an
> `.fbx` arrives with `colors.Length == 0`, and the loop above indexes off the end of it on
> the first draw.
>
> Either set `OverrideMaterial = true`, which paints the whole part one
> `ShapeShaderMaterialType` and gives up the outline, or build the mesh in code.

Building it in code is less work than it sounds, because every vanilla quadrant is a flat
extruded 2D polygon. An outline, an extrusion and a vertex colour per face is the whole job.

### The space to author in

Read off the scale factors in `ShapeItemRenderer.GenerateShapeMesh`:

```csharp
float num  = RendererData.ShapeDimensions2D / 0.37f * 0.5f;   // horizontal
float num2 = 0.1f;                                            // vertical divisor
```

- **Reference radius `0.37`, height `0.1`.** Those divisors are the dimensions the vanilla
  meshes are authored at. A quadrant may reach past the radius — the square's outer corner is
  at `0.37 * √2`.
- **The unrotated quadrant spans +Z to +X.** Part index `j` is rotated by
  `j / partCount * 360` and offset along `(sin, cos)` of its centre angle, so index 0 is
  unrotated and sits between the +Z and +X axes.
- **Clockwise in (x, z) is the front-facing winding for the top face.** Walking the outline
  from +Z round to +X gives that for free.
- One mesh serves every part count. Vanilla reuses the same quadrant meshes for its 6-part
  configurations, leaving wedges that do not meet; match that rather than working around it.

Everything else about the look — how wide the outline is, whether it sits on the top face or
only on the side walls — is authored Unity asset data and **cannot be read out of the
assemblies**. Dump it from the running game instead. The meshes are readable at runtime
because the renderer reads `vertices` and `colors` on every cache miss:

```csharp
foreach (IShapeSubPart subPart in configuration.Parts)
{
    if (subPart is MetaShapeSubPart part && part.Mesh.TryGet(0, out IMeshReference handle))
    {
        Mesh mesh = handle.GetMeshInternal();
        // mesh.vertices, mesh.triangles, mesh.colors — write them out and read them
    }
}
```

### The dark border is grown outward, and it is about 0.043 wide

Every vanilla part puts its coloured face at the designed outline and its black **past** it —
measured off the dumped meshes, the band is 0.041 to 0.045 in the authoring units where the nominal
radius is 0.37:

```
circle     colour to 0.370   black to 0.413
square     colour to 0.502   black to 0.544
CubeHex    colour to 0.395   black to 0.440
```

Carving the border *inward* instead — insetting the cap and filling the gap with black — looks
plausible and is wrong twice: the border comes out thin, and because each part then stops at its own
outline, the gaps the renderer opens between neighbours (`ShapeInnerGap`) stay background-coloured
instead of merging into one thick line. Two neighbours each growing 0.043 across that gap is what
closes it.

**Round the corners.** A mitre pushes a 90° corner out by `width * √2`; vanilla's square corner
grows by 0.042 against a width of 0.043, so its offset is circular. At this width a mitred sharp tip
is a visible thorn.

**If you drop offset points, the band has to walk the outline anyway.** The band between the
outline and its offset is normally one quad per vertex pair, and dropping a folded point makes one
step span several outline vertices. Join only that step's two ends and the band's inner edge becomes
a chord across the notch while the coloured cap still follows the notch — the sliver between them is
in no triangle, so the shape is open and you see through it. Walk every outline vertex the step
passed over. The check that catches this is "does the band's inner boundary cover every outline edge
exactly once", which is a different question from "does the offset self-intersect".

**Drop offset points that fold over.** An outward offset self-intersects wherever the outline has a
notch narrower than twice the border width — and a part that is clean at 90° can fold at 60°, because
the narrower sector compresses every angular feature. The fold is bevel-against-bevel with different
facetted normals, so it z-fights visibly rather than hiding as black-on-black. The fix is one filter:
a valid offset point is exactly `width` from the outline it came from, so drop anything closer than
about `0.97 * width` and let what remains bridge the notch. That is what a true offset does anyway.

**Make it a bevel, not a flat band, or it will z-fight.** Count the flat triangles in a dumped
vanilla part and *none of them are black*: the top face is entirely coloured, and the border is the
sloped surface running outward and **down** from that face's edge — 0.02 down, for a part 0.0955
tall. A flat black band at the face's own height renders perfectly on its own, and then fights every
neighbour, because a border that grows 0.043 across a gap of 0.022 always reaches over the next
part's coloured face and lands in the same plane at the same depth. Sloping it puts a neighbour's
black *under* your colour. The width seen from above is unchanged either way, so a check that only
measures the band cannot catch this — it takes two adjacent parts and a depth buffer.

One trap when checking this against a dump: **vanilla's border is a flange below the top face**, at
y 0.0754 against the face's 0.0954, and wider than the face. Draw only top-face triangles and every
vanilla part renders with no border at all.

### Filling the top face

Fill it by ear clipping the outline, not with a triangle fan from the shape's centre. The fan is
the obvious shortcut and it quietly rules out every part that does not contain the centre — a lens,
a crescent, a whole circle sitting out in the quadrant — by covering the gap with stray triangles.
Ear clipping over a simple polygon is about sixty lines and accepts all of them.

### What stacking does to a silhouette

This is the constraint that decides whether a new part is worth adding at all.

```csharp
float num3 = GetShapeLayerScale(i) * num;          // pow(1 - ShapeLayerScaleReduction, layer)
float num4 = RendererData.ShapeInnerGap * num;     // the same for every layer
```

Each layer is scaled about the shape's centre, and the gap offset is not scaled with it. At the
default reduction of `0.24`, **layer 1 covers the inner 76% of layer 0** — so from above, only the
outer quarter of a part's radius survives having anything stacked on it.

A part distinguished by what happens near the centre is therefore a circle as soon as the player
stacks on it. Put the distinguishing feature — teeth, points, a waist, a notch — in the outer
quarter. `ShapeLayerScaleReduction` is a serialized field with a default, so print the shipped value
rather than trusting `0.24`.

## Hexagonal mode

The game ships **three** `MetaShapesConfiguration` assets, not one:
`DefaultShapesQuadConfiguration` and `OnboardingShapesQuadConfiguration` with `PartCount = 4`,
and `DefaultShapesHexagonalConfiguration` with `PartCount = 6`. Injecting a part into every
configuration — which is the right thing to do, because a code has to be unique across all of
them — therefore puts it into hexagonal mode too, and a quadrant mesh is the wrong shape there.

**A part mesh is authored for exactly `360 / PartCount` degrees, and nothing rescales it.**
`ShapeItemRenderer.GenerateShapeMesh` places part `j` with a Y rotation and a *uniform*
horizontal scale:

```csharp
Angle rotation = Angle.FromDegrees((float)j / (float)parts.Length * 360f);
float x = math.radians(((float)j + 0.5f) / (float)parts.Length * 360f);   // gap direction
combineInstance.transform = Matrix4x4.TRS(
    new Vector3(math.sin(x) * num4, i * RendererData.ShapeLayerHeight, math.cos(x) * num4),
    FastMatrix.RotateYAngle(rotation),
    new Vector3(num3, RendererData.ShapeLayerHeight / num2, num3));
```

There is no angular squash anywhere in that matrix. A 90°-wide mesh dropped into a six-part
shape is placed every 60°, so each part overlaps its two neighbours by 30° and sits 15° off the
gap offset that is supposed to centre it. That is why vanilla has a *separate* part asset per
configuration — `RectQuad` and `RectHex` are different ScriptableObjects that happen to share
nothing but a role, and `CubeHex`, `FlowerHex`, `PinHex`, `CrystalHex` likewise.

The corollary is that the same code can name two different parts in two configurations, and the
renderer is built to expect it. `GameSessionOrchestrator.Init_7_Rendering` seeds its `char ->
IShapeSubPart` dictionary from the *current mode's* configuration and then fills in codes from
the others only `if (!dictionary.ContainsKey(part.Code))` — first wins, on purpose.

**Vanilla relies on that, which is the permission slip for doing it yourself.** `PinQuad` and
`PinHex` are both coded `P`; `CrystalQuad` and `CrystalHex` are both coded `c`. Same code, two
ScriptableObjects, two meshes, and the one whose configuration is being played always wins because
it seeded the dictionary. The `ContainsKey` guard exists for exactly those two pairs — nothing else
in the base game needs it. So registering one `MetaShapeSubPart` per configuration under a single
code is the supported arrangement, not a trick.

Name the objects apart even so (`ShapePart_Gear_6`), because `MetaShapeSubPart` has no other
distinguishing field and two entries reading `ShapePart_Gear` with different meshes is a bad
half hour.

**A correct transform is not the same as a part that still reads as itself.** The transform keeps
the geometry faithful; it cannot keep the *gestalt*, because the gestalt depends on how many copies
are arranged around the circle. A square minus its outer corner is obviously not a square, so four
of them read as a plus. A 60° rhombus minus its outer point is still obviously a rhombus, so six of
them read as — a hexagon of rhombi, which is exactly what vanilla's `RectHex` already is. That was
found by playing, after the geometry had been checked and re-checked on paper.

The fix for that case was to hold the notch's *angular* width constant rather than its fraction of
the cell, so the notch deepens as the sector narrows. The general lesson is to look at a whole
shape of six before believing a part survived the port, and to compare it against the vanilla parts
of that configuration rather than against its own quad version.

**Some collisions are conceptual and no amount of re-parametrising fixes them.** A part built as
"one round lobe per sector" is the same shape as `FlowerHex` at six parts, whatever the lobe's
width, waist or radius — every knob just produces a slightly different round petal. That kind of
clash needs a feature the vanilla part does not have (a cleft tip, a notch, a tooth), not a better
transform. Add it scaled by the sector, and the configuration that has no such vanilla neighbour
keeps the shape it already had.

The way to find these is `esp.dump` — or whatever equivalent writes `MetaShapeSubPart.Mesh` out —
because it walks **every** configuration and so dumps the vanilla parts too. Drawing your part
beside the real mesh is the only check that compares the mod against the game rather than against
itself.

Re-authoring an outline for a different sector is not one transform, which is the thing that
catches people who try it. A radial outline — anything of the form *radius as a function of angle*
— just takes the sector angle as a parameter. An outline authored in the quadrant's **cell**, the
square from the origin out to `(R, R)` that the square part fills, has to go through the sector's
own oblique basis instead, or its straight edges bend. And that basis has to be **normalised**:
two edges of length `R` at 60° span a rhombus whose long diagonal is `1.73R` against the quadrant
cell's `1.41R`, so the raw basis makes every cell-authored part a third too big and pushes it into
its neighbours.

So a mod that wants to support both either builds a second mesh per part at `360 / PartCount`
degrees and registers a separate `MetaShapeSubPart` per configuration, or skips configurations
whose `PartCount` is not 4:

```csharp
foreach (IShapesConfiguration configuration in gameData.ShapesConfigurations)
{
    if (configuration.PartCount != 4)
        continue;   // the mesh is authored for a quadrant
    ...
}
```

`MetaShapesConfiguration.OnValidate` caps `PartCount` at 32 and requires it to be even "so the
half cutter works", so 4 and 6 are conventions, not limits.

## Map generation

`MapShapeGenerator.PickRandomShape` rolls **rare first, then very rare**, then falls through to
common, and each bucket is a flat `rng.Choice`:

```csharp
if (ShapesConfiguration.MapGenerationRareParts.Count > 0 && rng.TestPercentage(parameters.ShapePatchRareShapeLikelinessPercent))
    return rng.Choice(ShapesConfiguration.MapGenerationRareParts);
if (ShapesConfiguration.MapGenerationVeryRareParts.Count > 0 && rng.TestPercentage(parameters.ShapePatchVeryRareShapeLikelinessPercent))
    return rng.Choice(ShapesConfiguration.MapGenerationVeryRareParts);
return rng.Choice(ShapesConfiguration.MapGenerationCommonParts);
```

The order matters for the arithmetic: very rare is rolled only on the 70% that rare declined, so
at the shipped 30 / 10 it is 30% rare, **7%** very rare, 63% common — not 30 / 10 / 60. The two
percentages are authored per scenario, but `RandomResearchShapeGenerator` hardcodes 30 and 10 in
code, so those are the values research goals actually use.

So a part added to a bucket takes an **equal share** of it. Six new rare parts make the
vanilla rares six times scarcer, which is a balance decision, not a detail.

### Which vanilla part is in which bucket

Not all four base parts are common, and the split is the same in every shipped configuration —
two commons, one rare, one very rare:

| Configuration | `PartCount` | Common | Rare | Very rare | Never spawns |
| --- | --- | --- | --- | --- | --- |
| `DefaultShapesQuadConfiguration` | 4 | `C` circle, `R` square | `S` star | `W` windmill | `P` pin, `c` crystal, `X`, `Y` |
| `OnboardingShapesQuadConfiguration` | 4 | `C`, `R` | `S` | `W` | `P`, `c`, `X` |
| `DefaultShapesHexagonalConfiguration` | 6 | `H` hexagon | `G` "rect" | `F` flower | `P` pin, `c` crystal |

So the windmill is the rarest thing on a quad map, at 7% of parts, and the star at 30%.

> [!NOTE]
> This is authored ScriptableObject data, so it is not in the assemblies — it was read out of
> `shapez 2_Data/resources.assets`, where `MetaShapesConfiguration` serializes as `PartCount`,
> the pin and crystal `PPtr`s, then the `Parts` array as (12-byte `PPtr`, 4-byte rarity enum)
> pairs. The pin and crystal entries cross-check the decoding: the ids the array carries with
> rarity `NotSpawned` are the same ids as the two named fields.
>
> A game update rewrites that file. Print the buckets from a
> [console command](console-command.md) before relying on the table.

The same generator drives `RandomResearchShapeGenerator`, so a part that spawns on the map
will also appear in research goals.

## Gotchas

- **`AffectsSaveGames: true`.** A mined shape puts the code into the save. Without the mod,
  `ShapeHashParser` throws on the unknown code and the save will not load.
- **Nothing names a shape part.** `MetaShapeSubPart` has no title field and no UI shows one,
  so a new part needs no [translations](add-translations.md).
- **No behaviour to implement.** `ShapeOperationCut`, `ShapeOperationStack` and the rest never
  look at which part they are holding. The only parts anything special-cases are
  `PinShapePart` and `CrystalShapePart`, both named fields on `IShapesConfiguration`.
- **Two bits of UI draw one entry per part, and adding ten overflows at least one of them.**
  `HUDShapeCodesPreview` builds the shape viewer's "Parts" and "Colors" legend by walking
  `ShapesConfiguration.Parts` — which with a mod installed is eighteen entries in the quad
  configuration rather than eight — into a parent that lays them out in a single row. The extras
  run off the side and over the controls beside them. **Confirmed in game**, not predicted.
  Swapping that parent's layout for a `GridLayoutGroup` with `Constraint.Flexible`, plus a
  `ContentSizeFitter` whose vertical fit is `PreferredSize` and whose horizontal fit is explicitly
  `Unconstrained`, wraps it to the panel width. **The old layout group has to be
  `DestroyImmediate`d first**: Unity refuses to add a second `LayoutGroup` to a GameObject at all —
  it logs `Can't add 'GridLayoutGroup' … because a 'HorizontalLayoutGroup' is already added` and
  `AddComponent` then returns **null** rather than throwing, so the next property set is what
  actually fails. Disabling the old one is not enough, and plain `Destroy` is deferred to the end of
  the frame, which is too late for an `AddComponent` on the next line. There is no HUD rewirer in
  ShapezShifter, so it takes a MonoMod postfix on `HUDShapeCodesPreview.Construct` — which is safe
  to hook, the class not being generic.
  `HUDMapResourcesFilterRow` builds a row per part in `MapGenerationAllParts` the same way and may
  overflow too; that one is still a prediction.
- Keep a reference to the ScriptableObjects you create, and set
  `HideFlags.HideAndDontSave`. `Resources.UnloadUnusedAssets` runs on scene changes and will
  collect an unreferenced one, leaving the registered part pointing at a destroyed Unity
  object.
