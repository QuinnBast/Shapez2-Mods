# Extra shape parts - design notes

What was read out of the decompiled assemblies rather than assumed, so none of it has to be
re-derived.

## The idea

shapez 2 ships four shape quadrant types - circle `C`, square `R`, windmill `W`, star `S` - plus
the pin `P` and the crystal `c`. This adds more, and puts them into map generation so they can be
mined rather than only crystallised into existence.

**Windmill is already in shapez 2.** It came over from shapez 1 with the rest; `W` is a live code,
used in `HUDChooseBlueprintIconComponent`'s `WuWuWuWu` preset. Adding it again would collide.

## Status: ten parts, hexagonal 2026-09-13

Compiles clean and installs to `mods/ExtraShapeParts`. Everything below marked *verified* was read
out of the assemblies or out of `resources.assets`.

Gear `E`, Cross `K`, Bar `I`, Diamond `D`, Dot `O`, Dome `M`, Wedge `T`, Sawblade `Z`,
Flower `B`, Leaf `L`.

Two changes landed on 2026-09-13, both driven by the same discovery - that the shape code
namespace and the shape *geometry* both span every configuration, not just the one being played.

**Gear, Dome and Flower were recoded**, from `G`, `H` and `F`, which are taken by
`DefaultShapesHexagonalConfiguration`.

**Hexagonal mode is supported.** Every outline is now a function of its sector angle and each part
is built once per distinct `PartCount`, so the quad configurations get a 90 degree mesh and the
hexagonal one gets its own at 60. Quad geometry is unchanged.

**The seven side quest chains are built.** Same story: a quest stores a repeating layer pattern
rather than a shape code, so the chains work at any part count.

The mod was never published to the Workshop (`Steam/base.vdf` still carries `publishedfileid "0"`),
so the only saves the recode can break are local ones - **a save that has mined a `G`, `H` or `F`
shape will not load against this build**, because `AffectsSaveGames` is true and `ShapeHashParser`
throws on a code that no longer resolves. Start a new save, or roll back to a build from before the
rename to mine those out.

Ten parts against vanilla's four. That is a lot, and the rarity split is the only lever holding it
back from swamping the map - read `esp.report` before deciding it is balanced.

Seven shapes have been cut, and the pattern in *why* is worth reading:

- **Ring** and **Bevel** failed the stacking test below.
- **Big diamond** (a triangular quadrant making one large rotated square) and **Arrowhead** (a
  chevron on the diagonal, four of which made a four-pointed star) read as shapes the game already
  has. Arrowhead was confirmed against the vanilla `S` in game.
- **Arrow right** and **Arrow left** - a whole arrow split down its own axis so two quadrants make
  one arrow across half the shape - also read as something the game already has. The geometry did
  exactly what it was meant to; it just landed on an existing silhouette.
- **Battlement** was never judged - square notches against Gear's round teeth looked like too thin a
  difference to spend a code on.

**Diamond** and **Dot** were briefly cut on the grounds that a whole circle inside one quadrant is
a *squashed circle* and a diamond is a squashed square, so they belonged to a separate squashing
mod. That mod was abandoned and both are back in. The rule they were cut under no longer applies.

Three of the four cuts were for resembling a vanilla shape, which is the failure mode to design
against here - not legibility, not stacking. The map already has a circle, a square, a windmill and
a star, and four quadrants of anything simple tends to land on one of them.

## Publishing

