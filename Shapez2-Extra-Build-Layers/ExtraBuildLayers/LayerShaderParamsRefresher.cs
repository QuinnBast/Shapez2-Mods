using System;
using MonoMod.RuntimeDetour;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ShapezShifter.SharpDetour;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Keeps the shader's idea of how many layers exist in step with the player's.
///
/// <c>LayerRenderingController</c> captures the layer bounds **once**, in its constructor:
///
/// <code>
/// short maximumAllowedBuildingLayer = InteractionMode.GetMaximumAllowedBuildingLayer(LocalPlayer);
/// short minimumAllowedIslandLayer   = InteractionMode.GetMinimumAllowedIslandLayer(LocalPlayer);
/// short maximumAllowedIslandLayer   = InteractionMode.GetMaximumAllowedIslandLayer(LocalPlayer);
/// LayersRenderingController = new LayerRenderingController(…, maximumAllowedBuildingLayer, …);
/// </code>
///
/// and holds them in <c>readonly</c> fields that feed the global shader vectors:
///
/// <code>
/// // LayerRenderingController.SetShaderParams
/// float2.y = interpolatedBuildingLayer + (float)MaxAllowedBuildingLayer + 1f;   // _G_LayersParams
/// float3.x = interpolatedIslandLayer   + (float)MinAllowedIslandLayer;          // _G_IslandLayersParams
/// float3.y = interpolatedIslandLayer   + (float)MaxAllowedIslandLayer;
/// </code>
///
/// Those bounds are what the shaders fade against, so **anything above the ceiling that was
/// in force when the session started renders as hidden**. In vanilla this barely shows: you
/// unlock a layer rarely and reload soon after. With ten layers bought in one session it is
/// the whole feature going invisible, which reads as "the mod did not work" rather than as a
/// stale uniform.
///
/// The fields cannot simply be reassigned - they are <c>readonly</c>, so even publicized they
/// need reflection - and writing them would not republish the uniforms anyway. Instead this
/// re-sets the two vectors from live values: as a postfix on <c>SetShaderParams</c>, so every
/// update the game makes is corrected in place, and from a tick when the bounds actually
/// change, because no event fires on a research unlock.
/// </summary>
internal sealed class LayerShaderParamsRefresher : ITickRewirer, IDisposable
{
    private readonly ILogger Logger;
    private readonly Hook SetShaderParamsHook;

    private short LastMaxBuildingLayer = short.MinValue;
    private short LastMinIslandLayer = short.MinValue;
    private short LastMaxIslandLayer = short.MinValue;
    private bool Complained;

    public LayerShaderParamsRefresher(ILogger logger)
    {
        Logger = logger;
        SetShaderParamsHook = DetourHelper.CreatePostfixHook(
            (LayerRenderingController controller) => controller.SetShaderParams(), _ => Apply());
    }

    public void Dispose()
    {
        SetShaderParamsHook?.Dispose();
    }

    public void Tick(float deltaTime)
    {
        try
        {
#pragma warning disable 0618   // IGameSessionManagers is [Obsolete("Inject only what you need")]; GameHelper.Core is the only way in from a mod.
            IGameSessionManagers core = GameHelper.Core;
#pragma warning restore 0618
            if (core?.LocalPlayer == null || core.InteractionMode == null)
            {
                return;
            }

            short maxBuilding = core.InteractionMode.GetMaximumAllowedBuildingLayer(core.LocalPlayer);
            short minIsland = core.InteractionMode.GetMinimumAllowedIslandLayer(core.LocalPlayer);
            short maxIsland = core.InteractionMode.GetMaximumAllowedIslandLayer(core.LocalPlayer);

            if (maxBuilding == LastMaxBuildingLayer && minIsland == LastMinIslandLayer && maxIsland == LastMaxIslandLayer)
            {
                return;
            }

            LastMaxBuildingLayer = maxBuilding;
            LastMinIslandLayer = minIsland;
            LastMaxIslandLayer = maxIsland;

            Apply();

            Logger.Info?.Log(
                "Extra Build Layers: layer bounds changed - building <=" + maxBuilding +
                ", island " + minIsland + ".." + maxIsland + "; shader params refreshed.");
        }
        catch (Exception exception)
        {
            Complain(exception);
        }
    }

    /// <summary>
    /// Recomputes both vectors exactly as <c>SetShaderParams</c> does, but with the bounds read
    /// now rather than the ones captured when the session began. The other components are
    /// recomputed too rather than read back, because there is no way to read a global vector.
    /// </summary>
    private void Apply()
    {
        try
        {
#pragma warning disable 0618   // IGameSessionManagers is [Obsolete("Inject only what you need")]; GameHelper.Core is the only way in from a mod.
            IGameSessionManagers core = GameHelper.Core;
#pragma warning restore 0618
            Viewport viewport = core?.Viewport;
            if (viewport == null || core.LocalPlayer == null || core.InteractionMode == null)
            {
                return;
            }

            float buildingLayer = viewport.InterpolatedBuildingLayer;
            float islandLayer = viewport.InterpolatedIslandLayer;

            short maxBuilding = core.InteractionMode.GetMaximumAllowedBuildingLayer(core.LocalPlayer);
            short minIsland = core.InteractionMode.GetMinimumAllowedIslandLayer(core.LocalPlayer);
            short maxIsland = core.InteractionMode.GetMaximumAllowedIslandLayer(core.LocalPlayer);

            Shader.SetGlobalVector(GlobalShaderInputs.LayersParams, new Vector4(
                buildingLayer + 1f,
                buildingLayer + maxBuilding + 1f,
                viewport.ShowAllBuildingLayers ? 1f : 0f,
                buildingLayer));

            bool showAllIslands =
                core.LocalPlayer.InteractionState.BaseState == PlayerInteractionBaseState.Islands &&
                viewport.ShowAllIslandLayers;

            Shader.SetGlobalVector(GlobalShaderInputs.IslandLayersParams, new Vector4(
                islandLayer + minIsland,
                islandLayer + maxIsland,
                showAllIslands ? 1f : 0f,
                islandLayer));
        }
        catch (Exception exception)
        {
            Complain(exception);
        }
    }

    private void Complain(Exception exception)
    {
        if (Complained)
        {
            return;
        }

        Complained = true;
        Logger.Exception?.LogException(exception);
        Logger.Error?.Log(
            "Extra Build Layers: could not refresh the layer shader parameters. Floors above the " +
            "ceiling that was in force when the session started may render as hidden until a reload.");
    }
}
