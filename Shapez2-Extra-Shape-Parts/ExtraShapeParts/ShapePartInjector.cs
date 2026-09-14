using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ILogger = Core.Logging.ILogger;
using Rarity = MetaShapesConfiguration.PartGenerationRarity;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// Adds the mod's parts to the game's shape configurations.
    ///
    /// There is no registration API. A `ShapesConfiguration` is built once per process in the
    /// `GameData` constructor out of an authored ScriptableObject, and every consumer - the session's
    /// `StrictShapeDefinitionFactory`, `MapShapeGenerator`, `UniversalShapeRenderer`, the map
    /// resource filter - reads it from there. What makes it reachable anyway is that it builds each
    /// of its lists with `.ToList()` and exposes them as `IReadOnlyList`: the list object is still
    /// mutable, so adding to it adds the part everywhere at once.
    public static class ShapePartInjector
    {
        /// Injects into every configuration the game knows about.
        ///
        /// Called per session rather than once, because the hook that drives it is a session hook,
        /// and because a game update could plausibly rebuild `GameData`. Adding the same code twice
        /// would be fatal - <c>GameSessionOrchestrator.Init_7_Rendering</c> and
        /// <c>StrictShapeDefinitionFactory</c> both call <c>ToDictionary</c> keyed on the code - so
        /// the presence check below is load bearing, not defensive tidiness.
        public static void Inject(IGameData gameData, IEnumerable<ShapePartProfile> profiles, ILogger logger)
        {
            foreach (IShapesConfiguration configuration in gameData.ShapesConfigurations)
            {
                // A part count below three puts the sector at 180 degrees or more, where the cell
                // basis degenerates. `MetaShapesConfiguration.OnValidate` allows it, so this is a
                // real bound; nothing shipped is anywhere near it.
                if (configuration.PartCount < ShapeGeometry.MinimumPartCount)
                {
                    logger.Warning?.Log(
                        $"Skipping a shape configuration with PartCount {configuration.PartCount}: " +
                        $"the geometry needs at least {ShapeGeometry.MinimumPartCount}.");
                    continue;
                }

                foreach (ShapePartProfile profile in profiles)
                {
                    Add(configuration, profile, logger);
                }
            }
        }

        private static void Add(IShapesConfiguration configuration, ShapePartProfile profile, ILogger logger)
        {
            if (configuration.Parts.Any(part => part.Code == profile.Code))
            {
                // Either this is the second session of the process - normal, and the reason the
                // check exists - or the code belongs to somebody else. The two are indistinguishable
                // from here, so say so rather than skipping in silence: a part that quietly never
                // registers in one configuration is exactly the kind of thing that goes unnoticed
                // until a save will not load.
                logger.Warning?.Log(
                    $"Shape code '{profile.Code}' ({profile.Name}) is already present in the " +
                    $"{configuration.PartCount}-part configuration. Expected on a second session; " +
                    "on the first, the code is taken - run esp.report to see what holds it.");
                return;
            }

            IList parts = AsMutable(configuration.Parts, logger, "Parts");
            if (parts == null)
            {
                return;
            }

            IList bucket = BucketFor(configuration, profile.Rarity, logger);
            IList all = AsMutable(configuration.MapGenerationAllParts, logger, "MapGenerationAllParts");

            if (profile.Rarity != Rarity.NotSpawned && (bucket == null || all == null))
            {
                return;
            }

            MetaShapeSubPart part = ShapePartFactory.GetOrCreate(profile, configuration.PartCount);

            parts.Add(part);
            if (profile.Rarity != Rarity.NotSpawned)
            {
                bucket.Add(part);
                all.Add(part);
            }

            logger.Info?.Log($"Added shape part '{profile.Code}' ({profile.Name}, {profile.Rarity}) " +
                             $"to a {configuration.PartCount}-part configuration");
        }

        private static IList BucketFor(IShapesConfiguration configuration, Rarity rarity, ILogger logger)
        {
            switch (rarity)
            {
                case Rarity.Common:
                    return AsMutable(configuration.MapGenerationCommonParts, logger, "MapGenerationCommonParts");
                case Rarity.Rare:
                    return AsMutable(configuration.MapGenerationRareParts, logger, "MapGenerationRareParts");
                case Rarity.VeryRare:
                    return AsMutable(configuration.MapGenerationVeryRareParts, logger, "MapGenerationVeryRareParts");
                default:
                    return null;
            }
        }

        /// The cast the whole approach rests on - and it has to be the **non-generic** `IList`.
        ///
        /// `Parts` and the three rarity buckets are `List&lt;MetaShapeSubPart&gt;` at runtime, because
        /// `ShapesConfiguration` builds them from `GenerationPart.Part`, which is the concrete
        /// ScriptableObject. They only *look* like `IReadOnlyList&lt;IShapeSubPart&gt;` because that
        /// interface is covariant. `List&lt;T&gt;` is not, so `is List&lt;IShapeSubPart&gt;` is false and
        /// casting to it fails - while `MapGenerationAllParts`, built by reading those properties
        /// back, genuinely is a `List&lt;IShapeSubPart&gt;`. Non-generic `IList` covers both.
        ///
        /// This cost a session: the guard fired, logged "no longer a List", and the mod loaded
        /// perfectly while registering nothing.
        private static IList AsMutable(IReadOnlyList<IShapeSubPart> list, ILogger logger, string name)
        {
            if (list is IList mutable && !mutable.IsReadOnly)
            {
                return mutable;
            }

            logger.Error?.Log(
                $"ShapesConfiguration.{name} is not a mutable IList (it is " +
                $"{list?.GetType().FullName ?? "null"}). Extra shape parts cannot be registered.");
            return null;
        }

        /// What the configurations actually hold, for the `esp.report` console command.
        ///
        /// Worth printing rather than assuming: which vanilla parts sit in which rarity bucket is
        /// authored ScriptableObject data, so it cannot be read out of the assemblies, and it is
        /// what decides how much a new part dilutes the map.
        public static string Describe(IGameData gameData)
        {
            StringBuilder report = new StringBuilder();

            foreach (IShapesConfiguration configuration in gameData.ShapesConfigurations)
            {
                report.AppendLine($"parts={configuration.PartCount} " +
                                  $"all={Codes(configuration.Parts)}");
                report.AppendLine($"  common={Codes(configuration.MapGenerationCommonParts)}");
                report.AppendLine($"  rare={Codes(configuration.MapGenerationRareParts)}");
                report.AppendLine($"  very rare={Codes(configuration.MapGenerationVeryRareParts)}");
            }

            return report.ToString();
        }

        private static string Codes(IReadOnlyList<IShapeSubPart> parts)
        {
            return parts == null ? "-" : new string(parts.Select(part => part.Code).ToArray());
        }
    }
}
