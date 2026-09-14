# Placement previews and predictions

**Problem.** When the player drags a building out, the game shows what it *would*
produce before it is built. Your building shows nothing.

**Solution.** A prediction simulation — a second, lightweight simulation of your
building that runs against hypothetical inputs to compute hypothetical outputs.

## What predictions are for

The prediction system is what powers the placement preview: the little display of "this
cutter would output these two shapes". It is a parallel simulation graph, run on demand
rather than every tick, so the preview can be computed without touching the real
factory.

Every processing building in the game has one. Without it your building is placeable but
the preview is blank, which reads as broken.

## Through Flow

For a building you are adding, it is one stage in the chain:

```csharp
.WithPrediction(new MyPredictionFactoryBuilder(), logger)
```

`DiagonalCutter` supplies `Operation1In1OutPredictionFactoryBuilder`, which is the
shortcut worth knowing: for a machine that takes one item and produces one item via a
shape operation, the generic 1-in-1-out builder does the work — you hand it the
operation and it derives the prediction.

```csharp
IBuildingPredictionFactoryBuilder   // buildings
IIslandPredictionFactoryBuilder     // islands
```

The corresponding extenders are `BuildingPredictionExtender` and
`IslandPredictionExtender`.

## A transport island without one is a hole in the graph

For a machine, a missing prediction means *its own* preview is blank. For anything that
**carries items through** — a belt-like island, a crossing, a custom space path — the cost
is bigger: the prediction graph stops there, so everything *downstream* reads as empty
while real items flow through it perfectly. The usual report is "the shape/fluid readout
flashes Empty even though it's working".

Forwarding is cheap to model. Vanilla's `SpacePathPredictionSimulation` is the whole
pattern — one bundle handed out as both receiver and provider, so whatever it is told
arrives at the other end:

```csharp
public class MyPathPrediction : IItemBundlePredictionSimulation, ISimulation, IUpdatableSimulation
{
    private readonly ItemPredictionBundle<ItemPredictionConverter> Bundle =
        ItemPredictionBundle.Create<ItemPredictionConverter>();

    public int NumItemProviderBundles => 1;
    public int NumItemReceiverBundles => 1;

    public IItemPredictionProviderBundle GetItemProviderBundle(int outputIndex) => Bundle;
    public IItemPredictionReceiverBundle GetItemReceiverBundle(int inputIndex) => Bundle;

    public void Update(Ticks startTicks, Ticks deltaTicks) => Bundle.Update(deltaTicks);
}
```

`ConnectableIslandPredictionSimulation` pairs prediction bundles to connectors by the same
declaration order `ConnectableIslandSimulation` uses for the real ones, so one connector
order serves both — an island with two independent paths returns two bundles and needs no
extra wiring. It is bounded the same way too: `min(NumItemReceiverBundles, connectors.Count)`,
so an island that declares two connectors at one pivot — a belt-tagged one and a pipe-tagged
one, which `IslandConnectorData` allows — must claim **two** bundles or the second connector
is silently dropped from the prediction graph.

Claim two, but hand back the *same* bundle object for both indices:

```csharp
public int NumItemReceiverBundles => 2;
public int NumItemProviderBundles => 2;

public IItemPredictionProviderBundle GetItemProviderBundle(int outputIndex) => Bundle;
```

`NextBundle` is one field on the bundle, and `ItemPredictionOutputChunkConnector.TryConnect`
refuses once it is set. Sharing means whichever connector links first satisfies both indices;
two separate bundles would leave the unused one permanently dangling, which is the state the
renderer draws a bubble for.

### The bubble appears on the *downstream* island

Worth knowing before you go looking in the wrong place. `IslandPredictionRenderer` walks each
island's **provider** bundles and draws at the connector pivot of any whose `NextBundle` is
null:

```csharp
if (itemProviderBundle.NextBundle == null)
{
    ItemPredictionDrawer.DrawPredictedItem(options, connector.Pivot, in prediction);
}
```

