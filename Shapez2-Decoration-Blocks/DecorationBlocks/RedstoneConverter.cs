using Core.Factory;
using Game.Content.Features.Signals;
using Game.Content.Features.Signals.Conductor;
using Game.Content.Features.Signals.Connections;
using Game.Content.Features.Signals.Simulation;
using Game.Content.Features.Signals.Tick;
using Game.Core.Coordinates;
using Game.Core.Simulation;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// The bridge between redstone and shapez wires, in one building, both ways at once.
///
/// Wire in at the back, wire out at the front, and redstone on all four sides like any other
/// component:
///
/// * anything on the **wire input** that is not Off broadcasts redstone 15 into whatever dust
///   touches the converter;
/// * any **redstone power** reaching the converter's tile puts a true on the wire output.
///
/// Both directions run at once and they do not feed each other - the wire input and the wire
/// output are different connectors on different networks, which is what makes one building able
/// to do both. A single shared wire port could not: the building would be a provider and a
/// receiver on the same network and would read back its own contribution.
///
/// ## The two things that make this awkward
///
/// **A simulation is not told where it is.** `IFactory&lt;T&gt;.Produce()` takes no position, so this
/// object has no idea which tile it sits on and cannot look itself up in `RedstoneWorld`. The
/// way across is the other direction: the redstone tick walks its own converters, and for each
/// one calls `map.Simulator.TryFindTileSimulation(tile, …)`, which resolves a tile to the
/// simulation sitting on it. See `RedstoneWorld.ExchangeWithConverters`.
///
/// **The two sides run on different threads.** `Simulator.StartAsynchronousUpdate` returns a
/// `Task`, so `Update` below runs on a pool thread, while the redstone tick is postfixed onto
/// `GameSessionOrchestrator.Tick` and runs on the main one. So exactly two `volatile bool`s cross
/// between them, each written by one side and read by the other. Nothing else passes, and a
/// single-word write needs no lock.
public sealed class RedstoneConverterSimulation : ISignalSimulation, ISimulation, IUpdatableSimulation
{
    private readonly SignalConductorInput InputConductor;

    private readonly SignalConductorOutput OutputConductor;

    public RedstoneConverterSimulation()
    {
        // The conductor's state is normally a field on a serialised `ISimulationState`, reached
        // through the stateful branch of Shifter's builder chain. It is built here instead, and
        // the only consequence is that a signal in flight is not saved - which costs nothing,
        // because the network recomputes its value every tick anyway.
        InputConductor = new SignalConductorInput(new SignalConductorInputState());
        OutputConductor = new SignalConductorOutput();
    }

    public int NumSignalProviders => 1;

    public int NumSignalReceivers => 1;

    /// Set on the simulation thread from the wire, read on the main thread by the redstone tick.
    public volatile bool WireIsOn;

    /// Set on the main thread by the redstone tick, read on the simulation thread to drive the
    /// wire output.
    public volatile bool RedstoneIsOn;

    public ISignalProvider GetSignalProvider(int index)
    {
        return OutputConductor;
    }

    public ISignalReceiver GetSignalReceiver(int index)
    {
        return InputConductor;
    }

    public void Update(Ticks startTicks, Ticks deltaTicks)
    {
        // Copied in shape from LogicGateNotSimulation, which is the smallest thing in the game
        // that both reads and writes a wire: pop one signal per signal-tick, push one back.
        int signals = SignalSimulation.GetAmountOfSignalsThisUpdate(startTicks, deltaTicks);
        SignalTicks ticks = SignalTicks.FromTicks(startTicks);
        bool truthy = WireIsOn;

        for (int i = 0; i < signals; i++)
        {
            SignalTicks tick = new SignalTicks(ticks.NumOfTicks + i);

            InputConductor.TryPopSignal(startTicks, tick, out ISignal value);

            // "Anything that is not Off". IsTruthy is the game's own test and treats NullSignal
            // and a zero as false, which is exactly the rule asked for.
            truthy = value.IsTruthy();

            OutputConductor.PushSignal(IntegerSignal.Get(RedstoneIsOn), startTicks, tick);
        }

        WireIsOn = truthy;
    }
}

internal sealed class RedstoneConverterFactory : IFactory<RedstoneConverterSimulation>
{
    /// A new instance per placed building, unlike the inert simulations the other components
    /// share: this one carries the two fields the bridge passes across, so it has to be per
    /// building rather than a singleton.
    public RedstoneConverterSimulation Produce()
    {
        return new RedstoneConverterSimulation();
    }
}

internal sealed class RedstoneConverterFactoryBuilder
    : IBuildingSimulationFactoryBuilder<RedstoneConverterSimulation>
{
    private static readonly RedstoneConverterFactory Factory = new RedstoneConverterFactory();

    public IFactory<RedstoneConverterSimulation> BuildFactory(SimulationSystemsDependencies dependencies)
    {
        return Factory;
    }
}
