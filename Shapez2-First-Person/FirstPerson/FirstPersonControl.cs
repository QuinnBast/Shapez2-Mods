namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// The mod's public surface, for other mods to build on.
///
/// Reference this assembly and it is what it looks like:
///
/// <code>
/// FirstPersonControl.Forced = true;
/// FirstPersonControl.FlightRequiresResearch = false;
/// </code>
///
/// Deliberately tiny and deliberately dumb - plain statics with obvious names, no events, no
/// interfaces, no generics - for one reason beyond taste: a mod that would rather not take a
/// hard reference on another mod can drive the same surface reflectively, through
/// <c>Type.GetType("QuinnBast.Shapez2.FirstPerson.FirstPersonControl, FirstPerson")</c>.
/// Anything cleverer would work for the first kind of consumer and not the second.
///
/// Nothing here is saved, and everything is read live. Set it from your mod's constructor:
/// that runs at mod load, before any scenario, which is early enough for all of it.
/// </summary>
public static class FirstPersonControl
{
    /// <summary>
    /// Locks the player into first person: they enter on the first frame of a session and
    /// the toggle stops working.
    ///
    /// This is the flag for a mod that wants to *be* a first-person game rather than offer
    /// first person as a view. Leaving a session still exits, because the body's position
    /// belongs to a map that no longer exists; the next session puts them straight back in.
    ///
    /// Flight is still gated on its research, so a mod using this either wants the player to
    /// buy it or should unlock it themselves - a forced first-person player who cannot fly
    /// also cannot easily go looking for shape islands.
    /// </summary>
    public static bool Forced;

    /// <summary>
    /// Whether the player is in first person right now. Written by the camera, read by
    /// anyone.
    /// </summary>
    public static bool Active { get; private set; }

    /// <summary>
    /// Called by <see cref="FirstPersonCamera"/>. Public only because a private setter and a
    /// reflection-friendly surface do not mix; treat it as internal.
    /// </summary>
    public static void ReportActive(bool active)
    {
        Active = active;
    }

    /// <summary>
    /// Whether flight exists at all. Set false and there is no research node, no row in the
    /// keybindings screen, and the gesture does nothing - for a mod that wants the player on
    /// foot, permanently.
    ///
    /// This is a different thing from <see cref="FlightRequiresResearch"/>: that one is about
    /// whether flight is *bought*, this one is about whether it is *offered*.
    ///
    /// Read when a scenario loads and when the keybindings are registered, so set it from
    /// your constructor.
    /// </summary>
    public static bool FlightEnabled = true;

    /// <summary>
    /// Whether flight has to be bought. Set false and flight is simply available, and no
    /// research node is added to the tree at all - which is what a mod wants if it is
    /// building its own progression, or none.
    ///
    /// Read when a scenario loads, so set it from your constructor.
    /// </summary>
    public static bool FlightRequiresResearch = true;

    /// <summary>
    /// What the flight node costs, in research points.
    ///
    /// **This is the stored amount, not the displayed one.** The research screen renders
    /// <c>Amount * 100</c> - <c>Format(this ResearchPointCurrency)</c> multiplies before it
    /// formats - so the default of 2 appears as "200". Set 50 here and the screen says
    /// "5k". Reading that backwards prices a node a hundredfold out and still looks
    /// plausible, so it is worth stating twice.
    /// </summary>
    public static int FlightResearchCostPoints = 2;

    /// <summary>
    /// Whether the player can fly right now - either because it was researched, or because
    /// <see cref="FlightRequiresResearch"/> is off.
    /// </summary>
    public static bool FlightUnlocked => FirstPersonResearch.FlightUnlocked;

    /// <summary>
    /// Whether waypoint travel exists at all. Off means the travel key and clicking a
    /// waypoint both refuse, and no node is added.
    /// </summary>
    public static bool TravelEnabled = true;

    /// <summary>
    /// Whether waypoint travel has to be bought. **Defaults to true**, which is a change
    /// from travel simply working - set it false to have it available from the start.
    /// </summary>
    public static bool TravelRequiresResearch = true;

    /// <summary>
    /// What the travel node costs. The same times-one-hundred display rule as
    /// <see cref="FlightResearchCostPoints"/>: 2 shows as "200".
    /// </summary>
    public static int TravelResearchCostPoints = 2;

    /// <summary>
    /// Whether the player can fast travel right now.
    /// </summary>
    public static bool TravelUnlocked => FirstPersonResearch.TravelUnlocked;

    /// <summary>
    /// Whether riding trains exists at all. Off means the board key is not registered and
    /// no node is added.
    /// </summary>
    public static bool TrainRidingEnabled = true;

    /// <summary>
    /// Whether riding trains has to be bought. Defaults to true, like the others.
    /// </summary>
    public static bool TrainRidingRequiresResearch = true;

    /// <summary>
    /// What the train-riding node costs. Same times-one-hundred display rule: 2 shows as
    /// "200".
    /// </summary>
    public static int TrainRidingResearchCostPoints = 2;

    /// <summary>
    /// Whether the player can ride trains right now.
    /// </summary>
    public static bool TrainRidingUnlocked => FirstPersonResearch.TrainRidingUnlocked;
}
