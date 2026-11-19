using System;
using System.Collections.Generic;
using Core.Localization;
using Game.Core.Coordinates;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// The side panel of a selected lift station: a button that takes the player to the other end of
/// the climb - "Jump to exit" on an entry, "Jump to entry" on an exit.
///
/// The button is vanilla's <c>HUDSidePanelModuleGenericButton</c>, which takes a label and an
/// action. The action moves the view to the other station's layer (the viewport eases the camera
/// as the layer keys do) and selects that station, so its panel opens with the button pointing
/// back. Both stations stand in the same column, so only the layer changes.
///
/// The viewport and the selection come from <c>StaticGameCoreAccessor.G</c>: a module provider is
/// handed only the island, and the button fires long after the provider returned.
/// </summary>
internal sealed class LiftStationPanel : IIslandModuleDataProvider
{
    private readonly LiftEnd End;

    public LiftStationPanel(LiftEnd end)
    {
        End = end;
    }

    public IEnumerable<IHUDSidePanelModuleData> GetStats()
    {
        return Array.Empty<IHUDSidePanelModuleData>();
    }

    public IEnumerable<IHUDSidePanelModuleData> GetModules(IslandModel island)
    {
        if (island == IslandModel.Invalid)
        {
            yield break;
        }

        int rise = LiftStationConfiguration.RiseOf(island.Configuration);
        if (rise == 0)
        {
            yield break;
        }

        GlobalChunkCoordinate here = island.Transform.Position;
        int offset = End == LiftEnd.Entry ? rise : -rise;
        GlobalChunkCoordinate there = new GlobalChunkCoordinate(here.x, here.y, (short)(here.z + offset));

        string key = End == LiftEnd.Entry
            ? "extra-build-layers.lift-station.jump-to-exit"
            : "extra-build-layers.lift-station.jump-to-entry";

        yield return new HUDSidePanelModuleGenericButton.Data(
            key.T().Bind("layer", new RawText(there.z.ToString())),
            () => Jump(there));
    }

    private static void Jump(GlobalChunkCoordinate to)
    {
#pragma warning disable CS0618 // The accessor is obsolete as a pattern, not removed; a button has nothing to inject.
        IGameSessionManagers session = StaticGameCoreAccessor.HasInstance() ? StaticGameCoreAccessor.G : null;
#pragma warning restore CS0618
        if (session?.Viewport == null)
        {
            return;
        }

        session.Viewport.IslandLayer = to.z;

        IslandModel other = IslandModel.Invalid;
        if (session.LocalPlayer?.CurrentMap != null && session.LocalPlayer.CurrentMap.TryGetIsland(to, out other) && other != IslandModel.Invalid)
        {
            session.LocalPlayer.InteractionState.IslandSelection.Set(new[] { other });
        }
    }
}
