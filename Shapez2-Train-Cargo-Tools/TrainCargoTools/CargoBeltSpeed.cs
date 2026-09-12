using Game.Core.Simulation;

namespace TrainCargoTools
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
    internal sealed class CargoBeltSpeed : IBeltSpeed
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

        public StepRate StepsPerTick => Inner.StepsPerTick / Divisor;
    }
}
