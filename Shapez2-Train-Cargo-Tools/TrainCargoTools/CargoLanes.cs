using Game.Content.Features.SpacePaths;

namespace QuinnBast.Shapez2.TrainCargoTools
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

        /// Sideways gap between the three lane layers on a cargo belt, in world units.
        ///
        /// The three layers ride **abreast on one deck**, not stacked. Stacking was the mod's
        /// own idea and it did not survive contact: three decks three units apart made a
        /// six-unit tower, and from the game's angled camera the top deck hid the two beneath
        /// it, so a loaded belt could not be read at a glance.
        ///
        /// Vanilla does not stack either. `SpacePathSimulationRenderer.DrawItems` computes a
        /// *lateral* offset of `(layer - 1) * TracksSpacing + (lane - 1.5) * TrackItemsSpacing`
        /// and applies it perpendicular to travel, so a space belt fans its layers across the
        /// track as well as through it. Cargo does the same thing with the lane term dropped,
        /// there being one lane per layer: three files side by side, nothing behind anything.
        ///
        /// 2.4 rather than the theme's `TracksSpacing`, for the same reason the old vertical gap
        /// was the mod's own: that number is authored for shapes, which are a fraction of a unit
        /// across, and freight scaled to fill a slot would overlap at it. Sized instead to the
        /// deck, which `cargo_track` in Tools/generate_meshes.py sweeps to +-3.7: three files at
        /// this spacing put the outer two at +-2.4 and leave the deck edge clear.
        public const float LayerAcross_W = 2.4f;

        /// The widest a container may be drawn across the belt, so two files cannot touch.
        ///
        /// This is a *third* constraint on the package scale, and it is the one that usually
        /// binds now: a container is scaled to fill its slot along the run, and a crate authored
        /// much squarer than that slot would be far too wide to put three of abreast.
        public const float MaxAcross_W = LayerAcross_W * 0.92f;

        /// How far off the centre line a layer's file of containers runs.
        ///
        /// Layer 1 is the middle, which is where a one-layer belt drew before and where a belt
        /// fed from a single floor still draws - so the common case did not move.
        public static float Across_W(int layer)
        {
            return (layer - 1) * LayerAcross_W;
        }

        /// Where the deck sits, in world units above the chunk centre.
        ///
        /// One deck now, where the middle of the three tiers used to be, so a cargo belt still
        /// meets the station it joins at the height it always did. Both drawers go through this
        /// so the track and the freight on it cannot disagree.
        ///
        /// Read from the theme rather than baked in: how high a space path floats is authored
        /// data, and `config.Height + config.LayerOffset` is vanilla's own middle layer.
        public static float DeckHeight_W(SpacePathItemRenderingConfig config)
        {
            return config.Height + config.LayerOffset;
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
