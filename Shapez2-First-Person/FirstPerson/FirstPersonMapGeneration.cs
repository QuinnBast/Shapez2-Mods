namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// The map the First Person scenario generates, as data another mod can change.
///
/// Reach it through <see cref="FirstPersonControl.ScenarioMapGeneration"/>:
///
/// <code>
/// FirstPersonControl.ScenarioMapGeneration.ShapePatchLikelinessPercent = 300;
/// </code>
///
/// A plain class with plain public fields, for the same reason
/// <see cref="FirstPersonControl"/> is plain statics: a consumer that would rather not take
/// a hard reference can still walk to it and set fields on it reflectively.
///
/// The defaults live here rather than in <c>FirstPersonTuning</c>, which holds the camera
/// and body constants. These are scenario data - they describe a map rather than how the
/// player moves around one - and there is nothing else that wants them.
///
/// **Percentages above 100 do not saturate, they loop.** <c>DefaultMapGenerator</c> runs
///
/// <code>
/// do { if (rng.TestPercentage(k)) …; k -= 100; } while (k > 100);
/// </code>
///
/// so the number of patches is <c>ceil(k / 100) - 1</c> per super chunk, each placed
/// outright because k is still over 100 when it is tested, and the trailing remainder is
/// dropped rather than rolled. 250 and 200 are both exactly two; 199 is one. A super chunk
/// is 64x64 chunks, and the game's own defaults are 15 and 30 - well under one patch each.
/// </summary>
public class FirstPersonMapGeneration
{
    /// <summary>
    /// The spiral is a shape the map is cut into, and a first-person player walking it meets
    /// an edge rather than a horizon.
    /// </summary>
    public bool SpiralGeneration;

    /// Two fluid patches per super chunk. See the class summary for the arithmetic.
    public int FluidPatchLikelinessPercent = 250;

    public int FluidPatchBaseSize = 3;

    public int FluidPatchSizeGrowPercentPerChunk = 60;

    public int FluidPatchMaxSize = 8;

    /// <summary>
    /// Two shape patches per super chunk. This was 500 - four - which read as a map made of
    /// asteroids rather than a map with asteroids in it. Fewer and larger is the shape a
    /// walking player wants, so the sizes below are generous while this is not.
    /// </summary>
    public int ShapePatchLikelinessPercent = 200;

    public int ShapePatchBaseSize = 4;

    public int ShapePatchSizeGrowPercentPerChunk = 70;

    public int ShapePatchMaxSize = 8;

    public int ShapePatchRareShapeLikelinessPercent = 45;

    public int ShapePatchVeryRareShapeLikelinessPercent = 33;

    /// <summary>
    /// Writes these onto the parameters the scenario's preset resolved.
    ///
    /// Deliberately not exhaustive: <c>ShapePatchGenerationLikeliness</c>, the table of which
    /// shape types appear at which distance from the origin, is left exactly as the
    /// <c>#include</c> delivered it. It is authored ScriptableObject data with no readable
    /// source, so replacing it would mean inventing game data - which is the whole reason
    /// these are stamped in code rather than written into the preset file.
    /// </summary>
    public void ApplyTo(Game.Core.Mode.MapGenerationParameters map)
    {
        if (map == null)
        {
            return;
        }

        map.SpiralGeneration = SpiralGeneration;

        map.FluidPatchLikelinessPercent = FluidPatchLikelinessPercent;
        map.FluidPatchBaseSize = FluidPatchBaseSize;
        map.FluidPatchSizeGrowPercentPerChunk = FluidPatchSizeGrowPercentPerChunk;
        map.FluidPatchMaxSize = FluidPatchMaxSize;

        map.ShapePatchLikelinessPercent = ShapePatchLikelinessPercent;
        map.ShapePatchBaseSize = ShapePatchBaseSize;
        map.ShapePatchSizeGrowPercentPerChunk = ShapePatchSizeGrowPercentPerChunk;
        map.ShapePatchMaxSize = ShapePatchMaxSize;

        map.ShapePatchRareShapeLikelinessPercent = ShapePatchRareShapeLikelinessPercent;
        map.ShapePatchVeryRareShapeLikelinessPercent = ShapePatchVeryRareShapeLikelinessPercent;
    }
}