An output pivot sits against the entrance of whatever it feeds. So an island with no
prediction simulation produces bubbles that appear **on its own entrance** while belonging
entirely to the belt in front of it — the island is not drawing anything, it is failing to
give that belt something to link to. Adding the receiver bundle is what clears them.

`ItemPredictionDrawer.DrawPredictedItem` also explains the two shapes the complaint takes:
`drawPlateForEmpty` defaults to `false`, so an empty prediction draws nothing at all, while
`PredictedItem.Degenerated` draws a plate with the null material over it — the crossed-out
"could be anything" marker. Neither is your island's own output.

### Do not put an island's prediction on the extender chain

`.WithPrediction(...)` exists on the island chain, and using it is a trap:
`AtomicIslandExtender.Build` re-arms itself only once **every** branch it was handed has
fired, and the prediction branch never fires at all for a player who has turned the
`prediction` setting off. A chain that never re-arms is spent on the main menu's background
game, so the island never appears in that player's actual save, with no error anywhere. The
mechanism, the `Player.log` signature and the fix are written up in
[The extender chain is one-shot](add-an-island.md#the-extender-chain-is-one-shot-and-withprediction-can-strand-it).

Register it by hand instead, re-arming per scenario load:

```csharp
IAtomicIslandExtender simulated = AtomicIslands.Extend()
    .AllScenarios()
    .WithIsland(island, group)
    .UnlockedAtMilestone(new ByIndexMilestoneSelector(0))
    .WithSimulation(new MySimulationFactory());

// The cast is the only way in. WithSimulation lands on IAtomicIslandExtender, which has no
// WithDefaultPlacement; placement is reachable only through IDefinedSimulatableIslandExtender
// and nothing in the chain returns it. Casting back out again on the other side is the same
// story: InToolbar returns an interface whose only two exits are WithPrediction and
// WithoutPrediction, and WithoutPrediction is `throw new NotImplementedException()`.
IDefinedAccessibleSimulatablePlaceableIslandExtender placed =
    ((IDefinedSimulatableIslandExtender)simulated)
    .WithDefaultPlacement()
    .InToolbar(slot);

((IAtomicIslandExtender)placed)
    .WithoutModules()
    .Build();

registrations.Add(new ReArmingRewirer(() =>
    new IslandPredictionExtender<MyPredictionSimulation>(
        definitionId, new MyPredictionFactory(), logger)));
```

Both casts are safe, because every one of those interfaces is implemented by the same
`AtomicIslandExtender` instance and both `WithDefaultPlacement` overloads are the same
no-op.

## Registering a prediction system directly

For prediction behaviour not tied to one building:

```csharp
using ShapezShifter.Hijack.Predictions;

public class MyPredictionsRewirer : IPredictionSystemsRewirer
{
    public void ModifyPredictionSystems(
        ICollection<ISimulationSystem> simulationSystems,
        PredictionSystemsDependencies dependencies)
    {
        simulationSystems.Add(new MyPredictionSystem(dependencies.ShapeRegistry));
    }
}

GameRewirers.AddRewirer(new MyPredictionsRewirer());
```

Prediction systems are ordinary `ISimulationSystem`s — see
[Add a simulation system](add-simulation-system.md) — registered into a *separate*
collection from the real ones.

`PredictionSystemsDependencies` is a slimmer set than the simulation one:

```csharp
public readonly GameMode Mode;
public readonly IGameResourcesMap ResourcesMap;
public readonly IShapeRegistry ShapeRegistry;
public readonly IShapeIdManager ShapeIdManager;
public readonly IResearchUnlockManager ResearchUnlockManager;
public readonly ILogger Logger;
public readonly ITrainHashCalculatorHeuristic TrainHashHeuristic;
```

No fluid registry, no signal channels — a hint at what predictions are expected to
model.

## Replacing a vanilla prediction, not just adding one

`ModifyPredictionSystems` hands you a mutable `ICollection`, so vanilla's entry can come
**out**. That matters whenever you are correcting a prediction rather than supplying a
missing one: two systems claiming the same building will both accept it in
`BuildingIsOffered` and both build a simulation for it.

A system announces the building it serves through
`ISpecializedBuildingTenantSimulationSystem.SpecializedBuildings`.
`AtomicBuildingSimulationSystem` implements that **explicitly**, so the id is reachable by
casting to the interface — no publicizer, no reflection:

```csharp
foreach (ISimulationSystem system in simulationSystems)
{
    if (system is ISpecializedBuildingTenantSimulationSystem specialized
        && specialized.SpecializedBuildings.Contains(definitionId))
    {
        doomed.Add(system);   // remove in a second pass; do not mutate while enumerating
    }
}
```

> [!TIP]
> Add your replacement **only if you actually removed something**. If a game update or
> another mod moves the registration you were expecting, leaving the graph untouched
> degrades to vanilla behaviour; adding regardless double-registers the building.

Also resolve building groups with `TryGetDefinitionGroup`, not `GetDefinitionGroup` — the
latter is a raw dictionary indexer, so a game mode lacking that building throws
`KeyNotFoundException` out of a postfix hook during session construction.

## A prediction can read the real simulation's state

The dependencies carry no signal channels and no fluid registry, which reads as "a
prediction cannot know about live state". For a *placed* building that is not quite true:

> `AtomicBuildingSimulationSystem.CreateConnectableSimulation(BuildingInstance building)` is
> `protected abstract`, and the `BuildingInstance` a prediction system is handed is the
> **same one** the real simulation system gets — including the same
> `SimulationStateContainer`.

So `building.State.Is<TRealState>(out var state)` gives a prediction access to the real
simulation's state object. That is the only practical route for a building whose routing
depends on something the prediction graph does not model, such as a wire signal.

`AtomicBuildingPredictionSimulationSystem` discards the `BuildingInstance` — it builds from
an `IFactory<TSimulation>` — so subclass `AtomicBuildingSimulationSystem<ConnectableBuildingPredictionSimulation>`
directly and override the one hook. Everything else is inherited, and
`ConnectableBuildingPredictionSimulation` still pairs connectors exactly as for vanilla.

Three hazards if you do this, the first of which will bite you:

> [!CAUTION]
> **Do not reach it through the prediction system's own `BuildingInstance`.** Predictions
> run over `LazyEventMapLayout`, which queues map edits as `BuildingDescriptor`s in a
> `LookupQueue` — a `Dictionary`. `BuildingDescriptor.Equals`/`GetHashCode` compare
> definition, transform and configuration and **ignore `State`**, so when a placement ghost
> at a tile is removed and the real building at that tile is added in one lazy batch,
> `StoreBuildingAdded` finds the pending removal, treats them as the same entity and cancels
> both. The runner map keeps the *ghost's* instance, whose container nothing ever populates.
>
> The effect: entities that existed when the graph was built read correctly, every **newly
> placed** one reads an empty container forever, and toggling shape predictions appears to
> fix it.

Publish the container from the **real** simulation side instead, where there is no lazy
layer, and have the prediction look it up by tile:

```csharp
// real side, via ISimulationSystemsRewirer
public class MyStateObserver : ISpecializedBuildingObserverSimulationSystem
{
    public void BuildingWasAdded(in BuildingInstance building, IReadOnlyMapLayout layout)
        => Registry.Record(building.Transform.Position, building.State);

    public void BuildingWillBeRemoved(in BuildingInstance building, IReadOnlyMapLayout layout)
        => Registry.Forget(building.Transform.Position);
}
```

An observer rather than a tenant: the building already has a tenant, and observers are
additive, so nothing about the real simulation changes. Note that `Simulator.RevealBuilding`
runs observers *before* `OfferBuilding` creates the real simulation — which is why this
records the container and not the state:

> [!WARNING]
> **Hold the container, never the state object.** `SimulationStateContainer.New<T>()`
> *replaces* the state rather than populating it — `State = new T();` — and
> `AtomicStatefulBuildingSimulationSystem.CreateConnectableSimulation` calls it every time
> the real simulation is built. A state reference captured before that moment is orphaned
> the instant it happens. Re-resolve with `Container.Is<T>(out var state)` on every update;
> it is one `as` cast.

Orphaned state does not announce itself. It goes stale in two ways, and the second defeats
every null check you would think to write:

- the state was never used to build a simulation, so the fields that simulation's
  constructor assigns are still `null`; or
- the state *was* used by a now-superseded simulation, so those fields hold perfectly valid
  objects — a conductor, a lane, a buffer — that nothing is connected to any more. Reads
  succeed, nothing throws, and the value is simply always the empty default.

The failure this produces is selective enough to send you hunting in the wrong place:
entities that already existed when the prediction graph was built behave correctly, while
every **newly placed** one silently falls back forever. Toggling shape predictions off and
on appears to fix it — that rebuilds the graph after the real state exists — which makes a
dangling reference look like graph staleness.

- **Nothing orders the two systems.** The prediction can be constructed before the real
  simulation, so any state field that the real simulation's constructor populates is still
  null. `BeltFilterSimulationState.CurrentSignal`, for instance, dereferences
  `InputConductorState.InputConductor`, which `SignalConductorInput`'s constructor assigns —
  reach through it with a null check rather than calling the convenience property.
- **Signal reads are tick-sensitive.** `SignalBuffer.GetMostRecent()` returns `NullSignal`
  unless the buffer was written on the current start tick. Predictions update on their own
  round-robin (`PredictionUpdateStrategy`), not in step with the signal tick, so a raw read
  flickers between the real value and "nothing". Latch the last concrete value instead.
  `GetMostRecent(force: true)` skips the freshness test but indexes with a plain `%` where
  `TryPopSignal` uses `FastMath.SafeMod`, so on a never-written buffer the index can go
  negative.

Keep the fallback honest: where the live state is unavailable, answer what vanilla would
have answered. A prediction that blanks a readout it used to populate reads as a bug in
your mod, even when it is technically more correct.

## What vanilla prediction systems look like

Worth reading before writing one:

| Class | Predicts |
| --- | --- |
| `AtomicBuildingPredictionSimulationSystem` | the generic single-building case |
| `AtomicIslandPredictionSimulationSystem` | the island equivalent |
| `RailOpenOutputsPredictionSimulationSystem` | where rail outputs would go |
| `TrainLauncherCatcherPredictionSimulationSystem` | train launch/catch pairing |
| `TrainCargoLoaderPredictionSubSimulationSystem` | cargo loading |
| `TrashPredictionSimulationSystem` | the trash island — the simplest of the set |

`TrashPredictionSimulationSystem` is the one to start from: minimal, and it shows the
shape of a prediction sub-simulation without train complexity.

## Gotchas

- **Predictions must agree with reality.** If your prediction says one thing and your
  simulation does another, players will report it as a bug in your building — and they
  will be right. Derive both from the same operation where you can, which is exactly
  what the 1-in-1-out builder achieves.
- Predictions run on placement, i.e. during interactive dragging. Keep them cheap; a
  slow prediction shows up as input lag on the placement preview.
- A prediction has no real inputs. It is computing "given an input of X, what comes
  out" — do not reach for live lane state inside one.
- Islands and buildings have separate prediction paths; adding an island with
  simulation means the island variant.

> [!NOTE]
> `DiagonalCutter` is the only sample with a prediction, and it uses the generic
> 1-in-1-out builder rather than a hand-written system. Anything beyond that shape is
> unexplored — read the vanilla systems above and verify against your game version.
