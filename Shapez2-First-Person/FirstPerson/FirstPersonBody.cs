using Game.Core.Coordinates;
using Game.Content.BuildingPath.Simulation;
using Game.Core.Simulation;
using Unity.Mathematics;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Gravity, ground and walls for the first-person player.
///
/// Not a rigidbody. shapez has no physics world to join - buildings are rows in a model,
/// not colliders, and the only `Collider` components in a session are the per-chunk boxes
/// islands carry so they can be clicked. So this queries the map model directly, which in
/// a game where everything is on a one-tile grid is both cheaper and more predictable than
/// a swept capsule would be.
///
/// The model is Minecraft's, not Unity's:
///
/// <list type="bullet">
/// <item>A column of tiles is solid or it is not. <c>IMapModel.TryGetBuilding</c> answers.</item>
/// <item>The surface you stand on is the top of the highest building in your column, or
/// the island floor if the column is empty.</item>
/// <item>A surface no more than <see cref="FirstPersonTuning.StepHeight"/> above your feet
/// is a step, not a wall. In shapez the platform is usually covered in machines, so
/// without this a factory is a maze of one-tile dead ends.</item>
/// <item>No island under you means no floor, and you fall - which on a space platform is
/// the correct outcome rather than a bug.</item>
/// </list>
/// </summary>
public sealed class FirstPersonBody
{
    /// World XZ, in the same space and units as <c>Viewport.Position</c>.
    public double2 Horizontal;

    /// Unity world Y of the feet. The island floor for island layer L is at L * 20.
    public float Height;

    public bool Grounded { get; private set; }

    /// Noclip: no gravity, no walls, free vertical movement. Also the way out of a stuck
    /// spot, which a spike needs more than it needs correctness.
    public bool Flying;

    private float VerticalSpeed;

    /// Where to put the player back when they fall past every floor in the map.
    private double2 SafeHorizontal;
    private float SafeHeight;

    public void Reset(double2 horizontal, float height)
    {
        Horizontal = horizontal;
        Height = height;
        VerticalSpeed = 0f;
        Grounded = false;
        SafeHorizontal = horizontal;
        SafeHeight = height;
    }

    /// <summary>
    /// One simulation step. <paramref name="wish"/> is the horizontal movement already
    /// scaled by speed and delta time; <paramref name="vertical"/> is -1..1 and only used
    /// in noclip.
    /// </summary>
    /// <summary>
    /// Aboard a train. Position is written from outside, by whatever the wagon's matrix
    /// says, so gravity and walls have to stand down entirely - a moving wagon would
    /// otherwise be fighting a body trying to fall through it.
    /// </summary>
    public bool Riding;

    public void Step(IMapModel map, double2 wish, float vertical, bool jump, float deltaTime)
    {
        if (Riding)
        {
            VerticalSpeed = 0f;
            Grounded = false;
            return;
        }

        if (Flying || map == null)
        {
            Horizontal += wish;
            Height += vertical * FirstPersonControl.FlySpeed * deltaTime;
            VerticalSpeed = 0f;
            Grounded = false;
            return;
        }

        // One axis at a time, so a blocked X still allows Z. Moving both at once and
        // rejecting the whole step is what makes a player stick to walls instead of
        // sliding along them.
        TryMove(map, new double2(wish.x, 0.0));
        TryMove(map, new double2(0.0, wish.y));

        // Conveyors carry you. Through TryMove rather than straight onto Horizontal, so a
        // belt running into a wall presses you against it instead of through it - and so
        // that walking against the belt works the way it looks like it should.
        if (FirstPersonControl.BeltsCarryPlayer)
        {
            double2 drift = ConveyorDrift(map, deltaTime);
            TryMove(map, new double2(drift.x, 0.0));
            TryMove(map, new double2(0.0, drift.y));
        }

        if (jump && Grounded)
        {
            VerticalSpeed = FirstPersonControl.JumpSpeed;
            Grounded = false;
        }

        VerticalSpeed = math.max(
            VerticalSpeed - FirstPersonTuning.Gravity * deltaTime,
            -FirstPersonTuning.MaxFallSpeed);

        float next = Height + VerticalSpeed * deltaTime;

        // The ceiling on the ground query is where the feet are now, plus the step
        // allowance - so walking into a belt lifts you onto it, but a stack two tiles high
        // is not somewhere you can suddenly be standing.
        float surface = SurfaceUnder(map, Horizontal, Height + FirstPersonTuning.StepHeight);

        if (VerticalSpeed <= 0f && next <= surface)
        {
            Height = surface;
            VerticalSpeed = 0f;
            Grounded = true;
            SafeHorizontal = Horizontal;
            SafeHeight = surface;
            return;
        }

        Height = next;
        Grounded = false;

        if (Height < SafeHeight - FirstPersonTuning.FallRescueDepth)
        {
            // Nothing below to land on. Falling off the edge is the point; staying fallen
            // is a soft lock, and there is no respawn in this game to fall back on.
            Horizontal = SafeHorizontal;
            Height = SafeHeight;
            VerticalSpeed = 0f;
        }
    }

    /// <summary>
    /// The island layer the body is standing in, for handing to <c>Viewport.IslandLayer</c>
    /// so the game's own notion of the current layer follows the player up and down.
    /// </summary>
    public short IslandLayer(IMapModel map)
    {
        int layer = (int)math.floor(Height / 20f);
        return (short)math.clamp(layer, 0, map?.MaxIslandLayer ?? 0);
    }