`Steam/` follows the same layout as the other mods here: `SteamPublish.sh` copied verbatim (it
resolves the account from steamcmd's own cached login, so there is nothing to edit), `base.vdf` with
`publishedfileid "0"` so the first run *creates* the item, and `visibility "2"` so it starts private.

```bash
dotnet build                      # game closed - installs to mods/
dotnet build -t:SteamPublish      # uploads whatever is at OutputPath, it does not build it
```

**`Steam/preview.png` is generated, not photographed.** `Screenshots/render.py` draws shape codes
to PNG with Pillow, using the same outline maths, quadrant placement, inner gap and per-layer scale
the mod feeds the game - the part profiles are transcribed from `ExtraShapePartCatalog`. Re-run it
after changing a shape:

```bash
python Screenshots/render.py     # writes Steam/preview.png, Screenshots/patterns.png, parts.png
```

What it is not: a screenshot. The game shades an extruded mesh and these are flat fills, and the
colours are approximations because the palette is authored Unity data. Replace them with real
captures if the flat look is wrong for the store page.

**It is still the only image the manifest carries.** It is the only image the
manifest carries; extra screenshots and the category tags are set on the item's web page afterwards,
because `workshop_build_item` has no key for either. See
[`publish-to-workshop.md`](../shapez2-modding-docs/docs/howto/publish-to-workshop.md).

After the first successful run the script writes the assigned id back into `base.vdf`, so every
later publish updates the same item. Flip `visibility` to `0` by hand when it is ready to be public.

### Shape codes for the store shots

The parts can be typed straight into a sandbox shape producer. Ten plain ones -
`EuEuEuEu`, `XuXuXuXu`, `IuIuIuIu`, `DuDuDuDu`, `OuOuOuOu`, `MuMuMuMu`, `TuTuTuTu`, `ZuZuZuZu`,
`BuBuBuBu`, `LuLuLuLu` - plus layered and coloured ones that photograph better:

| Code | Layers | What it makes |
| --- | --- | --- |
| `MrMrMrMr:MyMyMyMy:MgMgMgMg:MbMbMbMb` | 4 | nested domes, all turning one way |
| `TrTrTrTr:MwMwMwMw:TbTbTbTb:MwMwMwMw` | 4 | wedge and dome alternating, so the spin reverses each layer |
| `CrCrCrCr:CwCwCwCw:CrCrCrCr:OwOwOwOw` | 4 | bullseye, dots on top |
| `EuEuEuEu:EyEyEyEy:ErErErEr` | 3 | three gears nested, teeth stepping inwards |
| `EuEuEuEu:ZrZrZrZr:EwEwEwEw` | 3 | gear, sawblade, gear |
| `BmBmBmBm:BrBrBrBr:ByByByBy` | 3 | flower three deep |
| `LgLgLgLg:LyLyLyLy:LrLrLrLr` | 3 | leaves three deep |
| `LwLwLwLw:KcKcKcKc:OwOwOwOw` | 3 | leaves, cross, dots |
| `OcOcOcOc:OmOmOmOm:OyOyOyOy` | 3 | twelve dots spiralling in |
| `IuIuIuIu:IwIwIwIw:IuIuIuIu` | 3 | bars stacked into a turbine |
| `MbMbMbMb:TyTyTyTy` | 2 | Dome under Wedge - the two turn opposite ways |
| `ZuZuZuZu:OrOrOrOr` | 2 | sawblade with four dots riding on it |
| `ZuZuZuZu:IyIyIyIy` | 2 | sawblade under gold bars |
| `LgLgLgLg:KwKwKwKw` | 2 | leaves on the diagonals, cross above |
| `IcIcIcIc:KmKmKmKm` | 2 | bars under a cross |
| `CwCwCwCw:OmOmOmOm` | 2 | white circle, magenta dots |
| `DwDwDwDw:OmOmOmOm` | 2 | diamond under dots - the straight and round pair |
| `EuKuDuMu:TuZuBuLu` | 2 | all ten, two layers |
| `CuRuCuRu:EuDuMuLu` | 2 | vanilla underneath, new on top |
| `DrDwDrDw` | 1 | diamonds alternating colour |
| `MwMbMwMb` | 1 | domes alternating colour |
| `ErKgDbMy` | 1 | four parts, four colours |
| `EuKuDuMu` | 1 | four parts, plain |
| `MuMu----` | 1 | half a shape, for showing a cut |

Four layers is the cap used here. The real limit is `Mode.MaxShapeLayers`, which is authored per
game mode and not readable from the assemblies, so it may be higher.

## The catalogue list has to be lazy

The arrow pair shipped a crash, and it is worth recording because the cause has nothing to do with
shapes.

`ExtraShapePartCatalog.All` was a static field initialiser declared at the top of the class. The
arrow halves shared one `static readonly Vector2[] ArrowHalf` declared further down. **Static field
initialisers run in declaration order**, so `All` ran first, called `ArrowRight()`, and read
`ArrowHalf` while it was still null:

```
System.TypeInitializationException: The type initializer for
'ExtraShapeParts.ExtraShapePartCatalog' threw an exception.
 ---> System.ArgumentNullException: Value cannot be null. Parameter name: source
  at System.Linq.Enumerable.ToArray[TSource] (...)
  at ExtraShapeParts.ShapePartProfile..ctor (...)
  at ExtraShapeParts.ExtraShapePartCatalog.ArrowRight ()
  at ExtraShapeParts.ExtraShapePartCatalog..cctor ()
```

Two things make it worse than an ordinary null:

- A throw inside a type initialiser surfaces as `TypeInitializationException` at the point of first
  *use*, so the stack points at the mod constructor rather than at the declaration order.
- `ModLoader` does not contain it. The exception came out through `ModLoadingStep.LoadMods` and took
  the game's whole mod loading step down - not just this mod.

`All` is now a property that builds on first access (`Built ??= ...`), which cannot be reached
before every field is initialised regardless of declaration order, and `ShapePartProfile`'s
constructor rejects a null outline by name so the next one says which profile.

The parts that read a shared static array are gone with the arrows, so the immediate fault is gone
twice over - but removal is not a fix, and the next shared outline would have walked into it again.

Note that a hot reload is **not** enough to see a geometry change: the parts are already registered
in the shape configuration under the same codes, and the idempotency check that stops a second
session from crashing also stops the new meshes from replacing the old ones. Restart the game.

## A rotation is not a reflection

Kept because **Wedge** rests on it, though it was first derived for the arrow pair that is now gone.

The renderer only ever *rotates* a part - part `j` is turned by `j / partCount * 360` - and there is
nowhere in `ShapePart` to record anything else. A rotation cannot produce a mirror image, so the
handedness of a shape has to be built into its outline.

Two quadrants that meet at an axis are related by a 90 degree rotation. `FastMatrix`'s
`QuaternionByRotation[1]` is a positive turn about +Y, so `rot90(x, z) = (z, -x)` - which the gap
offset confirms independently, since part `j` is pushed along `(sin, cos)` of
`(j + 0.5) / parts * 360` and part 1 therefore lands in +X/-Z.

For one part to serve as its own mirror image across that axis:

```
rot90(U) = mirror(U)  requires  (x, z) in U  =>  (z, x) in U
```

- which is to say U must be symmetric about the quadrant's diagonal. Anything chiral fails.

The useful consequence: **mirroring an outline about the quadrant's diagonal, `(x, z) -> (z, x)`,
reverses the handedness of the pinwheel four of them make.** Dome lies along the +Z axis and bulges
towards +X; Wedge is that mirror with a straight edge instead of an arc, so the two turn opposite
ways. Wedge is also the thinner of the two - 0.25 R&sup2; against Dome's 0.42 R&sup2; - because a
chunky triangle per quadrant is what the vanilla windmill already is.

## How a shape part is put together

`MetaShapeSubPart` is a ScriptableObject carrying a code character, four flags and two meshes.
Everything else about a part - how it cuts, stacks, collapses, paints - is generic code driven by
`ShapePart.Shape` being non-null. There is no per-shape behaviour to implement.

*Verified.* `ShapeOperationCut`, `ShapeOperationStack` and the rest never look at which part they
are holding. The only parts anything special-cases are `PinShapePart` and `CrystalShapePart`, both
of which are named fields on `IShapesConfiguration`.

## Why the part has to be a real `MetaShapeSubPart`

`IShapeSubPart` is an interface with four members and it is tempting to implement it. Do not:

```csharp
// ShapeItemRenderer.GetCachedSubPartMesh
value = PartMeshCache[key] = GenerateShapeSubPartMesh((MetaShapeColor)shapeColor, (MetaShapeSubPart)subPart, lod);
```

Both the renderer and `HUDShapeViewer.GenerateShapePartMeshes` hard cast back to the
ScriptableObject to reach `Mesh` and `HighDetailMesh`. A custom implementation registers, parses,
generates on the map, and then throws `InvalidCastException` the first time something draws it.

`ScriptableObject.CreateInstance<MetaShapeSubPart>()` is fine - the cast is to the type, not to an
asset loaded from disk.

## Where the parts get registered

There is no registration API. The chain is:

1. `GameData`'s constructor builds one `ShapesConfiguration` per authored `MetaShapesConfiguration`
   and caches it for the process.
2. `ShapesConfiguration` builds `Parts`, `MapGenerationCommonParts`, `MapGenerationRareParts`,
   `MapGenerationVeryRareParts` and `MapGenerationAllParts` with `.ToList()`, exposing each as
   `IReadOnlyList<IShapeSubPart>`.
3. Every consumer reads from there: the session's `StrictShapeDefinitionFactory`, `MapShapeGenerator`
   (through `PickRandomShape`), `UniversalShapeRenderer`, `HUDMapResourcesFilterRow`,
   `RandomResearchShapeGenerator`.

**`IReadOnlyList` over a `List` is a read-only *view*, not a read-only collection.** Casting back
and adding puts the part into every one of those consumers at once, because they all hold the same
list object. That is the whole mechanism - one cast, five `Add` calls.

### The cast has to be non-generic `IList`

This cost a session. The first version cast to `List<IShapeSubPart>`, which is wrong:

```csharp
Parts = data.Parts.Select(p => p.Part).ToList();                        // List<MetaShapeSubPart>
MapGenerationAllParts = MapGenerationCommonParts.Concat(...).ToList();  // List<IShapeSubPart>
```

`GenerationPart.Part` is the concrete `MetaShapeSubPart`, so `Parts` and the three rarity buckets
are `List<MetaShapeSubPart>` at runtime. They satisfy the declared `IReadOnlyList<IShapeSubPart>`
only because that interface is **covariant**; `List<T>` is not, so `is List<IShapeSubPart>` is
false. `MapGenerationAllParts` is built by reading those properties back through their interface
type and genuinely is a `List<IShapeSubPart>` - so four of the five lists fail the generic cast and
the fifth does not.

Non-generic `System.Collections.IList` covers both, and `Add(object)` type-checks at runtime.

`ShapePartInjector.AsMutable` checks the cast and logs instead of throwing, which is the only reason
this was diagnosable: the mod loaded, said "6 parts built", and registered nothing. The log line
`ShapesConfiguration.Parts is no longer a List<IShapeSubPart> (it is List`1)` is what gave it away -
the mangled name being `List`1` with no type argument printed was the clue that it was a `List` of
something else.

