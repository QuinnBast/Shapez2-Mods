using Game.Content.Features;
using Game.Core.Simulation;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// A cargo belt's speed: the space belt's, divided down.
    ///
    /// Cargo used to fly. Shortening the lane to four slots made it a quarter the length of a
    /// vanilla space belt's, and at the same steps-per-tick that means crossing a chunk four
    /// times faster - which reads as freight being flung along rather than hauled.
    ///
    /// Dividing here rather than picking a speed outright keeps the one property that mattered
    /// about reading it off the space belt in the first place: `SpaceConveyorSpeed` is a
    /// `BuffableBeltSpeed` whose `StepsPerTick` is rewritten when a belt-speed research
    /// completes, and this delegates to it every time it is asked, so those upgrades still land.
    /// Substituting a fixed `BeltSpeed` would have quietly cut cargo belts off from research.
    ///
    /// Note this changes latency, not throughput. The rate past any point is
    /// `speed / LaneConstants.ItemSpacing`, so slowing the belt does slow delivery - unlike the
    /// lane-length change, which did not.
    /// Derives from `BeltSpeed` because `SpaceSplitterConfiguration` takes that concrete class
    /// rather than the interface, and a splitter has to run at the same speed as the belt either
    /// side of it. `BeltSpeed` exposes `StepsPerTick` as a plain field but implements
    /// `IBeltSpeed` *explicitly*, so re-implementing the interface here wins wherever the game
    /// holds this as an `IBeltSpeed` - which is everywhere that reads a speed. Handing over a
    /// bare `BeltSpeed` instead would have frozen splitters at whatever the speed was when the
    /// island was built, cutting them off from belt-speed research.
    internal sealed class CargoBeltSpeed : BeltSpeed, IBeltSpeed
    {
        /// Five, which is a judgement rather than a derivation: four would exactly undo the
        /// lane shortening and match a vanilla belt, and freight reading a little heavier than
        /// the belt beside it is the point.
        public const int Divisor = 5;

        private readonly IBeltSpeed Inner;

        public CargoBeltSpeed(IBeltSpeed inner)
        {
            Inner = inner;
        }

        StepRate IBeltSpeed.StepsPerTick => Inner.StepsPerTick / Divisor;
    }
}
