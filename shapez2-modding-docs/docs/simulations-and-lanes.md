# Simulations and Item Lanes

> **You need this when** you want to know what a machine is *doing* — running, starved,
> or backed up. Task-shaped version:
> [Read machine state](howto/read-machine-state.md).


A `BuildingModel` says a machine *exists*. A **simulation** is the object that makes it
*do* something. If you want to know what is on a belt, whether a cutter is running, or
why a factory is stalled, this is the layer you need.

## Finding a simulation

`IMapModel.Simulator` is an `ISimulator`:

```csharp
public interface ISimulator : ISimulationTimeProvider
{
    IEnumerable<ILocalizedSimulation> Simulations { get; }
    IEvent<ILocalizedSimulation> OnSimulationCreated { get; }
    IEvent<ILocalizedSimulation> OnBeforeSimulationDestroyed { get; }

    ILocalizedTileSimulation FindTileSimulation(in GlobalTileCoordinate position);
    bool TryFindTileSimulation(in GlobalTileCoordinate position, out ILocalizedTileSimulation simulation);
    bool TryFindChunkSimulation(in GlobalChunkCoordinate position, out ILocalizedChunkSimulation simulation);

    void FindAllConnectedSimulations(ILocalizedSimulation simulation, ICollection<ILocalizedSimulation> targetResults);
    bool TryGetConnectedSimulation(ILocalizedSimulation simulation, int index, out ILocalizedSimulation inputSimulation);

    TSystem GetSystem<TSystem>() where TSystem : ISimulationSystem;
    bool TryGetSystem<TSystem>(out TSystem system) where TSystem : ISimulationSystem;
    IEnumerable<TSystem> GetSystems<TSystem>() where TSystem : ISimulationSystem;

    Ticks GetSimulationTimeFor(ILocalizedSimulation simulation);
    Ticks GetSimulationUpdateDeltaTimeFor(ILocalizedSimulation simulation);
}
```

Two ways in, and they suit different jobs:

```csharp
// From a building you already have:
if (map.Simulator.TryFindTileSimulation(building.Tile_G, out ILocalizedTileSimulation localized))
{
    ISimulation simulation = localized.Simulation;
}

// Or sweep everything (belts included — this is a big list):
foreach (ILocalizedSimulation localized in map.Simulator.Simulations) { }
```

`FindAllConnectedSimulations` walks the graph outward from one simulation, which is the
starting point for anything that needs to follow a production chain upstream or
downstream.

### `ILocalizedSimulation`

The wrapper that says *where* a simulation is:

```csharp
public interface ILocalizedSimulation
{
    ISimulation Simulation { get; }
    int NumOccupiedChunks { get; }
    GlobalChunkCoordinate GetOccupiedChunk(int index);
}

public interface ILocalizedTileSimulation : ILocalizedSimulation
{
    GlobalTileBounds TileBounds { get; }
    int NumOccupiedTiles { get; }
    GlobalTileCoordinate GetOccupiedTile(int index);
}

public interface ILocalizedChunkSimulation : ILocalizedSimulation
{
    GlobalChunkBounds ChunkBounds { get; }
}
```

Buildings are tile simulations; island-level things (space research stations, trains)
are chunk simulations.

## `IItemSimulation` — the generic machine

Most machines implement `IItemSimulation`, and this is the key to writing code that
works across *all* buildings without knowing their concrete types:

```csharp
public interface IItemSimulation : ISimulation
{
    int NumItemReceivers { get; }
    int NumItemProviders { get; }
    IItemReceiver GetItemReceiver(int index);   // its inputs
    IItemProvider GetItemProvider(int index);   // its outputs
    void TraverseLanes<TTraverser>(TTraverser traverser) where TTraverser : IItemLaneTraverser;
}
```

`TraverseLanes` visits every internal lane, including ones that are neither input nor
output:

```csharp
public struct LaneCounter : IItemLaneTraverser
{
    public int Occupied;
    public void Traverse(IItemLane lane)
    {
        if (lane.HasItem) Occupied++;
    }
}

LaneCounter counter = new LaneCounter();
itemSimulation.TraverseLanes(counter);
```