## Where the injection happens

`GameSessionOrchestrator.Init_3_SavegameAndMode(IContent, IGameData, IGameStartOptions)`, prefixed.

It is the session stage that resolves the `GameMode`, and so the `ShapesConfiguration`, out of
`IGameData`. Everything that caches parts runs after it - `StrictShapeDefinitionFactory` and
`MapShapeGenerator` in stage 6, `UniversalShapeRenderer` in stage 7.

It is private (the publicizer handles that) and returns void, which `DetourHelper.CreatePrefixHook`
requires. `GameSessionOrchestrator.Prepare` is public and would read better, but it returns
`UniTask`: a prefix hook cannot target it, and a postfix on an async method runs when the method
hands back its task, which is the first `await`, not the end.

**Injection is per session and must stay idempotent.** `Init_7_Rendering` and
`StrictShapeDefinitionFactory` both build a `Dictionary` keyed on `Code`, so registering the same
code twice is a hard crash on the second session load, not a duplicate entry.

## The mesh has to be generated, not loaded

This is the constraint that shapes the whole mod.

`ShapeItemRenderer.GenerateShapeSubPartMesh` decides materials per vertex:

```csharp
if (colors[i].r < 0.05f)   array[i] = EncodeShaderMaterial(ShapeShaderMaterialType.Outline, baseColor);
else                       array[i] = EncodeShaderMaterial(shapeColor.Material, baseColor);
```

So **the mesh's vertex colours are the material assignment**, and the dark border around a quadrant
is geometry marked with red at zero - not a shader effect, not a texture.

ShapezShifter's importer cannot carry that. `AssimpToUnityMeshConverter` keeps positions, normals
and UVs and has no vertex colour channel at all, which the modding docs already note for buildings.
A part loaded from an `.fbx` therefore arrives with `colors.Length == 0` and the renderer indexes
off the end of it on the first draw.

Two ways out: set `OverrideMaterial = true`, which paints the whole part one material and loses the
outline; or build the mesh in code. This mod builds it in code, from a 2D outline, which also means
a new shape is five lines in `ExtraShapePartCatalog` rather than a modelling session.

## The space a sub-part mesh is authored in

*Verified,* from the scale factors in `ShapeItemRenderer.GenerateShapeMesh`:

```csharp
float num  = RendererData.ShapeDimensions2D / 0.37f * 0.5f;   // horizontal
float num2 = 0.1f;                                            // vertical divisor
```

- **Reference radius 0.37, height 0.1.** Those two divisors are the dimensions the vanilla meshes
  are authored at. A quadrant may reach past 0.37 - the square's outer corner is at `0.37 * sqrt(2)`.
- **The unrotated quadrant spans +Z to +X.** Part index `j` is rotated by `j / partCount * 360` and
  offset along `(sin, cos)` of the part's centre angle, so index 0 is unrotated and sits between
  the +Z and +X axes.
- **Clockwise in (x, z) is the front-facing winding for the top face.** Walking the outline from +Z
  round to +X gives that for free.

## Stacking is what decides whether a shape is worth adding

*Verified,* from `ShapeItemRenderer.GenerateShapeMesh`:

```csharp
float num3 = GetShapeLayerScale(i) * num;          // pow(1 - ShapeLayerScaleReduction, layer)
float num4 = RendererData.ShapeInnerGap * num;     // the same for every layer
```

Each layer is scaled about the shape's centre by `(1 - 0.24)^layer`, and the gap offset is *not*
scaled with it. So layer 1 covers the inner **76%** of layer 0, and from above only the outer
quarter of the radius survives.

**A new part therefore has to differ from the vanilla four in its outer quarter.** That is not a
style note, it is the test. It is what cut two of the first six:

- **Ring.** The hole was at `0.52R`, well inside the covered region - a stacked ring is a circle.
- **Bevel.** The chamfer is at the rim and does survive, but a square with a clipped corner reads as
  a square anyway, stacked or not.

`0.24` is the field's default in a `[Serializable]` class, so the shipped value is authored and
could differ; `esp.report` prints the real one.

## The border is grown outward, and it is 0.043

*Measured off the vanilla meshes, not tuned by eye.*

Reported from a real save: the vanilla shapes have a thick black line around them and ours did not,
and ours had "holes" between the parts where vanilla has black. Both symptoms are one cause.

`esp.dump` writes the vanilla meshes as well as ours, so this is measurable. Every vanilla part puts
the **coloured face at the designed outline and the black past it**:

| part | colour reaches | black reaches | band |
| --- | --- | --- | --- |
| circle | 0.370 | 0.413 | +0.044 |
| square | 0.502 | 0.544 | +0.042 |
| windmill | 0.373 | 0.414 | +0.041 |
| `CubeHex` | 0.395 | 0.440 | +0.045 |
| `FlowerHex` | 0.459 | 0.503 | +0.044 |
| **Cross, before** | 0.384 | 0.401 | **+0.017** |
| **Gear, before** | 0.357 | 0.370 | **+0.013** |

So the mod had it backwards. It inset the coloured cap by `OutlineWidth` and put the black *inside*
the nominal boundary, which made the border a third of vanilla's width **and** left every part
stopping exactly at its own outline. That second part is where the holes came from: the renderer
pushes each part outward along its bisector by `ShapeInnerGap`, and vanilla closes the resulting
gap because two neighbours each grow 0.043 across it. Ours grew by nothing, so the gap stayed
background-coloured.

`ShapePartMeshBuilder` now caps with the designed loop, grows the border outward from it, and walls
and floors from the grown loop.

### The border is a bevel, and that is what stops it z-fighting

The first version of the outward border was a **flat band at the top face's own height**, and it
z-fought - reported on `ZuZuZuZuZuZu`, where the shape visibly tore.

It renders perfectly in isolation, which is why it survived a numeric check that only measured
widths. The fault only appears *between* parts: a border grows 0.043 outward across a gap of 0.022,
so it always reaches over its neighbour's coloured face, and two coplanar surfaces of different
colours at the same depth fight. Counted from the dump, two of Sawblade's border triangles landed on
the next part's face in hexagonal mode, and one of Gear's.

Vanilla does not have this problem because **vanilla has no flat black triangles at all**:

| part | flat levels | body tris | black tris |
| --- | --- | --- | --- |
| circle (vanilla) | 0.0954 | 21 | **0** |
| `CubeHex` (vanilla) | 0.1000 | 22 | **0** |
| `RectHex` (vanilla) | 0.1000 | 26 | **0** |
| Sawblade (ours, before) | 0.0000 / 0.1000 | 6 | **51** |

