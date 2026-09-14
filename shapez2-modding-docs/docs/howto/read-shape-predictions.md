# Read what shapes will flow where

**Problem.** You want to know which shape arrives at a belt, a port, or a machine —
without waiting for an item to physically get there, and without composing shape
operations by hand.

**Solution.** The game already knows. Alongside the real simulation it runs a **second
complete simulation whose only job is to work out which shapes can appear where**, and it
is readable from a mod.

This is the *reading* side of the prediction system.
[Placement previews](placement-predictions.md) is the *authoring* side — giving your own
building a prediction so its placement preview works.

## What the prediction graph is

A parallel simulation over the same map layout, built with a different set of systems.
Every processing building appears in it as its `IItemOperation` applied to a set of
possible items:

```csharp
// Processing1In1OutPredictionSimulation.Update
PredictedItem input = Input.PopPrediction();
PredictedItem predictedItem = operation1In1Out.Predict(in input);
Output.Push(in predictedItem);
```

`PredictedItem` is a readonly struct holding **at most four** distinct `IItem`s, with two
extension methods worth knowing:

```csharp
predicted.IsEmpty()        // nothing predicted here
predicted.IsDegenerated()  // more possibilities than the game will track
```

Because the propagation through belts, mergers and splitters is already done, the shape
transfer function of any subgraph can be *read* rather than derived — you never have to
reconstruct a connector graph or compose operations yourself.

## Reaching the simulator

It is a private field on the session, reachable because
[the publicizer](../publicizer.md) opens everything:

```csharp
ISimulator predictions = orchestrator.PredictionSimulator;
```

> [!WARNING]
> It is **null when shape predictions are switched off** in the game's settings, and the
> session throws it away and builds a new one when they are toggled. Read the field fresh
> every time; never cache the simulator.

