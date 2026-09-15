using UnityEngine;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// One frame of the mod's own input, read early and read once.
///
/// It is filled from a prefix on <c>HUD.OnGameUpdate</c>, which is before every HUD part and
/// therefore before anything else that might want the same keys. That ordering is what makes
/// the shared defaults work: consuming a binding marks every other *active* binding with the
/// same key code as consumed too, so reading ours first is what stops Space from also
/// flipping the interaction scope and Tab from also cycling a building variant. The mod used
/// to do that with three hand-written suppression calls; the input system does it now.
///
/// When first person is off only <see cref="Toggle"/> is read. Consuming the rest would take
/// Space and Tab away from a player who is not even in first person.
/// </summary>
public struct FirstPersonInput
{
    public bool Toggle;
    public bool Fly;
    public bool Travel;
    public bool Board;
    public bool FreeCursor;
    public bool JumpPressed;
    public bool JumpHeld;
    public bool SinkHeld;

    /// <summary>
    /// Modifiers stay raw. A held modifier is not an action and the settings screen has
    /// nowhere sensible to put one. This is the only one the camera needs - it chooses where
    /// you arrive on entry; the wheel modifiers are read where the wheel is.
    /// </summary>
    public bool SpawnHere;

    public static FirstPersonInput Read(InputDownstreamContext context, bool active)
    {
        FirstPersonInput input = default;

        input.Toggle = context.ConsumeWasActivated(FirstPersonKeybindings.Toggle);
        input.SpawnHere = Input.GetKey(FirstPersonTuning.SpawnHereModifier);

        if (!active)
        {
            return input;
        }

        // Guarded because these bindings are not registered when their feature is disabled,
        // and an unregistered id is a KeyNotFoundException out of a frame hook, not a false.
        input.Fly = FirstPersonControl.FlightEnabled
                    && context.ConsumeWasActivated(FirstPersonKeybindings.Fly);
        input.Travel = FirstPersonControl.TravelEnabled
                       && context.ConsumeWasActivated(FirstPersonKeybindings.Travel);
        input.Board = FirstPersonControl.TrainRidingEnabled
                      && context.ConsumeWasActivated(FirstPersonKeybindings.Board);
        input.FreeCursor = context.ConsumeIsActive(FirstPersonKeybindings.FreeCursor);
        input.JumpPressed = context.ConsumeWasActivated(FirstPersonKeybindings.Jump);
        input.JumpHeld = context.ConsumeIsActive(FirstPersonKeybindings.Jump);
        input.SinkHeld = context.ConsumeIsActive(FirstPersonKeybindings.Sink);

        return input;
    }
}