Its top face is entirely coloured, and the black is the *sloped* surface running outward and down
from that face's edge, plus the wall beneath. The circle's face is at y 0.0954 and the widest point
of its black at 0.0754 - a drop of 0.02.

So the border is now a bevel with the same 0.02 drop. Where it reaches over a neighbour it sits
0.0055 below the face in quad and 0.0098 in hexagonal, which is depth separation rather than a tie.
The projected width from directly above is unchanged, because the bevel still spans the designed
outline to 0.043 past it.

**The lesson is about what the earlier check missed.** Measuring every part's band at +0.043 proved
the border was the right width and said nothing about whether it was in the right plane. A part
drawn alone cannot show this; it needs two neighbours and a depth buffer.

### And the border must not fold over itself

Reported again after the bevel: still z-fighting, hexagonal only, milder. There were two causes and
the bevel only addressed one.

**The outward offset self-intersects wherever a notch is narrower than twice the border width.** At
60 degrees Gear and Flower both do it - two crossings each - and no part does at 90 degrees, which is
exactly why it only showed in hexagonal mode: the sector compresses every angular feature, and
Gear's tooth valleys come out about 0.072 across against a border wanting 0.043 on each side.

It shows as z-fighting rather than harmless black-on-black because the folded region is *bevel*
against *bevel*, and the bevel carries facetted normals. Two overlapping black surfaces shaded
differently fight visibly.

`DropFoldedPoints` removes any offset point closer to the source outline than `0.97 * width`. A
valid outward offset point is exactly `width` from the outline it came from, so anything closer has
crossed some other part of it. What remains bridges the notch, which is what a true offset does:
a notch finer than the border is not representable in the border, and closing over it is the honest
answer.

Checked across all ten parts at 90, 60 and 45 degrees: zero crossings everywhere, and the band still
measures +0.043 on every one. Only Gear and Flower lose any points.

#### Dropping points opened a hole, and that is a separate bug

Reported next: Gear's and Flower's borders "not against the shape's edge", seeing straight through
the layer. Correct, and it is the pruning's fault rather than the pruning being wrong.

`AddBevel` walked the border and joined each step back to the outline vertex it came from. Where
points had been dropped, a single step spanned several outline vertices, and joining only its two
ends left the bevel's inner edge as a **chord across the notch** while the coloured cap still
followed the notch itself. The sliver between chord and notch belonged to no triangle at all - an
open mesh, so you see through the shape.

Counted as outline edges the bevel's inner boundary fails to cover:

| part | sector | outline edges | uncovered |
| --- | --- | --- | --- |
| Gear | 60 | 19 | **9** |
| Gear | 90 | 19 | **4** |
| Flower | 60 | 66 | **20** |
| Sawblade, Cross, Leaf | either | - | 0 |

Exactly the two parts that lose points, and quad was affected too - which is why it was reported as
"gears and flowers" rather than as a hexagonal problem.

The fix is for each border step to walk *every* outline vertex it passed over rather than just its
ends, so the bevel's inner boundary is the outline, edge for edge. After it, all ten parts cover
every outline edge exactly once at 90 and 60 degrees.

**Worth keeping as a class of bug:** the pruning check asked "does the border self-intersect" and
got a clean answer, because that was the right question about the *border* and the wrong question
about the *mesh*. Dropping a vertex from one loop silently changed what the surface between two
loops had to span.

**The corners have to be rounded, not mitred**, and the meshes say so: a mitre pushes a 90 degree
corner out by `width * sqrt(2)` = 0.061, and vanilla's square corner grows by 0.042 against a width
of 0.043. That is a circular offset. At the old 0.013 the difference was invisible; at 0.043 a
mitred leaf tip would be a thorn three times the width of the border it belongs to. `Outset` emits
an arc at convex corners and a single clamped mitre at concave ones, so the band is no longer one
point per input point - hence the source indices, and hence `AddBorder` fanning back to the source
rather than stripping quads.

Checked afterwards: every one of the ten parts grows exactly +0.043 at both 90 and 60 degrees, with
no mitre spikes anywhere, and Gear at 90 degrees now measures 0.370 / 0.413 - the same two numbers
as vanilla's circle.

### The dump renderer was lying, briefly

`render_dump.py` first drew only top-face triangles, on the reasoning that the top face is what you
see from above. It is not: **vanilla's border is a flange below the top face and wider than it**, at
y 0.0754 against the face's 0.0954. Filtering to the top face drew every vanilla part with no border
at all - in a picture whose whole purpose was comparing borders. It now draws every triangle sorted
bottom to top.

Worth keeping as a caution about the dump: the .obj files are the truth, but a reading of them can
be just as wrong as a guess, and this one was wrong in the direction that confirmed the mistake.

## Any simple polygon works

The cap is filled by ear clipping (`ShapePartMeshBuilder.Triangulate`), not by a fan from the shape's
centre, which is what lets a part be detached from the centre entirely - Dot is a whole circle
sitting out in the quadrant, Leaf is a lens that only touches the centre at one point. A fan would
cover the gap with stray triangles.

Winding is normalised in `AsClockwise`, so a profile can be written in whichever direction reads
best. The one thing the builder will not accept is a repeated point, because a zero-length edge has
no direction to build a normal or a bisector from - it throws at mod load and names the profile.

## What was a guess, and what the screenshots settled

- **Outline placement: confirmed.** `Screenshots/Swirl.PNG` shows each quadrant with a dark border
  around its coloured top face, exactly as built - top face, side walls and underside all marked
  outline. The bet that a border on the top face was needed, rather than relying on the side walls
  alone, was the right one: from the camera angle the game uses, the top face is most of what you
  see.
- **Outline width: was 0.013, and that was wrong in two ways.** Measured off the dumped vanilla
  meshes it should be **0.043**, and it should be grown *outward* from the designed shape rather
  than carved inward out of it. See "The border is grown outward" below. The old note said 0.013
  read as a similar proportion in game; it did not, and the only reason that survived is that
  nobody had measured it.
- **Layer scale and inner gap: confirmed by eye.** The layer rings and the quadrant seams in the
  screenshots match what `Screenshots/render.py` draws from the same numbers.

`esp.dump` remains the way to settle any of this properly: it writes every registered part's LOD0
mesh - vanilla circle included - as an `.obj` with the vertex colours as trailing comments, into
`<persistent>/extra-shape-parts/`. The meshes are readable at runtime because the renderer itself
reads `vertices` and `colors` on every cache miss.

## Shape codes

One byte, case sensitive, and a flat namespace shared with the base game and every other mod. A
shape code that no longer resolves makes `ShapeHashParser` throw, which means a save that contains
it will not load.

**The namespace spans every shape configuration, not just the one being played,** and most of it
cannot be read out of the assemblies. `C R W S P c` show up as literals in
`HUDChooseBlueprintIconComponent`, so they are knowable. The rest is authored ScriptableObject data
with nothing to grep - but it *can* be read straight out of `shapez 2_Data/resources.assets`, where
a `MetaShapeSubPart` serializes as name, `HighDetailMesh` PPtr, then the code as a 2-byte char.
Doing that gives the whole vanilla namespace, and it is only eleven letters:

    C R W S   DefaultShapesQuadConfiguration - circle, square, windmill, star
    X Y       the two ConverterQuad parts, rarity NotSpawned
    G H F     DefaultShapesHexagonalConfiguration - RectHex, CubeHex, FlowerHex
    P c       pin and crystal, in all three configurations

