namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Where the player was standing, carried in the save.
///
/// Small on purpose. Only what cannot be recovered goes in: a position, a facing, and a flag
/// saying whether any of it was ever written. Everything else about the body - what it is
/// standing on, which island layer that is, whether it is falling - is recomputed from the
/// map on the first frame, and storing it would only be a way to disagree with the map after
/// the player rebuilds the platform they left from.
///
/// **The type's full name is the save key.**
/// <c>ModSaveDataExtensions.ResolveId&lt;T&gt;()</c> is
/// <c>AssemblyName + "-" + typeof(T).FullName</c>, so renaming this class or moving it out of
/// <c>QuinnBast.Shapez2.FirstPerson</c> silently orphans every save that has one. It is the
/// one namespace in this mod that is not free to move.
/// </summary>
public class FirstPersonSaveData
{
    /// <summary>
    /// False in a save written before the mod stored anything, and in a brand new game. The
    /// position fields are meaningless then, and the player arrives at the vortex as they
    /// always did.
    /// </summary>
    public bool HasPosition;

    public double PositionX;

    public double PositionY;

    /// Feet height, in world units - not the eye. See <c>FirstPersonBody.Height</c>.
    public float Height;

    public float Yaw;

    public float Pitch;

    /// <summary>
    /// Whether they were flying. Restored only if flight is still available, so a save that
    /// loses the research - or a mod that turns flight off - puts the player on the floor
    /// instead of leaving them hovering with no way down.
    /// </summary>
    public bool Flying;
}
