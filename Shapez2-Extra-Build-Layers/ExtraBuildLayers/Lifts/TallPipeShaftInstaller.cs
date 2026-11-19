using System;
using System.Collections.Generic;
using Game.Orchestration;
using MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Decorates the fluid renderer's per-definition map so tall pipes draw a full shaft.
///
/// <c>FluidNetworkRenderer</c> holds
/// <c>IReadOnlyDictionary&lt;BuildingDefinitionId, ICustomFluidNetworkBuildingRenderer&gt;</c>,
/// built by <c>GameSessionOrchestrator.CreateSimulationRenderers</c> from the pipe groups. The
/// stretched pipes are internal variants of the Layer2Pipe group, so they are already in it,
/// already mapped to vanilla's <c>PipeUpDownCustomFluidNetworkBuildingRenderer</c>. Each such
/// entry is swapped for a <see cref="TallPipeShaftRenderer"/> wrapping the original, so the
/// fluid still draws exactly as before and the bodies are added on top.
///
/// Hooked rather than rewired because Shifter has no seam here - there is no renderers rewirer -
/// and <c>CreateSimulationRenderers</c> is a private, non-generic, non-iterator method returning
/// the finished list, which is the one spot where the map exists and is still reachable.
///
/// The field is declared `IReadOnlyDictionary` but the instance passed in is a `Dictionary`, so
/// the swap is a cast rather than a rebuild. If that ever stops being true the cast fails, the
/// error is logged once, and pipes simply keep the gap they have today.
/// </summary>
internal sealed class TallPipeShaftInstaller : IDisposable
{
    private readonly ILogger Logger;
    private readonly Hook RenderersHook;

    private bool Complained;

    public TallPipeShaftInstaller(ILogger logger)
    {
        Logger = logger;
        RenderersHook = DetourHelper.CreatePostfixHook(
            (GameSessionOrchestrator orchestrator) => orchestrator.CreateSimulationRenderers(), Postfix);
    }

    public void Dispose()
    {
        RenderersHook?.Dispose();
    }

    private IEnumerable<ISimulationRenderer> Postfix(
        GameSessionOrchestrator orchestrator, IEnumerable<ISimulationRenderer> renderers)
    {
        if (renderers == null || ExtraLiftBuildings.SpanByDefinition.Count == 0)
        {
            return renderers;
        }

        try
        {
            int decorated = 0;

            foreach (ISimulationRenderer renderer in renderers)
            {
                if (renderer is not FluidNetworkRenderer fluid ||
                    fluid.CustomFluidNetworkBuildingRenderers is not Dictionary<BuildingDefinitionId, ICustomFluidNetworkBuildingRenderer> map)
                {
                    continue;
                }

                foreach (KeyValuePair<BuildingDefinitionId, int> entry in ExtraLiftBuildings.SpanByDefinition)
                {
                    // Only ids this map already knows - a belt lift is in SpanByDefinition too and
                    // has no business in the fluid renderer.
                    if (!map.TryGetValue(entry.Key, out ICustomFluidNetworkBuildingRenderer inner) ||
                        inner is TallPipeShaftRenderer)
                    {
                        continue;
                    }

                    map[entry.Key] = new TallPipeShaftRenderer(inner, entry.Value);
                    decorated++;
                }
            }

            if (decorated > 0)
            {
                Logger.Info?.Log(
                    "Extra Build Layers: tall pipe shafts enabled on " + decorated + " definition(s).");
            }
        }
        catch (Exception exception)
        {
            if (!Complained)
            {
                Complained = true;
                Logger.Exception?.LogException(exception);
                Logger.Error?.Log(
                    "Extra Build Layers: could not decorate the fluid renderer; tall pipes will draw " +
                    "a two-layer body with a gap above it.");
            }
        }

        return renderers;
    }
}
