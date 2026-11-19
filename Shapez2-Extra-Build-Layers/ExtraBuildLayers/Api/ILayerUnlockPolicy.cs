namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Decides how - or whether - the player earns the layers this mod added.
///
/// This is the seam the mod exists to provide. Creating a layer and presenting a way to buy
/// it are genuinely separate problems: the first is engine plumbing that is the same for
/// everyone, the second is a design decision that belongs to whoever is building the game on
/// top. A campaign mod wants a chain of escalating purchases; a sandbox mod wants all fifty
/// layers available on the first frame; a mod that drives layers from its own progression
/// wants none of this and will call
/// <see cref="LayerUnlockContext.GrantWithoutResearch"/> itself.
///
/// Called once per <see cref="LayerKind"/>, only when that kind actually gained layers, and
/// only from inside the scenario rewirer - so an implementation may mutate the progression
/// freely and does not need to be thread-safe.
///
/// A throw is caught and logged by the caller. The layers survive; they simply stay
/// unreachable, which is the safe failure: a scenario that will not load is worse than a
/// layer nobody can buy.
/// </summary>
public interface ILayerUnlockPolicy
{
    void Present(LayerUnlockContext context);
}
