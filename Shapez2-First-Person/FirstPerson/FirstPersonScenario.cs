using System.Collections.Generic;
using System.Linq;
using Game.Core.GameData.Presets;
using Game.Core.Mode;
using ShapezShifter.Kit;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// The mod's own scenario: a game that is first person, and only first person.
///
/// Two files carry it, and the game finds both without being told:
/// <c>ModdedScenarios</c> reads every <c>*.json</c> under a mod's <c>scenarios</c> folder
/// and every one under <c>scenario-presets</c>, and hands them to the same readers the
/// built-in ones go through. So a scenario genuinely does ship in a mod folder - the
/// docs used to say otherwise.
///
/// What cannot ship in the file is the **map generation**. A preset names its parameters
/// as a whole value:
///
/// <code>
/// "MapGenerationParameters": "#include:Scenarios/SharedData/BaseMapGenerationParameters"
/// </code>
///
/// and <c>IncludePreProcessorSolver</c> substitutes the entire referenced document - there
/// is no merge, so changing one number means replacing all of them, including the
/// <c>ShapePatchGenerationLikeliness</c> table that decides which shape types appear at
/// which distance from the origin. That table lives in a Unity <c>TextAsset</c> which no
/// dump exposes (the base data exporter writes the `#include:` line out verbatim), so
/// inlining a replacement would mean inventing it.
///
/// Hence <see cref="ApplyMapGeneration"/>: keep the include, let it resolve, and then
/// overwrite only the scalars. The table survives because nothing touches it.
/// </summary>
public static class FirstPersonScenario
{
    /// <summary>
    /// Must match <c>UniqueId</c> in <c>scenarios/first-person-scenario.json</c>.
    /// </summary>
    public const string ScenarioId = "first-person-scenario";

    /// <summary>
    /// Must match <c>UniqueId</c> in
    /// <c>scenario-presets/first-person-preset.json</c>, whose <c>ScenarioId</c> in turn
    /// points at <see cref="ScenarioId"/>. The menu pairs the two by that field
    /// (<c>HUDMenuSelectScenarioState.StartScenarioConfig</c>) and calls <c>First()</c> on
    /// the result, so a scenario with no matching preset throws rather than being skipped.
    /// </summary>
    public const string PresetId = "first-person-scenario-parameter-preset";

    /// <summary>
    /// Whether the session that is running now is a game of ours - which is what forces
    /// first person, without a downstream mod having to set
    /// <see cref="FirstPersonControl.Forced"/>.
    ///
    /// Read per frame, so it has to tolerate being asked between sessions and from the main
    /// menu, whose background game is an ordinary scenario and answers false.
    /// </summary>
    public static bool IsCurrentSession
    {
        get
        {
            if (!FirstPersonControl.ScenarioEnabled)
            {
                return false;
            }

            GameScenario scenario = GameHelper.Core?.Mode?.Scenario;
            return scenario != null && scenario.UniqueId.Id == ScenarioId;
        }
    }

    /// <summary>
    /// Whether a promise the scenario readers are about to parse is one of ours. Compares
    /// against the raw JSON because that is all a promise carries - it has been read from
    /// disk but not yet deserialised, and refusing it there is the only point before the
    /// id exists as a field.
    /// </summary>
    public static bool IsOurs(string json)
    {
        return json != null
               && (json.Contains("\"" + ScenarioId + "\"") || json.Contains("\"" + PresetId + "\""));
    }

    /// <summary>
    /// Stamps the generation settings onto our preset, leaving every other preset alone.
    ///
    /// Called from the <c>GameData.ScenarioParameterPresets</c> getter rather than from a
    /// constructor, for two reasons: the getter is an ordinary method and a constructor is
    /// not, and every route into a new game reads it. The menu copies the preset by value -
    /// <c>gameParameters.ScenarioParameters.AssignFrom(preset.Parameters)</c> - so these
    /// are starting values the player can still change in the scenario config dialog, and a
    /// save carries its own copy afterwards.
    ///
    /// Idempotent, because it is assignment rather than adjustment, which is why it is
    /// allowed to run on every call.
    /// </summary>
    /// <summary>
    /// True once <see cref="ApplyMapGeneration"/> has found the preset and written to it.
    ///
    /// Worth exposing because the failure mode is silent and looks like nothing: a hook on
    /// a two-line property getter is the kind of method Mono's JIT inlines, and a caller
    /// that inlined it never reaches the hook. If a First Person game generates with a
    /// spiral and vanilla-spaced resources, this is the flag that says whether the stamp
    /// ran at all.
    /// </summary>
    public static bool MapGenerationApplied { get; private set; }

    public static void ApplyMapGeneration(IReadOnlyList<GameScenarioParametersPreset> presets)
    {
        if (presets == null)
        {
            return;
        }

        foreach (GameScenarioParametersPreset preset in presets)
        {
            if (preset?.UniqueId != PresetId)
            {
                continue;
            }

            MapGenerationParameters map = preset.Parameters?.MapGenerationParameters;
            if (map == null)
            {
                continue;
            }

            // Off. The spiral is a shape the map is cut into, and a first-person player
            // walking it meets an edge rather than a horizon.
            map.SpiralGeneration = false;

            // Above 100 these do not saturate, they loop: `DefaultMapGenerator` runs
            //
            //     do { if (rng.TestPercentage(k)) …; k -= 100; } while (k > 100);
            //
            // so the count is `ceil(k / 100) - 1` patches per super chunk, every one of them
            // placed outright because k is still over 100 when it is tested. The trailing
            // remainder is dropped rather than rolled - the loop exits at k <= 100 without
            // testing it - so 350 is exactly three, not three and a half, and 200 is exactly
            // two. A super chunk is 64x64 chunks, and vanilla's defaults are 15 and 30,
            // meaning well under one patch each.
            // Two, to match the shape patches below. This was 350 - three - which left fluids
            // denser than shapes on a map whose whole point is the shapes.
            map.FluidPatchLikelinessPercent = 250;
            map.FluidPatchBaseSize = 3;
            map.FluidPatchSizeGrowPercentPerChunk = 60;
            map.FluidPatchMaxSize = 8;

            // Two shape patches per super chunk. This was 500 - four - which read as a map
            // made of asteroids rather than a map with asteroids in it. Fewer and larger is
            // the shape a walking player wants: the patch sizes below are untouched, so each
            // one is still worth arriving at.
            map.ShapePatchLikelinessPercent = 200;
            map.ShapePatchBaseSize = 4;
            map.ShapePatchSizeGrowPercentPerChunk = 70;
            map.ShapePatchMaxSize = 8;

            map.ShapePatchRareShapeLikelinessPercent = 45;
            map.ShapePatchVeryRareShapeLikelinessPercent = 33;

            MapGenerationApplied = true;

            // Deliberately not set: ShapePatchGenerationLikeliness, the table of which shape
            // types spawn how far out. It arrives through the #include and is the one part
            // of the parameters this mod has no readable source for.
        }
    }

    /// <summary>
    /// Drops our preset from the list a caller is about to see. Used only when a downstream
    /// mod has turned the scenario off; the list is returned untouched otherwise, so the
    /// ordinary path allocates nothing.
    /// </summary>
    public static IReadOnlyList<GameScenarioParametersPreset> WithoutOurPreset(
        IReadOnlyList<GameScenarioParametersPreset> presets)
    {
        if (presets == null)
        {
            return null;
        }

        return presets.Where(preset => preset?.UniqueId != PresetId).ToList();
    }
}
