using System.Collections.Generic;
using Game.Core.Trains;
using UnityEngine;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Riding trains.
///
/// The hard part of this was meant to be knowing where a wagon is. Wagons are not in the
/// tile map, so the ground model that carries the player along belts cannot see them, and
/// their position is interpolated from chunk pivots and a jump progress by one of eight
/// <c>ITrainTransformSolver</c> implementations - a bezier along the rail in the ordinary
/// case, an animation curve on a lift, something else again mid-jump. Re-deriving that
/// would mean eight hooks and a real chance of an invisible train carrying the player
/// somewhere the visible one is not.
///
/// None of that is necessary. <c>DrawHooks.OnDrawTrain</c> is a plain multicast delegate
/// the renderer already fires per train per frame:
///
/// <code>
/// delegate void DrawTrainDelegate(FrameDrawOptionsNoLOD options, TrainId trainId,
///                                 TrainData trainData, IDictionary&lt;int, Matrix4x4&gt; wagonsMatricesMap);
/// </code>
///
/// which hands over **the matrices the wagons are actually drawn with**, keyed by wagon
/// index, alongside a `TrainId` that is stable between frames. So the position is not
/// computed, it is observed, and it cannot disagree with what is on screen.
/// <c>HUDTrainCargoVisualization</c> subscribes to the same event, so this is the
/// game's own way in rather than a patch.
///
/// Riding is then absolute rather than incremental: the player *is* at the wagon's
/// position each frame, so there is no delta to accumulate and no drift to correct.
/// </summary>
public sealed class FirstPersonTrains
{
    /// <summary>
    /// A wagon as the renderer last drew it: where it was, and which way was up.
    ///
    /// The second half is what makes riding an upside-down rail work. shapez has them -
    /// `SidedCoordinate` carries a plain `bool UpsideDown` - and a wagon on one is drawn
    /// with `pitch = 180`, which `TrainsDrawer.CalculateWagonTransform` composes as
    ///
    /// <code>
    /// Matrix4x4.TRS(pos, Quaternion.Euler(roll + lean, yaw, pitch), scale)
    /// </code>
    ///
    /// - so "pitch" is the **Z** euler despite the name, and at 180 it sends the wagon's own
    /// +Y to -Y. Column 1 of a TRS matrix is that transformed local Y, so the up vector is
    /// readable straight off the matrix we already collect. No second hook, and nothing to
    /// keep in step with the navigation state.
    /// </summary>
    private readonly struct Wagon
    {
        public readonly Vector3 Position;
        public readonly Vector3 Up;

        public Wagon(Vector3 position, Vector3 up)
        {
            Position = position;
            Up = up;
        }
    }

    /// <summary>
    /// Every drawn wagon, as of the last frame the renderer ran. Keyed by train and wagon
    /// index - `TrainId` implements `IEquatable`, so the pair is a sound dictionary key and
    /// survives between frames, which is what makes "keep riding the wagon I boarded"
    /// possible at all.
    /// </summary>
    private readonly Dictionary<(TrainId, int), Wagon> Wagons =
        new Dictionary<(TrainId, int), Wagon>();

    private DrawHooks Subscribed;
    private DrawHooks.DrawTrainDelegate Handler;

    public bool Riding { get; private set; }

    private (TrainId, int) RidingKey;

    /// <summary>
    /// Frames the ridden wagon has been missing from the draw. Not zero-tolerance, because
    /// the renderer culls: looking away from the train you are standing on could otherwise
    /// throw you off it.
    /// </summary>
    private int MissingFrames;

    /// <summary>
    /// Only collect while the player is actually down there. This runs per train per frame
    /// inside the render path, so when first person is off it has to cost nothing.
    /// </summary>
    public bool Collecting { get; set; }

    /// <summary>
    /// Subscribes to the draw hooks of whichever session is current. Called every frame
    /// because a new session brings a new <c>DrawHooks</c>, and re-subscribing to the same
    /// one would stack handlers.
    /// </summary>
    public void Attach(DrawHooks hooks)
    {
        if (ReferenceEquals(hooks, Subscribed))
        {
            return;
        }

        Detach();

        if (hooks == null)
        {
            return;
        }

        Handler = OnDrawTrain;
        hooks.OnDrawTrain += Handler;
        Subscribed = hooks;
    }