> [!NOTE]
> `TraverseLanes` is generic over a `struct` traverser specifically to avoid allocating
> and to let the JIT inline the callback. Passing a class works but gives up both.

> [!WARNING]
> `GetItemReceiver` and `GetItemProvider` are **default interface methods that throw**
> `NotImplementedException` unless the concrete type overrides them, and plenty of
> simulations implement only one side. Guarding with `try`/`catch` per instance is very
> expensive on a real save — one type appears tens of thousands of times — so cache the
> result per `Type` and let each type throw at most once.

### Not every item simulation is an `IItemSimulation`

Two more shapes exist, and code that only handles `IItemSimulation` has silent holes:

| Interface | Used by | Lanes |
|---|---|---|
| `IItemBundleSimulation` | space belts, space pipes | `TraverseLanes` over an `ItemLaneBundle`; several parallel lanes, four per building layer |
| neither | space *fluid* ports | none — fluid is packaged straight into a buffer |

A space belt is a bundle of `FastBeltPathLane`s and does **not** implement
`IItemSimulation`. A space pipe is the same thing carrying `FluidPackageItem`s.

### A belt run is one simulation

A whole path of belt is a single `ConveyorPathSimulation` with one `BeltPathLane` whose
`Slots` span the run — not one simulation per tile. It also returns **the same lane
object** from both accessors:

```csharp
public IItemReceiver GetItemReceiver(int index) => Lane;
public IItemProvider GetItemProvider(int index) => Lane;
```

Reference equality between an input and an output lane is therefore a reliable way to tell
transport from transformation, without naming any concrete type.

## The lane model

Items live on lanes. Three interfaces, layered:

```csharp
public interface IItemReceiver
{
    Steps MaxStep_S { get; }
    Steps FreeStepsAtTheBeginning { get; }
    bool CanAcceptItem(IBeltItem itemToTransfer);
    void HandOverItem(IBeltItem itemToTransfer, Ticks remainingTicks);
}

public interface IItemProvider
{
    IItemReceiver NextLane { get; set; }
    Steps FreeStepsAtTheEnd { get; }
}

public interface IItemLane : IItemReceiver, IItemProvider
{
    int ItemCount { get; }
    bool HasItem { get; }
    IBeltItem GetItem(int index);
    void Clear();
}
```

The important structural fact: **lanes are chained through `NextLane`**. A lane hands
its item to the next receiver when it reaches the end, and the whole factory is that
chain repeated. `CanAcceptItem` returning `false` is what backs a line up.

`SingleItemLane` is the common base (one item at a time) and adds:

```csharp
public abstract IBeltItem Item { get; protected set; }
public bool IsEmpty => Item == null;
public bool HasItem => Item != null;
public abstract float Progress { get; }      // 0..1 along the lane
public abstract Ticks Duration_T { get; }
```

`BeltLane` is the concrete workhorse, adding `Speed` (an `IBeltSpeed`), `Progress_S` in
steps, and conversions `S_From_T` / `T_From_S`. `DelayBeltLane` is the "processing"
variant — it holds an item for a fixed duration, which is how a machine's work time is
modelled.

### A machine, end to end

`HalfCutterSimulation` is representative of nearly every processing building:

```csharp
public class HalfCutterSimulation : Simulation<HalfCutterSimulationState>, IItemSimulation
{
    public readonly BeltLane      InputLane;
    public readonly DelayBeltLane ProcessingLane;
    public readonly BeltLane      OutputLane;

    public HalfCutterSimulation(HalfCutterSimulationState state, ICutterConfiguration config, …)
        : base(state)
    {
        // Built back to front, each lane pointing at the next:
        OutputLane     = new BeltLane(config.BeltSpeed, state.OutputLaneState);
        ProcessingLane = new DelayBeltLane(config.ProcessingDelay, state.ProcessingLaneState, OutputLane);
        InputLane      = new BeltLane(config.BeltSpeed, state.InputLaneState, ProcessingLane);

        // The actual work happens in a hook as the item is accepted:
        ProcessingLane.AcceptHook = delegate(IItemReceiver _, ref IBeltItem item, ref Ticks _)
        {
            // …transform `item` in place
        };
    }
}
```

Three things to take from that:

1. **Lanes are constructed back to front**, each taking the next as its receiver.
2. **The state object holds the lane states**, not the lanes — the lanes are rebuilt on
   load around the deserialized state. That is why `SimulationStateContainer` holds
   `…SimulationState`, not the simulation.
3. **Work happens in `AcceptHook`**, at the moment an item transfers, not in an update
   loop.

## Lane hooks

`SingleItemLane` implements `IHookableItemReceiver` and exposes four hook points:

```csharp
public PreAcceptHookDelegate   PreAcceptHook   { get; set; }
public AcceptHookDelegate      AcceptHook      { get; set; }
public PostAcceptHookDelegate  PostAcceptHook  { get; set; }
public PostHandoverHookDelegate PostHandoverHook { get; set; }
```

These are the game's own extension mechanism, and you can borrow them — but always
**chain, never replace**:

```csharp
AcceptHookDelegate saved = lane.AcceptHook;

lane.AcceptHook = delegate(IItemReceiver receiver, ref IBeltItem item, ref Ticks remaining_T)
{
    // …your observation here
    saved?.Invoke(receiver, ref item, ref remaining_T);
};

// on dispose:
lane.AcceptHook = saved;
```

Overwriting without chaining silently breaks whatever the machine was doing in its own
hook — for the cutter above, it would stop cutting.

**For observation, prefer `PostAcceptHook`.** It is a plain multicast delegate — the game
combines onto it in `CargoPackageTrack` and `PathMergerSimulation` — so you can add and
remove your own without the save-and-restore dance:

```csharp
lane.PostAcceptHook = (PostAcceptHookDelegate)Delegate.Combine(
    lane.PostAcceptHook, new PostAcceptHookDelegate(OnItemAccepted));

// on dispose:
lane.PostAcceptHook = (PostAcceptHookDelegate)Delegate.Remove(
    lane.PostAcceptHook, new PostAcceptHookDelegate(OnItemAccepted));
```

That matters because the vanilla building-efficiency panel replaces `AcceptHook` on
whichever building the player selects, saving and restoring as it goes. Two parties doing
save-and-restore on the same slot will drop each other's hooks depending on detach order.
Reserve `AcceptHook` for when you need to *modify* the item in flight, which is what it is
for. See [measuring throughput](howto/measure-throughput.md).

## Worked example: is this machine running?

The vanilla side panel measures true throughput by timestamping every item that arrives
on the output lane over a 60-second window, then comparing the average interval against
the building's theoretical processing duration
(`HUDSidePanelModuleBuildingEfficiency`). It is accurate, and it costs one hook per
observed lane — fine for one selected building, expensive for ten thousand.

For a cheap classification across many buildings, read the chain state instead. No
hooks, no warm-up, two property reads:

```csharp
public enum MachineStatus { Unknown, Starved, Blocked, Running }

private static MachineStatus Classify(IItemSimulation simulation)
{
    bool anyInputHasItem = false;
    for (int i = 0; i < simulation.NumItemReceivers; i++)
    {
        if (simulation.GetItemReceiver(i) is IItemLane input && input.HasItem)
        {
            anyInputHasItem = true;
            break;
        }
    }

    for (int i = 0; i < simulation.NumItemProviders; i++)
    {
        if (simulation.GetItemProvider(i) is not IItemLane output) continue;
        if (!output.HasItem) continue;

        // An item sitting on the output whose next lane will not take it: backed up.
        IBeltItem item = output.GetItem(0);
        if (output.NextLane != null && !output.NextLane.CanAcceptItem(item))
        {
            return MachineStatus.Blocked;
        }
    }

    if (!anyInputHasItem) return MachineStatus.Starved;
    return MachineStatus.Running;
}
```

`Blocked` means the problem is *downstream*; `Starved` means it is *upstream*. That
distinction is usually more actionable to a player than a percentage.

> [!NOTE]
> This classification is a design recommendation derived from the lane contracts, not a
> vanilla mechanism. A single sample is instantaneous and noisy — sample a few times a
> second and keep a rolling ratio before showing anything to a player.

## Simulation systems

Simulations are updated by **systems** (`ISimulationSystem`), reachable via
`ISimulator.GetSystem<T>()` / `GetSystems<T>()`. Adding a new machine type means adding
a system that pattern-matches your building and creates your simulation — the
`DiagonalCutter` sample does exactly this, and ShapezShifter's Flow layer wires it up
for you when you use `Building.Create(...)`. Reading existing machines, as above,
requires no system of your own.

