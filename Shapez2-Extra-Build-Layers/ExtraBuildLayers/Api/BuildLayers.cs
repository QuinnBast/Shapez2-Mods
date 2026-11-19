using System;
using System.Collections.Generic;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// The mod's public surface. Another mod referencing this assembly drives everything from
/// here.
///
/// The counts and the policy are read **once**, while the scenario is built. Setting them
/// from another mod's constructor works, because mods are constructed during mod loading and
/// no scenario exists yet; setting them from inside a session does not, and is ignored until
/// the next one. <see cref="Registered"/> is how to find out what a session actually got.
///
/// Everything here starts from this mod's own config.json, so a player who installs nothing
/// else still gets a working mod, and a dependent mod overrides rather than initialises.
///
/// <para><b>Depending on it.</b> Reference <c>ExtraBuildLayers.dll</c> (compile-time only - do
/// not ship a copy) and list this mod under <c>Dependencies</c> in your manifest. That is what
/// makes your constructor run after this one: <c>ModLoader</c> resolves the dependency graph and
/// constructs mods in that order, so values set in your constructor override this mod's config
/// rather than being overwritten by it. Without the dependency the order is not guaranteed.</para>
///
/// <para><b>Not depending on it.</b> A mod that only wants to cooperate when this one happens to
/// be installed can find it by reflection - <c>Type.GetType("QuinnBast.Shapez2.ExtraBuildLayers.BuildLayers, ExtraBuildLayers")</c>
/// - and check <see cref="ApiVersion"/> before touching anything else.</para>
///
/// <para><b>Your own buildings work on the new floors without doing anything.</b> The game sizes
/// every building's per-layer mesh array to three and a building above that takes its chunk's
/// mesh down; this mod widens every definition, including ones another mod registers after it
/// (a second pass runs once all buildings rewirers have). Floors above
/// <see cref="PortConnectedMachineLevels"/> cannot carry a port onto a space belt or space pipe,
/// whatever the building.</para>
/// </summary>
public static class BuildLayers
{
    /// <summary>
    /// Raised when something here changes in a way a dependent mod would have to adapt to. 1 is
    /// the surface as first published: the counts and their <c>Require</c> methods, the policy,
    /// <see cref="Current"/> and <see cref="Registered"/>.
    /// </summary>
    public const int ApiVersion = 1;

    private static readonly object Gate = new object();
    private static LayerRegistration _Current;

    /// <summary>
    /// Extra building layers inside every platform, on top of whatever the scenario ships.
    ///
    /// Clamped so the resulting top layer never exceeds <see cref="MaxMachineLevel"/>, which is
    /// a genuine coordinate limit rather than a policy. Everything inside a platform scales to
    /// that; inter-platform ports do not, and stop at
    /// <see cref="PortConnectedMachineLevels"/> - see there, it is a gameplay constraint worth
    /// knowing about rather than a bug.
    /// </summary>
    public static int ExtraMachineLevels { get; set; }

    /// <summary>
    /// Extra platform layers in space, added above the current topmost one. Clamped so the
    /// resulting top layer never exceeds <see cref="MaxSpaceLevel"/>; asking for more is logged
    /// as an error and the excess dropped, never an exception. See DESIGN.md for what scales
    /// linearly with this number.
    /// </summary>
    public static int ExtraSpaceLevels { get; set; }

    /// <summary>
    /// Raises <see cref="ExtraMachineLevels"/> to at least <paramref name="extra"/>, never lowers
    /// it. Prefer this to assigning when your mod needs *some* number of floors rather than
    /// exactly that number: two mods that each assign overwrite each other in load order, two
    /// that each require both get what they asked for.
    /// </summary>
    public static void RequireExtraMachineLevels(int extra)
    {
        ExtraMachineLevels = Math.Max(ExtraMachineLevels, extra);
    }

    /// <summary>The <see cref="RequireExtraMachineLevels"/> of space layers.</summary>
    public static void RequireExtraSpaceLevels(int extra)
    {
        ExtraSpaceLevels = Math.Max(ExtraSpaceLevels, extra);
    }

    /// <summary>
    /// The highest building layer that can exist at all, and a hard clamp rather than advice.
    ///
    /// A building's layer is not stored; it is recovered from its tile's <c>z</c> by
    /// <c>GlobalTileCoordinateExtensions.BuildingLayer()</c>, which is
    /// <c>FastMath.SafeMod(z, 20)</c> - because <c>CoordinateConstants.TilesPerIslandLayer</c>
    /// is 20 and <c>ScreenUtils</c> stamps <c>z = IslandLayer * 20 + BuildingLayer</c>. Layer 20
    /// is therefore layer 0 of the island layer above, and a building there would alias onto
    /// another platform's ground floor. 19 is the last value that does not.
    /// </summary>
    public const int MaxMachineLevel = 19;