    public void Detach()
    {
        if (Subscribed != null && Handler != null)
        {
            Subscribed.OnDrawTrain -= Handler;
        }

        Subscribed = null;
        Handler = null;
        Wagons.Clear();
        Riding = false;
    }

    private void OnDrawTrain(
        FrameDrawOptionsNoLOD options, TrainId trainId, TrainData trainData,
        IDictionary<int, Matrix4x4> wagonsMatricesMap)
    {
        if (!Collecting || wagonsMatricesMap == null)
        {
            return;
        }

        foreach (KeyValuePair<int, Matrix4x4> wagon in wagonsMatricesMap)
        {
            // Column 3 of a TRS matrix is its translation, column 1 the transformed local Y.
            Vector3 up = wagon.Value.GetColumn(1);

            // A lift's solver writes `scale` by reference, so the column is not unit length
            // and a degenerate one is possible. Falling back to world up puts the rider on
            // top, which is the answer for every rail that is not inverted.
            up = up.sqrMagnitude > 1E-06f ? up.normalized : Vector3.up;

            Wagons[(trainId, wagon.Key)] = new Wagon(wagon.Value.GetColumn(3), up);
        }
    }

    /// <summary>
    /// Clears last frame's wagons. Called from the camera update, which runs *before* the
    /// draw that refills them - so during an update the dictionary holds the positions the
    /// wagons were last drawn at, which is exactly one frame of latency and invisible.
    /// Clearing this way also retires wagons that stopped being drawn without needing to
    /// know they were destroyed.
    /// </summary>
    public void EndFrame()
    {
        Wagons.Clear();
    }

    /// <summary>
    /// Finds the wagon nearest the crosshair and boards it. Distance is measured to the
    /// ray rather than to a box, because a wagon's dimensions are not readable from here -
    /// and for "look at the train and press the key" a tolerance around the line of sight
    /// is the right test anyway.
    /// </summary>
    public bool TryBoard(Vector3 origin, Vector3 direction)
    {
        float best = float.MaxValue;
        bool found = false;
        (TrainId, int) bestKey = default;

        foreach (KeyValuePair<(TrainId, int), Wagon> wagon in Wagons)
        {
            Vector3 offset = wagon.Value.Position - origin;
            float along = Vector3.Dot(offset, direction);

            if (along < 0f || along > FirstPersonTuning.BoardReach)
            {
                continue;
            }

            float perpendicular = (offset - direction * along).magnitude;
            if (perpendicular > FirstPersonTuning.BoardRadius || perpendicular >= best)
            {
                continue;
            }

            best = perpendicular;
            bestKey = wagon.Key;
            found = true;
        }

        if (!found)
        {
            return false;
        }

        // Ride the locomotive, whichever wagon was actually aimed at. Wagon 0 is the head -
        // `TrainData.Head` is `Wagons[0]`, and `TrainSimulationDebugData` calls that pair
        // the Locomotive - so boarding a cargo wagon halfway down the train was a matter of
        // which part happened to be nearest the crosshair. Riding up front is what anyone
        // means by riding a train.
        RidingKey = (bestKey.Item1, 0);
        MissingFrames = 0;
        Riding = true;
        return true;
    }

    public void Disembark()
    {
        Riding = false;
        MissingFrames = 0;
    }

    /// <summary>
    /// Where the ridden wagon is now, and which way is up for it. False means it has been
    /// gone long enough to call it gone - the train was destroyed, or delivered itself into
    /// the hub with the player aboard.
    /// </summary>
    public bool TryGetRidingPosition(out Vector3 position, out Vector3 up)
    {
        if (Riding && Wagons.TryGetValue(RidingKey, out Wagon wagon))
        {
            position = wagon.Position;
            up = wagon.Up;
            MissingFrames = 0;
            return true;
        }

        position = Vector3.zero;
        up = Vector3.up;

        if (!Riding)
        {
            return false;
        }

        MissingFrames++;
        return MissingFrames < FirstPersonTuning.TrainLostFrames;
    }
}
