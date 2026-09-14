using Game.Core.Content.Islands;
using ShapezShifter.Flow;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Registers a crossing and its mirror image into one group, so one toolbar entry covers
    /// both and F flips between them.
    ///
    /// The game decides a group is flippable by counting. IslandPlacersCreator reads the group's
    /// IslandGroupCollection and builds a FlippableSinglePlacer when it holds two definitions, a
    /// plain SinglePlacer when it holds one, and throws above two. So the pair has to arrive in
    /// the same group - which the atomic extender cannot arrange, because it registers exactly
    /// one island per chain and a second chain would need a second group id and would add a
    /// second toolbar entry.
    ///
    /// Hence one builder that quietly builds both. The original is what gets returned, so the
    /// rest of the chain - placement, toolbar, simulation - keeps attaching to it exactly as if
    /// the mirror were not there. The mirror's own simulation, and both variants' prediction,
    /// are registered separately; see CrossoverPlatformsMod.ReArmedRegistrations.
    internal sealed class MirroredIslandPair : IIslandBuilder
    {
        private readonly IIslandBuilder Original;
        private readonly IIslandBuilder Mirrored;

        public MirroredIslandPair(IIslandBuilder original, IIslandBuilder mirrored)
        {
            Original = original;
            Mirrored = mirrored;
        }

        public IslandDefinition BuildAndRegister(IslandDefinitionGroup group, GameIslands gameIslands)
        {
            IslandDefinition original = Original.BuildAndRegister(group, gameIslands);
            IslandDefinition mirrored = Mirrored.BuildAndRegister(group, gameIslands);

            // What CommonIslandDefinitionFactory.LinkFlipped does for vanilla pairs. The placer
            // works off group membership alone, but IslandBlueprintProcessor reads
            // FlippableDefinition, so without it a flipped crossing would come back unflipped
            // out of a blueprint.
            // AttachOrReplace, not Attach: this runs again on every scenario load against the same
            // definitions, and a second FlippableDefinition would not overwrite the first - it
            // would sit beside it, which makes TryGet report a multiple match and find neither.
            original.CustomData.AttachOrReplace(new FlippableDefinition(mirrored, isFlipped: false));
            mirrored.CustomData.AttachOrReplace(new FlippableDefinition(original, isFlipped: true));

            return original;
        }
    }
}
