using System;
using Core;

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
    /// Whether the player is flying right now, as opposed to walking.
    /// </summary>
    public static bool Flying { get; private set; }

    /// <summary>
    /// Whether the player is riding a train right now.
    /// </summary>
    public static bool Riding { get; private set; }

    // ---- Events ---------------------------------------------------------------------
    //
    // Plain delegate fields rather than `event`, to stay reachable from a mod that would
    // rather not take a hard reference: `event` exposes only add and remove accessors, while
    // a field can be read, combined and written back through reflection.
    //
    //     FirstPersonControl.OnEntered += () => …;
    //
    // The cost of a field is that `=` clobbers everyone else's handlers, so use `+=`.
    //
    // All of them are parameterless on purpose. A consumer that needs to know *what*
    // happened can read the state above, or ask the game; putting game types in the
    // signatures would serve a referencing consumer and shut out the reflective one, which
    // is the same trade the rest of this class refuses.

    /// <summary>
    /// The player has entered first person. Fires on the frame they arrive, including the
    /// automatic entry at the start of a scenario session.
    /// </summary>
    public static Action OnEntered;

    /// <summary>
    /// The player has left first person - by leaving the session, by a mod clearing
    /// <see cref="Forced"/>, or by the camera standing down because its hook went quiet.
    /// </summary>
    public static Action OnLeft;

    /// <summary>
    /// Flight has been turned on or off. Read <see cref="Flying"/> for which.
    /// </summary>
    public static Action OnFlyingChanged;

    /// <summary>
    /// The player has boarded a train, or stopped riding one. Read <see cref="Riding"/> for
    /// which. "Stopped riding" covers stepping off, jumping off, losing the research, and
    /// the train being delivered into the hub underneath them.
    /// </summary>
    public static Action OnRidingChanged;

    /// <summary>
    /// The player has fast travelled to a waypoint - by the travel key, or by clicking a
    /// waypoint or the home icon. Fires after the body has been moved.
    /// </summary>
    public static Action OnTravelled;

    /// <summary>
    /// Called by <see cref="FirstPersonCamera"/>. Public only because a private setter and a
    /// reflection-friendly surface do not mix; treat it as internal.
    ///
    /// Each of these is the single choke point for its flag, which is why the events are
    /// raised from here rather than from the dozen places the camera assigns to the body:
    /// a transition cannot be missed if there is only one place it can happen.
    /// </summary>
    public static void ReportActive(bool active)
    {
        if (Active == active)
        {
            return;
        }

        Active = active;

        if (!active)
        {
            // Leaving first person ends both of these whether or not the camera says so -
            // it may be standing down from the watchdog, with no frame left to report in.
            ReportFlying(false);
            ReportRiding(false);
        }

        Raise(active ? OnEntered : OnLeft, active ? "OnEntered" : "OnLeft");
    }

    /// <summary>Called by <see cref="FirstPersonCamera"/>; treat it as internal.</summary>
    public static void ReportFlying(bool flying)
    {
        if (Flying == flying)
        {
            return;
        }

        Flying = flying;
        Raise(OnFlyingChanged, nameof(OnFlyingChanged));
    }

    /// <summary>Called by <see cref="FirstPersonCamera"/>; treat it as internal.</summary>
    public static void ReportRiding(bool riding)
    {
        if (Riding == riding)
        {
            return;
        }

        Riding = riding;
        Raise(OnRidingChanged, nameof(OnRidingChanged));
    }

    /// <summary>Called by <see cref="FirstPersonCamera"/>; treat it as internal.</summary>
    public static void ReportTravelled()
    {
        Raise(OnTravelled, nameof(OnTravelled));
    }

    /// <summary>
    /// Invokes one event without letting a subscriber take the frame with it.
    ///
    /// These are raised from inside the camera update, which is inside the game's own draw
    /// path. An exception escaping here would stop the camera updating for the rest of the
    /// session, and the player would see a frozen view rather than another mod's bug - so it
    /// is caught, reported once per throw, and the frame carries on.
    ///
    /// Reported through <c>DebugLogger</c> rather than the mod's own logger because this
    /// class deliberately has no state beyond its settings, and a logger field would be one
    /// more thing a consumer could reach and break.
    /// </summary>
    private static void Raise(Action handler, string name)
    {
        if (handler == null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception exception)
        {
            DebugLogger.Error?.Log(
                "First Person: a subscriber to " + name + " threw: " + exception);
        }
    }

    // ---- Movement -------------------------------------------------------------------
    //
    // Live values, read every frame, initialised from the defaults in FirstPersonTuning.
    // Distances are in **tiles**, and one tile is one world unit - `ScreenUtils`' tile DDA
    // steps by 1 while its chunk walk steps by 20 - so a chunk is 20 of these and a belt is
    // one wide. Speeds are tiles per second.

    /// <summary>
    /// How fast the player walks, in tiles per second.
    ///
    /// The default is deliberately close to belt speed: walking beside a shape and watching
    /// it keep pace is most of the point of being down here.
    /// </summary>
    public static float WalkSpeed = FirstPersonTuning.WalkSpeed;

    /// <summary>
    /// How fast the player flies, in tiles per second, before sprinting. Vertical movement
    /// uses the same number.
    ///
    /// Lowered from 180 once flight turned out to make trains pointless - you could outrun
    /// one on foot in the air. The sprint key still gets the old speed, so crossing the map
    /// is a decision rather than the default.
    /// </summary>
    public static float FlySpeed = FirstPersonTuning.FlySpeed;

    /// <summary>
    /// What the game's own "move faster" key multiplies walking and flight by. Applies to
    /// climbing and diving as well as to horizontal movement.
    /// </summary>
    public static float SprintMultiplier = FirstPersonTuning.SprintMultiplier;

    /// <summary>
    /// Tiles per second of initial upward speed. The apex is <c>v^2 / 2g</c> with a gravity
    /// of 26, and what a jump can get onto is governed by the step allowance as well - see
    /// <see cref="FirstPersonTuning.JumpSpeed"/> for the table. The default clears three
    /// building layers and not four.
    /// </summary>
    public static float JumpSpeed = FirstPersonTuning.JumpSpeed;

    /// <summary>
    /// How far along the crosshair a wagon can be and still be boardable, in tiles.
    /// </summary>
    public static float BoardReach = FirstPersonTuning.BoardReach;

    /// <summary>
    /// Writes a line to <c>Player.log</c> for every toolbar scroll: the list it walked, where
    /// the selection was, where it was sent, and - the point of the whole thing - where the
    /// selection actually ended up afterwards.
    ///
    /// Off. It was on while the wheel was being worked out, and it earned its keep: the
    /// toolbar answers a selection with side effects the calling code cannot see, and reading
    /// back what actually happened is what found both the placer crash that was cancelling
    /// selections and the `values[0]` fallback in
    /// `PrioritizeToolbarElementFromCurrentCategorySelector`. Neither was visible from the
    /// code, and neither was guessable from watching it. Left in place for the next time.
    /// </summary>
    public static bool LogToolbar;

    /// <summary>
    /// Whether standing on a belt carries the player along it, at the belt's real speed.
    ///
    /// **Off by default**, which is a change from it always happening. It is a lovely thing
    /// to discover and a bad thing to live with: a factory floor is mostly belt, so standing
    /// still to work on something meant being quietly carried away from it. The demo and the
    /// daily experience wanted different answers and the daily one won.
    ///
    /// Set it true to have it back - nothing else about belts changes, and a belt is still
    /// something you can stand on.
    /// </summary>
    public static bool BeltsCarryPlayer;

    /// <summary>
    /// How high above a ridden wagon's own origin the rider stands, in tiles.
    ///
    /// Exposed because it is the one number in the mod that cannot be derived. A wagon's
    /// dimensions are not readable from anything a mod can reach - the renderer is handed
    /// finished matrices rather than a size, and the one constant that would give it away,
    /// `VisualizationResources.VisualizationHeight`, is authored ScriptableObject data. So
    /// it is chosen by eye, and it has been chosen wrong three times; a knob beats a fourth
    /// rebuild.
    /// </summary>
    public static float TrainRideHeight = FirstPersonTuning.TrainRideOffset;

    /// <summary>
    /// How far from the player a shape or fluid patch stays rendered no matter which way the
    /// player is looking, in world units. 2000 is a hundred chunks.
    ///
    /// Set to 0 to leave the game's own culling alone. Raising it keeps more of the map
    /// drawn, which is the trade: asteroids that never blink out, for more to draw.
    /// </summary>
    public static float ResourceRenderRadius = FirstPersonTuning.ResourceRenderRadius;

    /// <summary>
    /// Whether the toggle key can put an ordinary session into first person.
    ///
    /// **Off by default**, which is the mod's whole shape: first person is a *game*, not a
    /// view. It is entered by playing the First Person scenario, or by a mod setting
    /// <see cref="Forced"/> - not by pressing a key in a save that was not built for it,
    /// where it is a novelty that outlives its welcome in about a minute and leaves the
    /// player's camera somewhere strange.
    ///
    /// With this off the toggle binding is **not registered at all**, so there is no dead
    /// row in the keybindings screen and no key that silently does nothing. Turn it on from
    /// your mod's constructor to get the old behaviour back.
    ///
    /// It does not affect a locked-in session: a forced player still cannot leave, because
    /// the key that would let them is the one this governs.
    /// </summary>
    public static bool ToggleEnabled;

    /// <summary>
    /// The map the First Person scenario generates. Change the fields on it to change the
    /// map; see <see cref="FirstPersonMapGeneration"/> for what each one does and why
    /// percentages above 100 behave the way they do.
    ///
    /// Read when the new-game menu asks for the scenario presets, so setting it from your
    /// mod's constructor is in time.
    /// </summary>
    public static readonly FirstPersonMapGeneration ScenarioMapGeneration =
        new FirstPersonMapGeneration();

    /// <summary>
    /// Whether the mod's own scenario is offered in the new-game menu.
    ///
    /// The scenario is a first-person game: entering it locks the player in the same way
    /// <see cref="Forced"/> does, and it generates a denser map. Turn this off and both the
    /// scenario and its parameter preset disappear from the menu - useful for a mod that
    /// wants first person as a view rather than as a game, and for testing the rest of the
    /// mod without the extra entry.
    ///
    /// Read while game data loads, which happens after mods are constructed, so setting it
    /// from your constructor is in time. It also gates
    /// <see cref="FirstPersonScenario.IsCurrentSession"/>, so turning it off mid-session
    /// releases a player who is already in one rather than stranding them.
    /// </summary>
    public static bool ScenarioEnabled = true;

    /// <summary>
    /// Whether the player is locked into first person right now - either because a mod set
    /// <see cref="Forced"/>, or because the running session is the mod's own scenario.
    ///
    /// This is what the camera actually reads. <see cref="Forced"/> stays a plain settable
    /// field so a consumer can drive it.
    /// </summary>
    public static bool LockedIn => Forced || FirstPersonScenario.IsCurrentSession;

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
    /// formats - so 30 appears as "3k". Reading that backwards prices a node a hundredfold
    /// out and still looks plausible, so it is worth stating twice.
    ///
    /// Priced against the authored ladder in `default-scenario.json`, where the top of the
    /// range is 42 for train transfer stations, 48 for an extra rail line colour, 50 for a
    /// third factory or space floor, 90 for the large platform pack and 200 for vortex
    /// delivery.
    ///
    /// Flight sits above the third-floor unlock because it is worth more than one: it does
    /// not add somewhere to build, it removes traversal as a problem for the rest of the
    /// save. Anything cheaper and there is no decision to make.
    /// </summary>
    public static int FlightResearchCostPoints = 60;

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
    /// <see cref="FlightResearchCostPoints"/>: 50 shows as "5k".
    ///
    /// Level with a third factory floor, and above the rail line colour at 48 that is the
    /// usual benchmark for "worth buying". Crossing the map instantly is worth at least as
    /// much as somewhere else to build.
    /// </summary>
    public static int TravelResearchCostPoints = 50;

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
    /// What the train-riding node costs. Same times-one-hundred display rule: 48 shows as
    /// "4.8k".
    ///
    /// Exactly the rail line colour's price, which is the node players point at when they
    /// say an unlock is worth having. The cheapest of the three, because the game gates it
    /// hardest by itself - it does nothing at all until there are trains to ride.
    /// </summary>
    public static int TrainRidingResearchCostPoints = 48;

    /// <summary>
    /// Whether the player can ride trains right now.
    /// </summary>
    public static bool TrainRidingUnlocked => FirstPersonResearch.TrainRidingUnlocked;
}
