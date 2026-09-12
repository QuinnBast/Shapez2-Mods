using Game.Content.Features.SpacePaths;

namespace TrainCargoTools
{
    /// Which of a space path's twelve lanes a cargo belt actually uses.
    ///
    /// A space belt carries four lanes abreast on each of three layers, because a shape is small.
    /// A cargo package is not: it is a shipping container, authored about as wide as a chunk. Four
    /// of them abreast cannot be drawn at anything like true size, and drawing them small made a
    /// loaded cargo belt look like a dusting of crumbs rather than freight.
    ///
    /// So a cargo belt uses **one lane per layer** and the other three refuse everything. That
    /// makes the picture and the simulation the same thing: one file of full-width containers per
    /// layer, three files on a belt, and nothing hidden behind anything else. Before this, three
    /// quarters of a backed-up belt's contents had nowhere to be drawn.
    ///
    /// The cost is buffer capacity - twelve lanes down to three, so a quarter of what a chunk
    /// held. Each slot is still a whole package and a lane still holds several along a chunk, so
    /// a cargo belt remains far denser than a shape belt; but it is a real reduction and it is
    /// the reason this is one constant rather than something inferred.
    ///
    /// Only the *belt* is restricted. The packager, unpackager and store go on accepting a
    /// package that arrives on any lane, which costs nothing and means a line fed from somewhere
    /// unexpected still works.
    internal static class CargoLanes
    {
        /// Lane 0. The packager already tried lanes in order and noted that "lane 0 carries the
        /// traffic in practice", so this is the lane cargo was mostly using anyway.
        public const short Travel = 0;

        /// How many containers one lane holds, and - because the two are the same number - how
        /// they are spaced.
        ///
        /// `FastBeltPathLaneState.Length_S` is `ItemCapacity * LaneConstants.ItemSpacing`, so a
        /// lane's physical length is derived from its capacity, and the renderer normalises an
        /// item's progress to 0..1 over that length before mapping it across the chunk. Those two
        /// facts together are the whole trick: a lane whose capacity is four is exactly four
        /// minimum gaps long, so four containers - even jammed nose to tail against a blockage -
        /// render at 0, 1/4, 1/2 and 3/4 of the chunk. Evenly spaced, by construction, with no
        /// special case for a saturated belt.
        ///
        /// It also sets the container size: one chunk divided by this is how much belt each one
        /// gets, which is what they are scaled to fill.
        ///
        /// Four rather than the old thirty-two. Thirty-two made a belt hold nearly as much as a
        /// cargo store, which left the store pointless, and it made the containers necessarily
        /// tiny - at minimum spacing they were half a world unit apart. Vanilla space belts use
        /// sixteen, so a cargo belt is now a quarter the length of one and cargo crosses a chunk
        /// four times faster. Throughput is unchanged by that: the rate past any point is
        /// speed divided by ItemSpacing, which does not depend on how long the lane is. Only
        /// latency and buffering shrink, which was the point.
        public const short SlotsPerLane = 4;

        /// World units of belt each container gets. A chunk is twenty units across, and the
        /// renderer draws one full chunk of run per segment.
        public const float SlotSpacing_W = 20f / SlotsPerLane;

        /// Vertical gap between the three lane layers on a cargo belt, in world units.
        ///
        /// **Not the theme's `SpacePathItemRenderingConfig.LayerOffset`**, which is what this
        /// used at first, and the reason a loaded belt looked like it was only carrying one
        /// layer. That offset is authored for shapes - small things, a fraction of a world unit
        /// apart. A cargo container is scaled to fill a whole slot, about two and a half units
        /// tall, so three of them at shape spacing interpenetrate almost completely: all three
        /// layers *were* being drawn, stacked so closely that they read as one.
        ///
        /// So a cargo belt sets its own layer spacing, from the size of the thing it carries.
        /// The track drawer stacks its three tiers by the same number, which is why this lives
        /// here rather than in either drawer - one constant, or they drift apart and the freight
        /// stops sitting on the deck.
        public const float LayerSpacing_W = 3.0f;

        /// The tallest a container may be drawn, leaving room for the deck of the tier above.
        ///
        /// A tier's deck is about 0.42 units thick beneath its surface, so a container of this
        /// height clears the underside of the next tier with a little air to spare.
        public const float MaxHeight_W = LayerSpacing_W * 0.78f;

        /// Where one layer's deck sits, in world units above the chunk centre.
        ///
        /// Both drawers go through this so the track and the freight on it cannot disagree - the
        /// track drawer stacks a tier here and the belt renderer puts that layer's containers on
        /// top of it.
        ///
        /// Centred on the theme's *middle* layer rather than stacked up from layer 0, so a cargo
        /// belt sits where a vanilla space belt sits instead of floating six units above the
        /// station it joins. `LayerOffset` is used only to find that centre; the gap between the
        /// tiers is the mod's own, for the reason on LayerSpacing_W.
        public static float LayerHeight_W(SpacePathItemRenderingConfig config, int layer)
        {
            return config.Height + config.LayerOffset + LayerSpacing_W * (layer - 1);
        }

        /// True for the one lane per layer that carries cargo.
        public static bool Carries(short lane)
        {
            return lane == Travel;
        }

        /// The lane index for the nth call to an ItemLaneBundle factory.
        ///
        /// `Bundle.CreateLanes` walks `for (lane = 0..3) for (layer = 0..2)` and hands the factory
        /// nothing but a lane state, so a factory that needs to know which lane it is building can
        /// only count its own invocations. Deterministic, but it is an ordering assumption about
        /// game code, so it lives here next to a name that says what it is rather than inline as a
        /// bare division.
        public static short LaneOfFactoryCall(int call)
        {
            return (short)(call / SpacePathConstants.NumLayers);
        }
    }
}