Everything else is free: `A B E J M N Q U V`, and the lower case letters other than `c`.

**This mod has now lost four codes to that namespace.** Cross shipped as `X` and moved to `K` when
the quad configuration turned out to use `X` and `Y` - found by playing, not by reading. Gear, Dome
and Flower shipped as `G`, `H` and `F` and moved to `E`, `M` and `B` when the hexagonal
configuration turned out to use all three. The second one is the more instructive failure: those
three registered perfectly into two configurations out of three, so nothing was visibly broken, and
the only evidence was three warnings in `Player.log` on the *first* session of a process rather
than the second.

Three consequences worth keeping:

- **Pick codes from `esp.report`, not from reasoning.** It prints every configuration's parts as they
  actually are, and it is the only thing that survives a game update rewriting `resources.assets`.
  Several of the codes in use here - `D`, `T`, `O` - are exactly the initials a shape name would
  take, so this is not a solved problem.
- **Read the "already present" warnings by which session they land in.** On the second session of a
  process every part warns, and that is the idempotency check doing its ordinary job. A warning on
  the *first* session is a real collision.
- **A collision is logged at all.** `ShapePartInjector` used to skip an already-present code in
  silence, which is indistinguishable from working.

| Code | Part |
| --- | --- |
| `C` `R` `W` `S` | circle, square, windmill, star - `DefaultShapesQuadConfiguration` |
| `P` `c` | pin, crystal |
| `X` `Y` | the two `ConverterQuad` parts - in the quad configuration, rarity `NotSpawned` |
| `G` `H` `F` | **hexagonal mode** - `RectHex`, `CubeHex`, `FlowerHex` |
| `E` `K` `I` | this mod: gear, cross, bar |
| `D` `O` | this mod: diamond, dot |
| `T` | this mod: wedge - Dome's mirror image |
| `M` `Z` `B` `L` | this mod: dome, sawblade, flower, leaf |

None of the ten touch a vanilla code in any of the three configurations, so all ten register into
all three. `A J N Q U V` are still free if an eleventh part is ever wanted.

The mnemonics that survived the rename are weak on purpose - `E` for gear is the letter whose three
prongs read as teeth, `M` for do**m**e, `B` for bloom - because the obvious initial was taken in
every case. The name is what the player sees; the code is only ever typed into a shape producer.

Colour codes are a separate dictionary, so `B` here does not collide with `b` for blue.

`AffectsSaveGames` is `true` in the manifest for that reason: removing the mod from a save that has
mined one of these shapes leaves an unparseable hash.

## Hexagonal mode

*Geometry verified against the mockup; the injection path is read out of the assemblies. Untested
in game.*

The game ships three `MetaShapesConfiguration` assets: `DefaultShapesQuadConfiguration` and
`OnboardingShapesQuadConfiguration` at `PartCount = 4`, and `DefaultShapesHexagonalConfiguration`
at `PartCount = 6`. `ShapePartInjector.Inject` walks all of them, so all ten parts register into
hexagonal mode and spawn on a hexagonal map.

Before the rename only seven did, because Gear, Dome and Flower were refused there - which was the
*less* harmful of the two outcomes, and is worth noticing: **fixing the code collision made the
rendering problem worse,** because it put three more wrong-shaped meshes into a configuration that
had been rejecting them. That is the shape of the whole bug: two separate things were
configuration-scoped, the code namespace and the geometry, and only one of them was known.

**A part mesh is authored for exactly `360 / PartCount` degrees, and nothing rescales it
angularly.** `ShapeItemRenderer.GenerateShapeMesh` applies a Y rotation and a *uniform* horizontal
scale:

```csharp
Angle rotation = Angle.FromDegrees((float)j / (float)parts.Length * 360f);
float x = math.radians(((float)j + 0.5f) / (float)parts.Length * 360f);
... Matrix4x4.TRS(offset(x), FastMatrix.RotateYAngle(rotation), new Vector3(num3, h, num3));
```

With six parts that is a 90-degree mesh placed every 60 degrees: each part overlaps both
neighbours by 30 degrees, and sits 15 degrees off the inner-gap offset meant to centre it.

That is why vanilla has a separate asset per configuration - `RectQuad` and `RectHex` are different
ScriptableObjects.

### One code, one part per configuration

**Vanilla proves the pattern, and it took reading `resources.assets` to see it.** `RectQuad` and
`RectHex` have different *codes* (`R` and `G`), so they look like an argument that codes are
per-configuration. But `PinQuad` and `PinHex` are both coded `P`, and `CrystalQuad` and
`CrystalHex` are both coded `c` - the same code naming two different ScriptableObjects with two
different meshes, in two different configurations. That is exactly what this mod now does for all
ten parts.

What makes it safe is `GameSessionOrchestrator.Init_7_Rendering`, which builds its
`char -> IShapeSubPart` dictionary from the **current mode's** configuration first and fills in the
others only `if (!dictionary.ContainsKey(part.Code))`. First wins, seeded from the configuration
being played, so the instance whose sector matches the shape always wins. That `ContainsKey` guard
exists *for* pin and crystal; nothing else in vanilla needs it.

### How it is built

- `ShapeGeometry` takes the sector as the first argument of every helper, so it cannot be
  forgotten, and `Radial` takes a *fraction* of the sector rather than an angle. The old
  `Radial(Func<float,float>, int)` that swept a hardcoded 90 degrees was deleted rather than kept,
  because leaving it there is leaving the trap there.
- `ShapePartProfile` carries `Func<float, IEnumerable<Vector2>>` instead of a fixed `Vector2[]`. A
  profile is one part; a `ShapePartFactory` entry is one part at one width.
- `ShapePartFactory` keys on `(code, partCount)`, and names the object `ShapePart_Gear_6` so two
  instances sharing a code stay distinguishable in `esp.dump` and in any log line.
- `ShapePartInjector` passes `configuration.PartCount`, and skips a configuration below
  `ShapeGeometry.MinimumPartCount` - at two parts the sector is 180 degrees, `Cell`'s far corner
  collapses onto the origin and the normalisation divides by zero. `OnValidate` allows a count that
  low, so it is a real bound.

Nothing else about the mod was quad-specific: the shape operations all read `PartCount` off the
shape, and the injector already walked every configuration.

**A hot reload will not pick this up.** `GameData` and its configurations are built once per
*process*, and the mod mutates their lists; after `mrl.reload` the old 90-degree instances are
still in the hexagonal configuration's list, so the presence check skips re-adding and the new
meshes never arrive. Restart the game.

`MetaShapesConfiguration.OnValidate` requires an even `PartCount` "so the half cutter works" and
caps it at 32, so 4 and 6 are conventions rather than limits - a third configuration could appear.

### What the first hexagonal session found

Two things, both of which had passed every check that could be run without the game.

