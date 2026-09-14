using Game.Core.Content.Islands;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Which two path types a crossing joins.
    ///
    /// The order matters: path A is always the West-to-East run and path B the North-to-South
    /// run, because <see cref="CrossoverSimulation"/> hands out its bundles in that order and
    /// the game pairs bundles to connectors by index. See CrossoverConnectors.
    internal enum CrossoverKind
    {
        BeltBelt,
        BeltPipe,
        PipePipe
    }

    /// The island definition ids a crossing registers under.
    ///
    /// Kept in one place because they are built once when the islands are registered and looked
    /// up again, by name, when the placers are rewired - two points far enough apart that a typo
    /// between them would only show up as crossings silently never being offered.
    internal static class CrossoverIds
    {
        public static IslandDefinitionId Original(CrossoverKind kind)
        {
            return new IslandDefinitionId($"Crossover_{kind}");
        }

        public static IslandDefinitionId Mirrored(CrossoverKind kind)
        {
            return new IslandDefinitionId($"Crossover_{kind}_Mirrored");
        }

        public static IslandDefinitionGroupId Group(CrossoverKind kind)
        {
            return new IslandDefinitionGroupId($"Crossover_{kind}_Group");
        }
    }
}
