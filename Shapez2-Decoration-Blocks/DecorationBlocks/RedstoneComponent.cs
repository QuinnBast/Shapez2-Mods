using Game.Core.Coordinates;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// One placed redstone component and everything mutable about it.
///
/// Held in RedstoneWorld rather than in a shapez `ISimulationState`, which means **it does not
/// survive a save and reload**. That is less of a loss than it sounds: almost all of it is
/// derived - dust strength, torch state and block power are recomputed from the sources within a
/// tick of loading - and only three things are genuinely remembered, a lever's position, a
/// repeater's delay setting and a button mid-press. Persisting those means a real
/// `ISimulationState` per component, which is the next step rather than this one.
public sealed class RedstoneComponent
{
    public RedstoneComponent(RedstoneDefinition definition, GridRotation rotation)
    {
        Definition = definition;
        Rotation = rotation;

        // A torch placed into the world starts lit, so a freshly built circuit does something
        // immediately rather than waiting a tick to discover it is not held off.
        TorchLitNextTick = definition.Kind == RedstoneKind.Torch;
    }

    public RedstoneDefinition Definition { get; }

    public GridRotation Rotation { get; }

    /// Signal strength this tick, 0-15.
    public int Power { get; set; }

    public bool LeverOn { get; set; }

    public int ButtonTicksLeft { get; set; }

    /// One to four, cycled by clicking the repeater. Minecraft's default is one.
    public int RepeaterDelay { get; set; } = 1;

    /// What the repeater is putting out now, having come off the front of the queue.
    public int Output { get; set; }

    public DelayQueue Queue { get; } = new DelayQueue();

    /// Computed at the end of a tick and read at the start of the next, which is the whole of a
    /// torch's one-tick delay.
    public bool TorchLitNextTick { get; set; }

    /// A lamp's only state. Kept apart from Power because a lamp is a dead end: it lights, and
    /// it hands nothing on. Writing its brightness into Power would make it look like a source to
    /// anything that reads neighbour power.
    public bool Lit { get; set; }

    /// Comparators subtract instead of comparing when this is set. Clicked, like a repeater's
    /// delay.
    public bool Subtracting { get; set; }

    /// Ticks left of an observer's output pulse.
    public int PulseTicksLeft { get; set; }

    /// What the observer saw in front of it last tick. A change here is the whole trigger.
    public int LastSignature { get; set; } = int.MinValue;

    /// What the converter's wire input last read, copied off the simulation on the main thread.
    public bool WireIsOn { get; set; }

    /// Whether a torch has a block behind it. Decides both whether it inverts that block and
    /// whether it is drawn leaning against it, which is the only way a player can see which of
    /// the two a given torch is.
    public bool Mounted { get; set; }

    /// What the renderer draws, and what an observer watching this component sees change.
    ///
    /// A lamp has to be asked about `Lit` rather than `Power`. Its power is **always zero** by
    /// design - a lamp is a dead end and must not read as a source to anything looking at
    /// neighbour power - so the obvious `Power > 0` made a correctly solved lamp draw dark for
    /// ever. Keeping the two apart was right; forgetting that this read the wrong one was not.
    public bool IsActive
    {
        get
        {
            switch (Definition.Kind)
            {
                case RedstoneKind.Torch:
                    return TorchLitNextTick;

                case RedstoneKind.Lamp:
                    return Lit;

                case RedstoneKind.Converter:
                    // Either direction counts. Power alone would miss the case where redstone is
                    // driving the wire, because that direction never raises the converter's own
                    // power - it only sets Lit.
                    return Lit || Power > 0;

                default:
                    return Power > 0;
            }
        }
    }

    /// A repeater's delay line: up to four ticks of history, shifted one step per tick.
    ///
    /// A ring would be tidier and is wrong here, because the delay is adjustable while the queue
    /// has values in it. Minecraft's repeater re-reads its delay each time it latches, and a
    /// four-slot array shifted by one does that correctly for free - lengthening the delay does
    /// not lose what is already in flight, and shortening it does not skip ahead.
    public sealed class DelayQueue
    {
        private readonly int[] Slots = new int[4];

        public int Shift()
        {
            int front = Slots[0];

            for (int i = 0; i < Slots.Length - 1; i++)
            {
                Slots[i] = Slots[i + 1];
            }

            Slots[Slots.Length - 1] = Slots[Slots.Length - 2];
            return front;
        }

        public void Push(int value, int delay)
        {
            int index = Mathf.Clamp(delay, 1, Slots.Length) - 1;

            for (int i = index; i < Slots.Length; i++)
            {
                Slots[i] = value;
            }
        }

        /// A locked repeater keeps putting out whatever it last latched.
        public void Hold()
        {
        }

        private static class Mathf
        {
            public static int Clamp(int value, int min, int max)
            {
                return value < min ? min : value > max ? max : value;
            }
        }
    }
}