**Cross read as vanilla's `G`.** Fixed by scaling the arm fraction by `sector / 90`, which holds
the notch's angular width constant instead of its share of the cell, so the notch deepens as the
sector narrows and the spokes stay open. At 90 degrees it is exactly `0.42f`, so quad is unchanged
to the bit.

**That fix was tuned against the wrong reference, and happened to move the right way.** The theory
was that `RectHex` is the cell-filling part and Cross was reading as a cell with a nick. Dumping the
real meshes says otherwise: the cell-filling part is **`CubeHex` (`H`)**, and **`RectHex` (`G`) is a
six-pointed star** with concave tapering arms. Cross was reading like `G` because a chunky notched
cell *is* a chunky star - so thinning the arms did separate them, by turning a thick tapering star
into a thin straight-edged one. Right change, wrong reason.

The lesson is about method rather than geometry: **an authored mesh's asset name is not its shape**,
and a stand-in drawn from a guess is worth very little. `esp.dump` walks every configuration, so it
writes the vanilla parts out too; `Screenshots/render_dump.py` draws whole shapes from those files
and is the only drawing in this repo that can compare the mod against the game rather than against
itself.

**What the real meshes show, and it is worth keeping:** `CubeHex` is the filled cell (a plain
hexagon), `RectHex` is a six-pointed star, `FlowerHex` is six round petals. Those are the three most
obvious six-fold silhouettes, and every part here has to avoid all three.

**Flower did not, and that was the second thing the hexagonal session found.** Ours is one round
lobe per sector; `FlowerHex` is the same idea, so at six parts they were all but the same shape.
This one could not be fixed by re-parametrising: a lobe is a lobe at any width, and every knob in
the sweep - waist depth, lobe width, radius - only ever made it a slightly different round petal.
The petal needed a feature vanilla's does not have.

It got **two notches, leaving three teeth per petal**, scaled in from nothing at 90 degrees so the
quad shape is untouched - and verified untouched: the 90 degree outline matches the shipped one to
8e-17. The notches are narrow Gaussians rather than wedges, because straight-sided notches read as
damage and rounded ones read as a leaf's teeth, and they sit either side of the bisector so the
middle tooth keeps the petal's full reach while the outer two step down.
`Screenshots/flower-before-after.png` draws it against the dumped vanilla mesh, and
`Screenshots/flower-tune.png` has the six candidates it was chosen from - a plain cleft, a twin
lobe, scalloping, a flat top - which is worth keeping for the ones that *failed*: scalloping and a
squared-off tip both still read as FlowerHex.

Two narrow notches need much finer sampling than a plain lobe - 64 steps against 28 - or they come
out as dimples. The step count is switched on the teeth rather than raised for both, which is what
keeps the quad mesh identical rather than merely similar.

**The general rule this leaves:** vanilla spends the obvious silhouettes of its own configuration.
A part that is *conceptually* the same as one of them cannot be saved by geometry, and the only way
to find out is to draw it against the real mesh.

The general lesson, and it is the one worth keeping: **a correct transform is not a part that still
reads as itself.** Check a whole shape of six against the *vanilla parts of that configuration*,
not against its own quad version.

**The shape viewer's legend overflowed.** `HUDShapeCodesPreview` draws one entry per
`ShapesConfiguration.Parts` entry under "Parts", and one per colour under "Colors" - fifteen in a
hexagonal save against vanilla's five. The parent lays them out in a single row, and the extras ran
off over the view controls beside them. `ShapeCodesPreviewLayout` postfixes `Construct` and swaps
that parent for a `GridLayoutGroup` with `Constraint.Flexible` plus a `ContentSizeFitter`, which
wraps to whatever width the panel has.