    /// <summary>
    /// How far the belt under the player's feet carries them this frame.
    ///
    /// The speed is not a guess and not a constant of ours. Every belt's own definition
    /// carries an <c>IConveyorConfiguration</c>, whose <c>ConveyorSpeed.StepsPerTick</c> is
    /// the same <c>BeltSpeed</c> the simulation is built from
    /// (<c>BuiltinSimulationSystems.CreateSimulationSystems</c> reads it from
    /// <c>Mode.Buildings.ForwardBelt</c> and hands it to every belt system). The unit
    /// algebra converts it exactly:
    ///
    /// <code>
    /// Steps  = StepRate * Ticks          // operator on StepRate
    /// tiles  = Steps.FloatWorldUnits     // one world unit is one tile
    /// </code>
    ///
    /// Asking the building rather than a global also means a space belt reports its own
    /// speed, and a research speed buff is already folded in - the configuration holds a
    /// <c>BuffableBeltSpeed</c>.
    ///
    /// Testing for the configuration rather than for a definition id is what makes this
    /// cover every belt variant at once, since carrying items is exactly what having an
    /// <c>IConveyorConfiguration</c> means.
    /// </summary>
    private double2 ConveyorDrift(IMapModel map, float deltaTime)
    {
        if (!Grounded)
        {
            return double2.zero;
        }

        // The surface is the *top* of the building, so the building itself is the tile
        // below it. Standing on the bare platform floor lands on empty space and drifts
        // nothing, which is correct.
        int z = (int)math.floor(Height) - 1;
        GlobalTileCoordinate column = Column(Horizontal);

        if (!map.TryGetBuilding(new GlobalTileCoordinate(column.x, column.y, (short)z), out BuildingModel building))
        {
            return double2.zero;
        }

        if (!building.Definition.TryConfigAs<IConveyorConfiguration>(out IConveyorConfiguration conveyor))
        {
            return double2.zero;
        }

        float tilesPerSecond = (conveyor.ConveyorSpeed.StepsPerTick * Ticks.FromSeconds(1f)).FloatWorldUnits;

        // A curve's rotation is its output facing, so a corner carries you out the way the
        // items leave rather than around the bend. Good enough to ride; the belt still
        // takes you where it is going.
        TileVector forward = TileVector.ByDirection(building.Rotation_G.ToTileDirection());

        // Tile space is the game's Z-up space, so its Y is the negation of Unity's Z - the
        // same conversion WorldCoordinate's operators do, applied to a direction.
        return new double2(forward.x, -forward.y) * tilesPerSecond * deltaTime;
    }

    private void TryMove(IMapModel map, double2 delta)
    {
        if (math.lengthsq(delta) < 1e-12)
        {
            return;
        }

        double2 candidate = Horizontal + delta;

        // Probe the leading edge as well as the centre, otherwise the body is a point and
        // you can stand inside the face of a machine.
        double2 leading = candidate + math.normalize(delta) * FirstPersonTuning.BodyRadius;

        if (Blocked(map, candidate, Height) || Blocked(map, leading, Height))
        {
            return;
        }

        Horizontal = candidate;
    }

    /// <summary>
    /// Is there a building occupying the space the body would stand in - above the step
    /// allowance, below the top of the head?
    /// </summary>
    private static bool Blocked(IMapModel map, double2 horizontal, float feet)
    {
        GlobalTileCoordinate column = Column(horizontal);

        int from = (int)math.floor(feet + FirstPersonTuning.StepHeight);
        int to = (int)math.floor(feet + FirstPersonTuning.BodyHeight);

        for (int z = from; z <= to; z++)
        {
            if (map.TryGetBuilding(new GlobalTileCoordinate(column.x, column.y, (short)z), out BuildingModel _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The highest walkable surface in a column at or below <paramref name="ceiling"/>, or
    /// negative infinity when there is no platform there at all - which is what makes a
    /// step off the edge a fall rather than a stop.
    /// </summary>
    private static float SurfaceUnder(IMapModel map, double2 horizontal, float ceiling)
    {
        GlobalTileCoordinate column = Column(horizontal);

        for (short layer = map.MaxIslandLayer; layer >= 0; layer--)
        {
            int floor = layer * 20;
            if (floor > ceiling)
            {
                continue;
            }

            if (!map.TryGetIsland(new GlobalTileCoordinate(column.x, column.y, (short)floor), out IslandModel _))
            {
                continue;
            }

            float surface = floor;
            for (int building = 0; building <= map.MaxBuildingLayer; building++)
            {
                int z = floor + building;
                if (z + 1 > ceiling)
                {
                    break;
                }

                if (map.TryGetBuilding(new GlobalTileCoordinate(column.x, column.y, (short)z), out BuildingModel _))
                {
                    surface = z + 1;
                }
            }

            return surface;
        }

        return float.NegativeInfinity;
    }

    /// <summary>
    /// Horizontal position to the tile column containing it.
    ///
    /// Going through <c>WorldCoordinate</c> rather than doing the arithmetic here is not
    /// ceremony. <c>WorldCoordinate</c> is Z-up *and* its Y axis is the negation of Unity's
    /// Z - the conversion is <c>float3 (x, y, z) -> WorldCoordinate (x, -z, y)</c>, which
    /// is easy to write backwards and produces a mirrored world that looks almost right.
    /// The implicit operator is the game's own, so it cannot drift.
    /// </summary>
    private static GlobalTileCoordinate Column(double2 horizontal)
    {
        WorldCoordinate world = new float3((float)horizontal.x, 0f, (float)horizontal.y);
        return world.ToGlobalTileCoordinate();
    }
}
