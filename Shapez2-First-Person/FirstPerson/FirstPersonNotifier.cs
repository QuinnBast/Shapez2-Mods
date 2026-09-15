using Core.Localization;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Player-facing output, without building any UI.
///
/// `HUDEvents` is a bag of public multicast events the HUD listens to, and firing one is
/// the cheapest thing a mod can show a player. There is no static accessor for it, so the
/// instance is captured as the HUD constructs itself - `HUDPart.Construct` is public and
/// receives it. That fires once per HUD part, which is many times; it is the same object
/// every time, so overwriting is harmless.
///
/// This exists because a key that silently does nothing is the worst failure mode a mod
/// has. Before it, pressing the fly key without the research wrote a line to `Player.log`
/// and nothing else.
/// </summary>
public sealed class FirstPersonNotifier
{
    private HUDEvents Events;

    public void Capture(HUDEvents events)
    {
        if (events != null)
        {
            Events = events;
        }
    }

    public void Release()
    {
        Events = null;
    }

    /// <summary>
    /// Null until the first HUD part constructs, and null again between sessions, so every
    /// call has to tolerate not being shown.
    /// </summary>
    public void Show(string message, HUDNotificationType type = HUDNotificationType.Info)
    {
        Events?.ShowNotification.Invoke(new HUDNotificationData(type, new RawText(message)));
    }
}