**The first attempt threw, and the reason is worth writing down.** Unity refuses to add a second
`LayoutGroup` to a GameObject - `Can't add 'GridLayoutGroup' to Parts because a
'HorizontalLayoutGroup' is already added' - and `AddComponent` then returns **null** rather than
throwing, so the failure surfaced as a `NullReferenceException` on the next line instead of at the
cause. Disabling the old group, which is what the first version did, does not help: the refusal is
about the component existing, not about it being enabled. It has to be `DestroyImmediate`d, because
plain `Destroy` is deferred to the end of the frame and the `AddComponent` is on the next line. The
`ContentSizeFitter` also has to have its *horizontal* fit set to `Unconstrained` explicitly, or the
row grows sideways again and a flexible grid laid out in an unbounded parent produces one row.

It logs what it measured - entry count, existing layout type, cell size, panel width before and
after - because the prefab's structure is not readable from the assemblies. `panel width=0` before
the rebuild is expected: no layout pass has run at `Construct` time, which is also why the cell has
to be measured after a forced rebuild rather than read off the transform.

**`HUDMapResourcesFilterRow` turned out to be fine**, checked in game with all fourteen parts
registered. It builds a row per part in `MapGenerationAllParts` exactly as `HUDShapeCodesPreview`
does, so it was the obvious candidate for the same overflow and it is not one - whatever that panel
does with its rows, it copes. Worth recording because the prediction had been repeated three times
on the strength of the shared pattern, and repeating a guess does not make it a finding.

### The mockup, and what checks it

`Screenshots/render_hex.py` draws all three cases, into `Screenshots/hexagonal.png` (one card per
part: quad, hexagonal drawn with the quad mesh, hexagonal with its own) and
`Screenshots/hexagonal-patterns.png` (layered hexagonal shapes).

Two checks make it worth more than a drawing, and both are worth re-running after any change to
either file:

- Its generalised `draw_shape(..., count, sector)` produces **pixel-identical** output to
  `render.py`'s shipped quad `draw_shape` at `count=4, sector=90`, so the hexagonal columns are the
  same code extrapolated rather than a re-drawing by eye.
- `ExtraShapePartCatalog.cs` transcribed back into Python matches `render_hex.py`'s builders **point
  for point at both 90 and 60 degrees**, to 1.2e-16. That is a transcription check, not a proof - it
  catches a mistyped constant or a swapped `Cell(u, v)`, not a mistake made identically in both -
  but those are the mistakes that actually happen. `esp.dump` is the real check.

The same script also confirms no outline produces a repeated point at 90, 60 or 45 degrees, which
`ShapePartMeshBuilder` rejects outright: a zero-length edge has no direction to build a normal or a
bisector from, and a sector that is legal at one width can collapse a point at another.

Three things the mockup settled that reasoning had not:

- **Re-authoring is per-part, not one transform.** The radial parts (gear, sawblade, flower, dome)
  take the sector angle as a parameter and need nothing else. The cell-authored parts (cross, bar,
  diamond, wedge) have to go through the sector's own oblique basis, or their straight edges bend.
  The two placed parts (dot, leaf) keep their shape and move to the new bisector.
- **The oblique basis has to be normalised, and that is not obvious.** Two unit edges 60 degrees
  apart span a rhombus whose long diagonal is `1.73R`, against a quadrant's `1.41R`. Left raw, every
  cell-authored part comes out a third too big and pushes into its neighbours. The first draft of
  the sheet drew exactly that, and it read as a flaw in the parts rather than in the transform.
- **Some parts survive the broken case and some do not.** Flower and Dot still read as themselves
  at 90 degrees in a 60-degree slot, because a round outline hides the overlap. Gear becomes an
  unreadable blob and Cross, Bar and Wedge become tangles. So "does it look broken in hexagonal
  mode" is not a question with one answer, which is an argument for the `PartCount != 4` guard
  rather than for hoping.

## Side quests

*Compiles clean; the shape codes are checked against the reviewed draft. Untested in game.*

Ten chains, thirty-eight quests. The first seven are from [JOBS-DRAFT.md](JOBS-DRAFT.md); the
last three - Millstone, Both Hands and Widow's Web - came from reading vanilla's own side quests
out of `debug.export-game-data`, and each uses an idiom the original seven do not: a layer that
changes rather than grows. They appear in the research
screen's Side Quests tab, below vanilla's.

### The hook, and why it is not a detour

`IGameScenarioRewirer.ModifyGameScenario` - ShapezShifter's own seam, which it runs immediately
after the game constructs a `GameScenario`. Extended Research already uses it, which is as close to
a proof that it works in this game version as reading can get.

The ordering against the shape part injection is load bearing and it happens to be free: the part
injection prefixes `Init_3_SavegameAndMode`, which runs *before* `GameMode.From` builds the
scenario, so by the time the rewirer fires the extra parts are already in every configuration and a
quest asking for `EuEuEuEu` resolves. `SideQuestRewirer` reads `IGameData` out of
`ShapePartConsole.GameData`, which the earlier hook sets, and skips with a warning if it is null -
that null is the signal that the ordering assumption has broken.

### A quest cannot store a shape code

This is the whole reason the chains are not just a table of strings. `StrictShapeDefinitionFactory`
rejects any hash whose first layer is not exactly `PartCount` pairs, so `MrMrMrMr` is a *quad*
quest and is **invalid** in hexagonal mode, not merely wrong-looking.

So a `SideQuestStep` stores each layer as a repeating pattern and fills it at injection time:
`"Mr"` becomes four red domes or six, `"RuDu"` alternates, `"Br--"` leaves gaps. Vanilla writes its
own side tasks the same way - `SG_Fluids_1` costs `Rb--Rb--:Cu--Cu--` - so the idea is the game's,
not an invention.

A pattern that does not divide the part count would run off the end mid-repeat, producing a shape
nobody wrote that still parses. `SideQuestStep.Fits` refuses it and the chain is dropped with a log
line rather than silently mangled.

### What gets skipped, and what gets trimmed

Per scenario, against that scenario's own configuration:

- A step whose layer count exceeds `ResearchConfig.MaxShapeLayers` is **trimmed** off the end. The
  steps are ordered, so a shorter chain is still a coherent progression.
- A step whose shape will not parse - a part or a colour this scenario does not have - **drops the
  whole chain**, because a hole in the middle is not a progression at all.
- Validation uses the game's own parser over the scenario's own parts and colours, so a code that
  passes here is a code the session can resolve.

### Four lists, not five

The draft said `ResearchProgression` keeps five collections a quest has to land in. It keeps
**four**: `_SideQuestGroups`, `_SideQuests`, `_AllUpgrades`, `_UpgradesById`. The fifth,
`_SideQuestsIncludingHidden`, is declared and never assigned or read anywhere in the assembly.

Appending to `_SideQuestGroups` alone renders the tab and then fails to resolve the quests by id,
which is exactly the kind of half-working that does not announce itself.

### Rewards are not optional, for a non-obvious reason

`RemoveUpgradesWithZeroRewards` deletes any non-`ResearchLevel` upgrade with an empty reward list.
It runs from `ResearchProgression`'s constructor, which has already finished by the time the
rewirer fires - so a reward-less quest would survive injection and then vanish the first time
anything calls `TryRemoveUpgrade`, which prunes again. A bug that appears later and elsewhere.

**The amounts are vanilla's, not invented.** `Scenarios/Classic/Regular/SideTasks/SG_Fluids_1`,
read out of `resources.assets`, is a five step chain paying 1, 2, 2, 4 and 12 research points and
30 to 60 chunk limit for 1200 to 2500 shapes a step. Ours pay 1, 2, 4, 8 points and 30 to 60
capacity; the shape amounts are the draft's 250 to 8000, which straddles vanilla rather than
sitting beside it.

### One chain was quad-only, and said so

`Foundations` is "vanilla underneath, new on top", and it was written as `RuDu` and `Cw`. `R` and
`C` are the *quad* configuration's square and circle; the hexagonal configuration has neither. So
the validator did its job and dropped the whole chain out of hexagonal saves - 23 quests instead of
26, with the reason in the log.

Fixed by not naming vanilla parts at all. `1` and `2` in a layer pattern resolve to the
configuration's first and second `MapGenerationCommonParts` entry, which is square-and-circle in
quad and hexagon in hexagonal - what "the vanilla shapes" actually means in each. Digits are safe
placeholders because no shape code in the base game is a digit. A configuration with only one common
part gets it for both.

This mod contributes nothing to `MapGenerationCommonParts` - every part here is Rare or VeryRare -
so those placeholders resolve to vanilla parts rather than to our own.

### Gating

Each chain names its gate, and the gate is checked against the scenario before it is used. That
check is not defensive tidiness: `ResearchProgression.Validate` runs in the constructor, long before
a rewirer fires, so a requirement naming an upgrade the scenario does not define **throws nothing**
and simply never resolves. The chain would be invisible forever with nothing in the log.

**What a chain needs is mostly its colours, not its machines.** The cutter, rotator and stacker all
arrive at `Milestone_Initial`, so gating on those would be a no-op. Colour availability was derived
from `debug.export-game-data` by asking, for every colour, the earliest milestone goal and the
earliest side quest in which *vanilla itself* asks for it. The two sources agree:

| colour | available from | why |
| --- | --- | --- |
| `u` `r` `b` `g` | the start, then fluid extraction | `PrimaryColors` |
| `c` `m` `y` | `CBFluids_Mixer` | `SecondaryColors` - one mix of two primaries |
| `w` | `CBFluids_Mixer` | `TertiaryColors` - all three mixed |
| `k` | `CBFluids_Mixer` | white mixed with white |

**That table is mechanical, and the first version of it was not.** Reading availability off *where
vanilla happens to ask for a colour* put magenta at `CBSpecial_SpaceFloor3`, because that is the
first side quest group using it - and a space floor has nothing to do with paint. Magenta is a
`SecondaryColors` entry exactly like cyan and yellow, made the same way by the same machine, so it
arrives with the mixer. Fine Detail was gated a whole milestone late on that mistake.

The lesson generalises: **correlation in the content is not the mechanism.** `ShapeColorScheme`
exposes `PrimaryColors`, `SecondaryColors`, `TertiaryColors` and `PlayerObtainableColors`, and those
are the answer. `esp.report` now prints all four for every scheme, which is how the remaining
question about black should be settled rather than argued.

So nine of the ten chains gate on the mixer, `CBSpecial_SpaceFloor3` or `CBSpecial_Crystals` - the
latest thing each needs anywhere, because the game allows one gate per group and a chain gated on
its *first* step's needs strands the player on its last.

**Black cost two wrong answers before the right one.** It is `white mixed with white` - one extra
mixing stage past a colour the mixer already makes, and no unlock that white does not need. So
Widow's Web gates on the mixer like everything else.

The two wrong answers are the point. Vanilla never asks for black before the post final milestones
in the quad scenarios and never asks for it *at all* in the hexagonal one, so reading availability
off usage put the chain six milestones late and then needed a per-scenario gate list to paper over
the fact that hexagonal has no post final milestone to point at. All of that machinery existed to
support a conclusion that was simply wrong. **Correlation in the content is not the mechanism** -
the same mistake that had put magenta behind a space floor, in the opposite direction.

A chain still names gate *candidates* in order and takes the first the scenario defines, because
that costs nothing and scenarios genuinely do differ. If none exist the chain is **skipped rather
than un-gated**: a scenario with no mixer cannot build a white shape either, so showing the chain
would only frustrate. That is what happens in onboarding, which has no painter, no pin pusher, no
mixer and no crystals - it gets none of these chains, which is correct.

What settled it was asking rather than inferring: `PlayerObtainableColors` lists `k`, and the mixing
rule is white plus white. `esp.report` prints that list now.

### Quest ids are save state

`ResearchUpgradeId` is what completion is recorded against, so ids are `esp.<chain>.<step>` and
must never be reused for a different quest. This is a second reason `AffectsSaveGames` is true.

## Map generation

`MapShapeGenerator.PickRandomShape` rolls **rare first, then very rare**, then falls through to
common, each bucket a flat `rng.Choice`. So a part added to a bucket takes an equal share of that
bucket - four new rare parts would make the vanilla rares much scarcer. The order is what sets the
odds: very rare is rolled only on the 70% that rare declined, so the shipped 30 / 10 is really
30% rare, **7%** very rare, 63% common.

**The vanilla four are not all common.** Read out of `resources.assets` - `MetaShapesConfiguration`
serializes `Parts` as (12-byte `PPtr`, 4-byte rarity) pairs, and the pin and crystal entries
cross-check the decoding against the two named fields:

| Configuration | Common | Rare | Very rare | `NotSpawned` |
| --- | --- | --- | --- | --- |
| `DefaultShapesQuadConfiguration` | `C` `R` | `S` | `W` | `P` `c` `X` `Y` |
| `OnboardingShapesQuadConfiguration` | `C` `R` | `S` | `W` | `P` `c` `X` |
| `DefaultShapesHexagonalConfiguration` | `H` | `G` | `F` | `P` `c` |

So a quad map is 63% circles and squares, 30% stars, 7% windmills. **This mod adds five parts to
rare and five to very rare, which makes the star one sixth as common as it was and the windmill one
sixth as common as it was.** That is a much bigger change than the ten new parts suggest, and it is
the number to argue about.

`esp.report` prints the buckets as they actually are at runtime, after the mod has run. **Read it
before deciding the rarities are balanced** - a game update rewrites `resources.assets`. The current split - gear, cross, diamond, dome and wedge rare; bar, dot,
sawblade, flower and leaf very rare - is a starting point, not a measurement. Bar sits in the lower
tier deliberately: Bar, Dome and Wedge all make pinwheels and so does the vanilla windmill, which is
a crowded family to put four entries in. The two arrow halves
share a rarity deliberately: a player who can only find one of them cannot build an arrow.

## Operator level goals - already wired, and the odds are hardcoded

*Verified.* `ResearchPlayerLevelGoalManager` builds a `RandomResearchShapeGenerator(mode, ...)`,
which builds its own `MapShapeGenerator` over **`mode.ShapesConfiguration`** - the same object this
mod mutates. So the parts appear in randomised operator goals with no extra work.

Unusually for this area, the odds are **not** authored. `MapConfigNormal` is a static
`MapGenerationParameters.SerializedData` literal inside `RandomResearchShapeGenerator`:

```csharp
ShapePatchRareShapeLikelinessPercent = 30,
ShapePatchVeryRareShapeLikelinessPercent = 10,
```

`PickRandomShape` rolls rare first, so per part pick: **30% rare, 7% very rare** (70% x 10%),
**63% common**. A goal stacks `clamp(level / 10, 0, MaxShapeLayers - 1)` extra cluster shapes on top
of the base one, so a high level makes several picks and the odds compound in the player's favour.

Two consequences:

- A goal can only ask for a part that is in a generation bucket, and the same buckets drive the map.
  So **a goal can never demand something unobtainable** - including for a part set to `NotSpawned`,
  which is in no bucket and therefore in neither.
- `Rng.SetSeed(seed + 333 * level + ...)` is deterministic per seed and level, so adding parts
  changes which shape a given seed asks for at a given level. Another reason for `AffectsSaveGames`.

**Vanilla progression is untouched.** Milestone goals are `FixedShapePlayerLevelGoalLine`, built from
literal shape strings in authored scenario data. This mod never touches those - it only widens the
random pool.

**Eight new parts is a lot** - four rare and four very rare against vanilla's four. That is a
deliberate starting point for looking at them in game, not a balance judgement. Rarity is one field
per part in `ExtraShapePartCatalog`, and `NotSpawned` registers a part without putting it on the
map.

## The easier route, found late

Most of what this file records as "read out of `resources.assets`" did need the byte reading. Some
of it did not.

`debug.export-game-data` writes `<persistent>/basedata-v<version>/`: every scenario as fully
resolved JSON with no `#include` left in it, the translations, the identifier list and the JSON
schemas. That is where the reward scale used by `SideQuestCatalog` should have come from - vanilla's
own side quests, costs, amounts and gate ids, all in one file - rather than from a regex over a
567 MB asset blob.