## Not everything is simulated every tick

Simulation cost is **not** flat in building count, which matters if you are reasoning about
performance or writing a mod that claims to reduce it.

Clusters are updated at a rate set by their level of detail, and LOD comes from **camera
distance** — `MapCuller` produces a `SimulationLOD` per visible chunk via
`LODRenderConfig.ComputeSimulationLOD`. `ProcessingUpdateStrategy` maps LOD to a required
interval through `RequiredUpdateTicksByLOD`, built as:

```csharp
array[i] = SimulationConstants.MaxSimulationUpdateDelta >> (num - i - 1);
```

So a distant cluster updates less often, with a larger delta each time — and because
`Update(Ticks deltaTicks)` advances a lane's progress in one step rather than iterating
ticks, a 60-tick update costs about the same as a 1-tick one. Those coarse updates really
are cheaper, not merely rarer.

Two consequences:

- **Anything off-screen already gets the maximum discount.** A cluster absent from the
  culler's list is updated as `SimulationLOD.Lowest` (`SimulationGraph.Update`), so moving
  buildings somewhere the camera never goes buys nothing that not looking at them did not
  already buy.
- Even at the lowest LOD a cluster still updates every `MaxSimulationUpdateDelta`, so
  nothing ever stops entirely.

If you are running [a simulation of your own](howto/run-a-detached-simulation.md), supply a
strategy that ignores LOD — there is no camera, and rationing would only make measurements
harder to read.

> [!TIP]
> The `prediction-graph` and simulation-LOD debug views (`F1` → debug modes) draw the
> cluster grid and how many ticks each cluster is behind. Faster than reasoning about it.

## A custom item type can be handed to a vanilla lane, and destroyed

If your island carries an `IBeltItem` the base game does not know about, the connector tags give
you no protection. `ConnectableIslandSimulation` maps a connector class to an item type with a
hard `is` test — `SpaceBeltOutputConnector` becomes `ItemOutputChunkConnector<ShapeItem>`, and
nothing else is reachable — so your output connects to an ordinary space belt exactly as it
connects to whatever you meant it for. Worse, `FastBeltPathLane.CanAcceptItem` returns `true` for
anything unless a `PreAcceptHook` is set, and vanilla space paths set none. Your item boards the
belt, rides away, and is destroyed by the first thing that casts it.

Subclassing the connector does not help: the `is` test still lands on `ShapeItem`, and
`IsCompatibleConnector` on both sides is `other is SpaceBeltInputConnector`. If a vanilla building
you *do* want to reach presents that same connector class — a train station, say — then no
type-level rule can separate the two.

Refuse the hand-over instead. A lane asks `NextLane.CanAcceptItem` before passing anything on, so
a receiver that answers no leaves the item where it is and the line backs up, which is the game's
own "this does not work" signal.

Two things make that practical:

- **Subclass the lane, overriding nothing.** Your lanes and vanilla's are the same class, so there
  is no question a sender can ask about the receiver. An empty subclass cannot change dispatch and
  gives you a type test.
- **Wrap `IItemProviderBundle.NextBundle`.** `ItemOutputChunkConnector.TryConnect` only ever
  assigns that one property, so returning a wrapper from `GetItemProviderBundle` catches every
  downstream the island can acquire. Have the wrapper's `GetReceiver` hand back a small
  `IItemReceiver` that answers `false` for your item when the real receiver cannot hold it.

> [!WARNING]
> **Do not stop at a `DummyLane`.** It is tempting to treat one as "a machine, therefore fine" —
> stations and most custom machines put one in front of their real receiver. But
> `NotchInputAdapterSimulation`, which is the platform belt port, is also twelve `DummyLane`s. A
> DummyLane holds nothing and forwards, so follow `NextLane` until you reach something that is not
> one, and decide there. Bound the walk; a cycle would hang the simulation rather than fail.

Cache the per-lane wrapper. `ItemLaneBundle.NextBundle`'s setter calls `GetReceiver` once for each
of the twelve lanes, and the receiver is then held for the life of the connection.
