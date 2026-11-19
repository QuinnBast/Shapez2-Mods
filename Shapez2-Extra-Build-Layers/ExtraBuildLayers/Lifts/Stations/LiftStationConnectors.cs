using System;
using System.Reflection;
using Game.Content.Features.Predictions;
using Game.Content.Features.SpacePaths;
using Game.Content.Features.SpacePaths.Prediction;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Simulation;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Joins each entry station to its exit through the simulation graph, as if they were neighbours.
///
/// <c>ConnectableIslandSimulation</c> builds an island's simulation connectors from the
/// definition's connector data - one per declared space path input and output - and the
/// <c>SimulationGraph</c> then pairs connectors whose sockets match
/// (<c>SimulationConnectorSocket.From(pivot)</c> against the other side's
/// <c>CounterpartConnector()</c>). Paired connectors land in one cluster, which is what decides
/// both that items are handed across (<c>ItemOutputChunkConnector.TryConnect</c> sets the
/// provider bundle's <c>NextBundle</c>) and that the two update in a safe order on one thread.
///
/// A definition is fixed, so it cannot declare a connector at a height that differs per instance.
/// But the connector list is built per instance, in a constructor, from a list that stays mutable
/// - so a postfix adds one more: an output at the top of the entry's shaft and an input on the
/// exit, at <see cref="LiftStationIds.ShaftPivots"/>, computed from the climb the station's
/// configuration records. The graph then treats a 500-level climb exactly like two adjacent belts.
///
/// Linking the bundles by hand instead would put the two stations in separate clusters, which
/// the simulator updates in parallel - a race on the lane between them.
///
/// The prediction graph is built the same way by <c>ConnectableIslandPredictionSimulation</c>,
/// and gets the same treatment, or everything downstream of an exit reads as empty.
/// </summary>
internal sealed class LiftStationConnectors : IDisposable
{
    private delegate void SimulationCtor(ConnectableIslandSimulation self, IslandInstance island, ISimulation simulation);

    private delegate void SimulationCtorHook(SimulationCtor orig, ConnectableIslandSimulation self, IslandInstance island, ISimulation simulation);

    private delegate void PredictionCtor(ConnectableIslandPredictionSimulation self, IslandInstance island, ISimulation simulation);

    private delegate void PredictionCtorHook(PredictionCtor orig, ConnectableIslandPredictionSimulation self, IslandInstance island, ISimulation simulation);

    private readonly ILogger Logger;
    private readonly Hook Simulation;
    private readonly Hook Prediction;
    private bool Complained;

    public LiftStationConnectors(ILogger logger)
    {
        Logger = logger;

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            Simulation = new Hook(
                typeof(ConnectableIslandSimulation).GetConstructor(new[] { typeof(IslandInstance), typeof(ISimulation) }),
                new SimulationCtorHook(AfterSimulation));
            Prediction = new Hook(
                typeof(ConnectableIslandPredictionSimulation).GetConstructor(new[] { typeof(IslandInstance), typeof(ISimulation) }),
                new PredictionCtorHook(AfterPrediction));
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            logger.Error?.Log("Extra Build Layers: could not hook island simulation connectors; lift stations will not carry items.");
        }
    }

    private void AfterSimulation(SimulationCtor orig, ConnectableIslandSimulation self, IslandInstance island, ISimulation simulation)
    {
        orig(self, island, simulation);

        try
        {
            if (!TryShaft(island, out LiftPath path, out LiftEnd end, out GlobalChunkPivot entryOutput, out GlobalChunkPivot exitInput))
            {
                return;
            }

            ConnectableIslandSimulation connectable = self;
            if (end == LiftEnd.Entry && simulation is LiftEntrySimulation entry)
            {
                connectable.Connectors.Add(path == LiftPath.Belt
                    ? new ItemOutputChunkConnector<ShapeItem>(entry.Top, in entryOutput)
                    : new ItemOutputChunkConnector<FluidPackageItem>(entry.Top, in entryOutput));
            }
            else if (end == LiftEnd.Exit && simulation is LiftExitSimulation exit)
            {
                connectable.Connectors.Add(path == LiftPath.Belt
                    ? new ItemInputChunkConnector<ShapeItem>(exit.Segment, in exitInput)
                    : new ItemInputChunkConnector<FluidPackageItem>(exit.Segment, in exitInput));
            }
        }
        catch (Exception exception)
        {
            Complain(exception);
        }
    }

    private void AfterPrediction(PredictionCtor orig, ConnectableIslandPredictionSimulation self, IslandInstance island, ISimulation simulation)
    {
        orig(self, island, simulation);

        try
        {
            if (simulation is not LiftStationPredictionSimulation prediction ||
                !TryShaft(island, out _, out LiftEnd end, out GlobalChunkPivot entryOutput, out GlobalChunkPivot exitInput))
            {
                return;
            }

            ConnectableIslandPredictionSimulation connectable = self;
            connectable.Connectors.Add(end == LiftEnd.Entry
                ? new ItemPredictionOutputChunkConnector(prediction.Bundle, in entryOutput)
                : new ItemPredictionInputChunkConnector(prediction.Bundle, in exitInput));
        }
        catch (Exception exception)
        {
            Complain(exception);
        }
    }

    /// <summary>The two shaft pivots for a station that is part of a climb; false for anything else.</summary>
    private static bool TryShaft(
        IslandInstance island, out LiftPath path, out LiftEnd end,
        out GlobalChunkPivot entryOutput, out GlobalChunkPivot exitInput)
    {
        entryOutput = default;
        exitInput = default;
        if (island.Definition == null || !LiftStationIds.TryParse(island.Definition.Id, out path, out end))
        {
            path = default;
            end = default;
            return false;
        }

        int rise = LiftStationConfiguration.RiseOf(island.Configuration);
        if (rise == 0)
        {
            return false;
        }

        GlobalChunkCoordinate here = island.Transform.Position;
        GlobalChunkCoordinate exitAt = end == LiftEnd.Exit
            ? here
            : new GlobalChunkCoordinate(here.x, here.y, (short)(here.z + rise));
        LiftStationIds.ShaftPivots(exitAt, rise, out entryOutput, out exitInput);
        return true;
    }

    private void Complain(Exception exception)
    {
        if (!Complained)
        {
            Complained = true;
            Logger.Exception?.LogException(exception);
        }
    }

    public void Dispose()
    {
        Simulation?.Dispose();
        Prediction?.Dispose();
    }
}