From there it behaves like any [`ISimulator`](../simulations-and-lanes.md#finding-a-simulation):
`Simulations`, `TryFindChunkSimulation`, `FindAllConnectedSimulations`.

## Only ever read providers

Every prediction simulation implements `IItemPredictionSimulation`:

```csharp
int NumItemReceivers { get; }
int NumItemProviders { get; }
IItemPredictionReceiver GetItemReceiver(int index);
IItemPredictionProvider GetItemProvider(int index);
```

Read the value from a **provider**, which exposes it as a plain property:

```csharp
if (localized.Simulation is IItemPredictionSimulation prediction
    && prediction.NumItemProviders > 0)
{
    PredictedItem predicted = prediction.GetItemProvider(0).PredictedItem;
}
```

> [!WARNING]
> Do not call `ItemPredictionReceiver.PopPrediction()`. It **clears** the stored value as
> it returns it — that is how the graph moves predictions along — so calling it from a mod
> silently corrupts the game's own propagation.

`GetItemProvider` and `GetItemReceiver` have default interface implementations that
**throw `NotImplementedException`** rather than returning null, and a simulation only
overrides the side it actually has. Check the counts first, or catch:

```csharp
private static IItemPredictionProvider ProviderAt(IItemPredictionSimulation prediction, int index)
{
    if (index >= prediction.NumItemProviders) return null;
    try { return prediction.GetItemProvider(index); }
    catch (NotImplementedException) { return null; }
}
```

## Predictions flow downstream from sources

The single most important thing to understand, and the one that costs an implementation if
you miss it:

> [!IMPORTANT]
> A subgraph with no source inside it has **no predictions at all**. Predictions propagate
> forward from extractors and other producers. An isolated copy of a factory — one you
> assembled yourself, or a blueprint expanded somewhere private — predicts nothing,
> because nothing is feeding it.

Concrete shapes appear on a live factory's ports only because that factory is being fed
concrete shapes. So "what does this blueprint output" is not a question the prediction
graph can answer on its own; it answers "what does this blueprint output *given these
inputs*".

Propagation also moves **one simulation per update**, so after any change a deep graph
needs several passes before the far end settles.

## Degenerated does not mean "more than four"

It is tempting to read `PredictedItem.Degenerated` as the game's marker for "too many
possibilities to track". It is not. Running past four and giving up are two separate
behaviours, and only one of them is visible.

**Overflowing four is silent.** `PredictedItem` holds exactly four `IItem`s, and both places
that build one from a list simply stop reading at the fourth —
`PredictionCombinationExtensions.CombinePredictions` breaks out of its outer loop once it
has four, and `PredictedItem(List<IItem>)` only ever reads indices 0–3. A fifth possibility
is dropped with no marker of any kind. A four-shape readout can mean "exactly these four"
or "these and more", and nothing distinguishes them.

**`Degenerated` means the operation failed on everything it was handed.**
`ItemOperationPredictionExtensions.Predict` pushes it when no candidate produced an output
at all:

```csharp
if (scopedList.Count == 0 && input != PredictedItem.None)
{
    return PredictedItem.Degenerated;
}
```

So it is closer to "this machine cannot do anything with what is arriving" than to "this is
too complicated".

> [!WARNING]
> `Degenerated` is **absorbing, and it renders as blank.** Both
> `PassThroughItemPredictionLane.PushPrediction` and `ItemPredictionConverter.Update`
> convert it to `PredictedItem.None` on the way through, and a machine that receives it
> returns `None` as well (`Predict` starts with `if (input.IsDegenerated()) return
> PredictedItem.None;`). One degenerate point therefore empties the readout for everything
> downstream of it — which is what is really being reported when a player says their belts
> "stopped showing anything".

A degenerate branch also **disappears** rather than contaminating a merge:
`CombinePredictions` skips degenerated entries with `continue`, so merging a degenerate line
into a concrete one yields just the concrete one.

The practical consequence for a mod that reads predictions: `IsDegenerated()` at an output
tells you that point is broken or starved, not that the factory there is complex. And an
empty readout is ambiguous — it may mean nothing is coming, or it may mean something
upstream degenerated several buildings ago.

## Vanilla predictions are not all accurate

Worth knowing before you trust a reading, or reimplement something the game "already does":

**The belt filter is predicted as a plain splitter.**
`BuiltinPredictionSimulationSystems.CreateFlowControlSystems` registers it with
`SplitterPredictionSimulationFactory(2)`, whose `Update` pushes the same set to every
output — so a filter's match belt and mismatch belt both claim to carry everything on the
line, even though `SignalControlledDistributionBehaviour.TryFindNextLane` routes them
precisely. The belt reader and pipe gate registered next to it are fine; they use
`ForwardingPredictionSimulation`, which is what they really do.

This matters beyond the filter itself, because an over-broad set is what pushes a line past
the silent four-item cap and hands downstream machines shapes they cannot process — which
is how a merely imprecise prediction turns into a blank one.

## Gotchas found the hard way

**A space port is split in two.** `PredictionSpacePathPortSystem` builds the building half
facing inward and a *separate* buffer simulation two tiles out to carry the hop across
space. So a space **output** port's own simulation has no provider to read — it has only a
receiver. Reach its shapes through whatever feeds it, by indexing every provider in the
region by `provider.Next`:

```csharp
Dictionary<IItemPredictionReceiver, IItemPredictionProvider> feeders = new();
// ...for each provider: if (provider.Next != null) feeders[provider.Next] = provider;

IItemPredictionReceiver receiver = sender.GetItemReceiver(0);
if (feeders.TryGetValue(receiver, out IItemPredictionProvider feeder))
{
    PredictedItem predicted = feeder.PredictedItem;
}
```

**Belt ports and fluid ports use the same classes.** `PredictionSpacePathPortSystem<TInput,
TOutput>` is generic over its connector types, so `SpacePortSenderPredictionSimulation`
serves both. Tell them apart by what the prediction *contains* — a `ShapeItem` versus an
`IFluid` or `FluidPackageItem` — not by the simulation type.

**Do not identify ports by their prediction simulation type.** Every conveyor on a platform
is also a `ForwardingPredictionSimulation`, because `ConveyorPredictionSimulationSystem`
uses one — so testing for that type matches the whole belt network rather than the handful
of ports. Discriminate on the *localized wrapper* instead:

| Wrapper | What it is |
| --- | --- |
| `IConnectablePort` | a genuine port transfer between two port buildings |
| `ConnectableBeltPortSender` | a port building with nothing docked on the far side |
| `ConnectablePathPredictionSimulation` | a belt run — not a port |

See [Ports and Notches](../ports-and-notches.md) for the equivalent taxonomy on the real
simulation graph.

**Off-screen platforms still predict.** Clusters absent from the culler's LOD list are
updated as `SimulationLOD.Lowest`, and `PredictionUpdateStrategy` still schedules those on
a round-robin — so predictions exist for platforms the camera is nowhere near, just
staler ones.

> [!NOTE]
> Derived from the game's own implementation and from a mod that reads the graph, not from
> an official sample. Verify the type names against your game version.