    /// <summary>
    /// The highest space layer this mod will create, as a clamp on the top layer rather than on
    /// the count requested.
    ///
    /// Unlike <see cref="MaxMachineLevel"/> this is a **safety margin, not a coordinate limit**.
    /// The coordinate limit is higher - a tile's <c>z</c> is a <c>short</c> holding
    /// <c>layer * 20 + floor</c>, so it runs out at layer 1637. Nothing this mod makes costs
    /// memory per layer any more (the grid's number is redrawn for the layer on screen, not
    /// stored for every layer), so what is left is:
    /// <list type="bullet">
    /// <item>Layer numbers are drawn for up to three digits. Two have been seen in game; three
    /// shrink to fit and have not; four would not fit at all.</item>
    /// <item>Tall space lifts are created for every span up to the top layer, so there are
    /// proportionally more of them, each one taller. Cheap per lift, never measured in bulk.</item>
    /// </list>
    /// It is a constant so a dependent mod can see the ceiling before asking.
    /// </summary>
    public const int MaxSpaceLevel = 999;

    /// <summary>
    /// How many building layers can carry a port onto a **space belt or space pipe**. Three -
    /// layers 0, 1 and 2 - and this cannot be raised from a mod.
    ///
    /// <c>SpacePathPortSystem.CreateAllSenderSimulations</c> and its receiver twin build the
    /// simulations that hand items between a platform and the space path at its edge, over a
    /// hardcoded <c>for (short num2 = 0; num2 &lt; 3; num2++)</c> of building layers. A belt or
    /// fluid port onto a space path above layer 2 gets no simulation, so nothing crosses there.
    ///
    /// It is **not** a limit on moving items between platforms in general: belts between two
    /// platforms that touch transfer on every floor - seen in play on floor 19. The class is
    /// <c>SpacePathPortSystem&lt;TInput, TOutput&gt;</c> - a **generic type**, whose methods
    /// MonoMod cannot hook - and <c>NotchConnectorsExtender.AddConnectors&lt;TMapLayout&gt;</c>
    /// has the matching placement-side bound in a generic *method*, equally unhookable.
    ///
    /// This is not a defect in the mod, and it reads as a design constraint in play: a tall
    /// factory has to bring its output back down to the bottom three floors to put it on a
    /// space belt or pipe. In-
    /// platform vertical movement is unaffected, and <c>LayerLiftPlacementTracker</c> already
    /// follows <c>MaxBuildingLayer</c>, so lifts reach every floor.
    /// </summary>
    public const int PortConnectedMachineLevels = 3;

    /// <summary>
    /// Decides how the player earns the added layers. Replace to take over presentation.
    /// Setting it to <c>null</c> is the same as <see cref="NoUnlockPolicy"/>: the layers exist
    /// and nothing makes them reachable.
    /// </summary>
    public static ILayerUnlockPolicy UnlockPolicy { get; set; }

    /// <summary>
    /// What the current session's scenario actually got, or <c>null</c> before the first one
    /// is built. Read this rather than the request counts above - a kind can end up with fewer
    /// layers than asked for, or none, if its registration failed.
    /// </summary>
    public static LayerRegistration Current
    {
        get { lock (Gate) { return _Current; } }
    }

    /// <summary>
    /// Fires once per scenario, immediately after the layers are registered and the policy has
    /// run. The place for a dependent mod to react to the real numbers - map generation keyed
    /// to the layer range, say.
    ///
    /// A throwing handler is caught and logged; it does not take the scenario down.
    /// </summary>
    public static event Action<LayerRegistration> Registered;

    internal static void Publish(LayerRegistration registration, Core.Logging.ILogger logger)
    {
        lock (Gate)
        {
            _Current = registration;
        }

        Action<LayerRegistration> handlers = Registered;
        if (handlers == null)
        {
            return;
        }

        foreach (Delegate entry in handlers.GetInvocationList())
        {
            try
            {
                ((Action<LayerRegistration>)entry)(registration);
            }
            catch (Exception exception)
            {
                logger.Exception?.LogException(exception);
                logger.Error?.Log(
                    "Extra Build Layers: a BuildLayers.Registered handler threw; continuing. " +
                    "The layers are registered either way.");
            }
        }
    }
}

/// <summary>
/// What one scenario ended up with. Immutable, and describes reality rather than intent.
/// </summary>
public sealed class LayerRegistration
{
    internal LayerRegistration(
        string scenarioId,
        IReadOnlyList<AddedLayer> machineLevels,
        IReadOnlyList<AddedLayer> spaceLevels,
        int topMachineLevel,
        int topSpaceLevel)
    {
        ScenarioId = scenarioId;
        MachineLevels = machineLevels;
        SpaceLevels = spaceLevels;
        TopMachineLevel = topMachineLevel;
        TopSpaceLevel = topSpaceLevel;
    }

    public string ScenarioId { get; }

    /// Layers this mod added. Empty if the kind was disabled or its registration failed.
    public IReadOnlyList<AddedLayer> MachineLevels { get; }

    public IReadOnlyList<AddedLayer> SpaceLevels { get; }

    /// <summary>
    /// The highest building layer that now exists, mod additions included. Equal to
    /// <c>GameMode.MaxBuildingLayer</c> for this scenario.
    /// </summary>
    public int TopMachineLevel { get; }

    /// <summary>
    /// The highest island layer any unlock now grants - the largest entry in
    /// <c>IslandLayersUnlockOrder</c>.
    ///
    /// Deliberately **not** <c>GameMode.MaxIslandLayer</c>, which is the unlock *count* and
    /// therefore over-reports whenever a scenario interleaves layers below the start. Use this
    /// one to size anything that reasons about altitude.
    /// </summary>
    public int TopSpaceLevel { get; }
}
