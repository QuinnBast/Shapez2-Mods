using Game.Core.Coordinates;

namespace TrainCargoTools
{
    /// Where a stored container sits on a store's rack.
    ///
    /// Shared by the two store renderers - the combined one and the legacy per-kind one - so the
    /// shelves cannot drift apart between them.
    ///
    /// **A seam the compiler cannot check.** These mirror `SHELF_HEIGHTS`, `RACK_SLOTS` and
    /// `RACK_INNER` in Tools/generate_meshes.py. Nothing can read an .obj's shelf heights back
    /// out, so if the rack moves in the generator these have to move with it and nothing will
    /// say so - the cargo just floats above the shelves or sinks into them.
    internal static class CargoRack
    {
        public static readonly float[] Shelves = { 1.5f, 4.1f, 6.7f };

        /// 5x5 per shelf, which is `CargoStoreState.CapacityPerLayer` exactly. The two are
        /// defined in different places for different reasons, so callers clamp to both rather
        /// than assuming they are equal.
        public const int SlotsPerSide = 5;
        public const int SlotsPerShelf = SlotsPerSide * SlotsPerSide;

        /// Usable width of a shelf, matching `RACK_INNER` in the generator.
        public const float Inner = 12.4f;
        public const float SlotPitch = Inner / SlotsPerSide;

        /// Clear height above a shelf: the gap to the next, less that shelf's thickness. Matches
        /// the 0.55 shelf thickness in the generator.
        public const float ShelfGap = 4.1f - 1.5f - 0.55f;

        /// Where slot `index` of `layer` sits, relative to the island's centre.
        ///
        /// Slots fill along a shelf and then back a row, so a part-full shelf reads as a queue
        /// with a front and a back rather than as a scatter.
        ///
        /// `bottom` is the package's lowest point once scaled - see CargoPackageMeshes - so the
        /// container sits *on* the shelf rather than halfway through it.
        public static WorldVector SlotOffset(int layer, int index, float bottom, GridRotation rotation)
        {
            float along = -Inner * 0.5f + SlotPitch * (index % SlotsPerSide + 0.5f);
            float across = -Inner * 0.5f + SlotPitch * (index / SlotsPerSide + 0.5f);

            // Mesh space is the renderer's - X along the flow, Z across, Y up - while game world
            // space is Z-up with Y across, so a mesh offset (x, y, z) is a world offset (x, -z, y).
            return new WorldVector(along, -across, Shelves[layer] - bottom).Rotate(rotation);
        }
    }
}
