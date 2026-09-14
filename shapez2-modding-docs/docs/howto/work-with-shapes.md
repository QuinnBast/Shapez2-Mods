# Work with shapes

**Problem.** You want to read the shape on a belt, test what it is, or transform it the
way the game's own machines do.

## Reading a shape off a lane

Items on lanes are `IBeltItem`. A shape item is a `ShapeItem`:

```csharp
if (lane.HasItem && lane.GetItem(0) is ShapeItem shapeItem)
{
    ShapeDefinition definition = shapeItem.Definition;

    string hash  = definition.Hash;        // e.g. "CuCuCuCu"
    int parts    = definition.PartCount;
    ShapeLayer[] layers = definition.Layers;
}
```

`ShapeDefinition.Hash` is the human-readable shape code — the same notation the game
and community tools use. It is the quickest way to log or compare a shape.

## Shape definitions are shared and interned

A `ShapeDefinition` is not per-item data. Every belt carrying the same shape references
the same definition object, identified by `ShapeId`:

```csharp
public class ShapeDefinition : IShapeOperationInput
{
    public readonly ShapeId Id;
    public readonly string Hash;
    public readonly int PartCount;
    public readonly ShapeLayer[] Layers;

    public ulong UniqueOperationId { get; }
    public ShapeLayer[] GenerateClonedLayers();
}
```

Two consequences:

- **Compare by `Id` or `Hash`**, not by walking layers.
- **Never mutate `Layers`.** You would be editing every belt in the save.
  `GenerateClonedLayers()` exists for when you need a mutable copy.

## The registry

```csharp
IShapeRegistry registry = GameHelper.Core.ShapeRegistry;

ShapeDefinition definition = registry.GetDefinition(shapeId);
ShapeItem item = registry.GetItem(shapeId);            // the interned item

registry.TryGetDefinition(shapeId, out var def);
registry.TryGetItem(shapeId, out var shapeItem);
```

Use `GetItem` when you need an `IBeltItem` to hand to a lane — do not construct
`ShapeItem` yourself, or you lose interning.

## Building a shape from a code

```csharp
IShapeDefinitionFactory factory = /* StrictShapeDefinitionFactory */;

if (factory.TryCreateShapeDefinition("CuCuCuCu", out ShapeDefinition definition))
{
    // …
}
```

`CreateShapeDefinition(string hash)` throws on a malformed code; the `TryCreate…` form
does not. Prefer the latter for anything player- or config-supplied.

> [!NOTE]
> The factory is constructed per session (`StrictShapeDefinitionFactory` is a field on
> `GameSessionOrchestrator`, reachable with the
> [publicizer](../publicizer.md)). There is no documented static accessor.

## Operations

Every transformation in the game is a `ShapeOperation<TInput, TResult>`:

| Operation | Does |
| --- | --- |
| `ShapeOperationCut` | cuts in half → two results |
| `ShapeOperationSwapHalves` | swaps halves |
| `ShapeOperationRotate` | rotates |
| `ShapeOperationStack` | stacks two shapes |
| `ShapeOperationUnstack` | removes the top layer |
| `ShapeOperationPaint` | paints |
| `ShapeOperationPaintTopmost` | paints only the top layer |
| `ShapeOperationCrystallize` | crystallizes |
| `ShapeOperationFilter` | tests a shape against a filter |
| `ShapeOperationAnalyze` | inspects |
| `ShapeOperationPushPin` | pushes pins |

Construct with the registry and id manager, then `Execute`:

```csharp
ShapeOperationCut cut = new ShapeOperationCut(maxShapeLayers, shapeRegistry, shapeIdManager);

ShapeCutResult result = cut.Execute(definition);
ShapeCollapseResult left  = result.LeftSide;
ShapeCollapseResult right = result.RightSide;
```

That is precisely what the `DiagonalCutter` sample does inside its processing lane's
`AcceptHook`, turning the cut result into the output item:

```csharp
ShapeCutResult cutResult = cutOperation.Execute(definition);
ShapeItem output = shapeRegistry.GetItem(cutResult.RightSide?.Shape ?? ShapeId.Invalid);
item = output;   // null output means "produced nothing"
```

Several operations also implement `IItemOperation1In1Out` / `IItemOperation1In2Out`,
which give a uniform `TryExecute(IItem input, out IItem output)` if you want to treat
operations generically.

## Operations are caches — dispose them

`ShapeOperation` is `IDisposable` and memoises results:

```csharp
public abstract void Dispose();
public abstract void GarbageCollect();
public abstract int GetCacheSize();
```

Because shapes are interned and finite, caching pays off enormously — but the cache
grows. Create **one operation instance** and reuse it, dispose it with your mod, and
call `GarbageCollect()` if you are generating unusual volumes of novel shapes.

Do **not** construct an operation per item; you would get no cache hits and allocate
constantly.

## Gotchas

- **A result can be empty.** Cutting can yield nothing on one side —
  `ShapeId.Invalid` and a null item are normal, and the samples treat "produced empty
  shape" as a real state.
- `maxShapeLayers` comes from game data, not a constant you invent. Wrong values give
  wrong results at the layer limit.
- Colours are `IShapeColor` and go through `IColorVisualizationManager` for display —
  do not hard-code colour hexes for shape rendering.
- Shape hashes appear in save files and blueprints. Treat them as a stable format for
  comparison, not something to reformat.
- The four quadrant types are not a closed set — see
  [Add a shape part](add-a-shape-part.md) for adding a fifth. The parts a session accepts
  come from `IShapesConfiguration`, whose lists are `IReadOnlyList` views over mutable
  lists.