What the export does *not* carry is the shape configurations: part codes, meshes and rarity buckets
are `MetaShapesConfiguration` data and appear nowhere in it. So the `resources.assets` reading was
necessary for the namespace and the rarity table, and unnecessary for `SG_Fluids_1`.

Worth knowing before the next question about authored data: **check the export first.**

## Open questions

- Does the generated geometry read as a shapez shape in motion, or does the flat extrusion look
  wrong next to the authored ones? `esp.dump` first.
- Does Bar read as the vanilla windmill? Four bars laid along the quadrants' leading edges make a
  pinwheel, and so does the windmill. Bar's blades are straight-sided rectangles rather than sails,
  which should be enough, but it is the open risk in the set now that Arrowhead is gone.
- **Four pinwheels is probably one too many.** Bar, Dome and Wedge plus the vanilla windmill all
  read as blades going round. Wedge is the newest and the most deliberate of them; Bar is the one
  to drop if the map looks samey.
- Ten parts is a lot. Trimming is a one-word edit per part - `NotSpawned` registers a code without
  putting it on the map.
- Eight new parts at once may be too many for map legibility even at rare.
- ~~`HUDMapResourcesFilterRow` builds a row per part in `MapGenerationAllParts`. Ten parts instead
  of four may overflow that UI.~~ Checked in game: it copes.
