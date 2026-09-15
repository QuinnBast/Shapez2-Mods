using System;
using System.Collections.Generic;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Registers the mod's keys as a real keybindings layer, so they appear in the game's own
/// settings screen, persist when rebound, and take part in the input system.
///
/// That last point is the reason this exists rather than a config file. Two consumers walk
/// <c>Keybindings.Layers</c>: <c>HUDKeybindingsRenderer</c>, which is what puts a
/// rebindable row on the settings screen, and <c>GameInputManager</c>, which is what feeds
/// <c>InputDownstreamContext</c>. A key read with <c>Input.GetKey</c> is outside both, and
/// so cannot know what else is bound to it - which is exactly how `F6` spent several
/// versions quietly triggering <c>debug.step-speed</c> and looking like "the mod pauses the
/// game".
///
/// Persistence is free: <c>KeybindingsLayer</c>'s constructor calls
/// <c>AssignFullIdAndLoad</c> on every binding, so a player's rebind is loaded the moment
/// the layer is built.
/// </summary>
public static class FirstPersonKeybindings
{
    public const string LayerId = "first-person";

    public const string Toggle = LayerId + ".toggle";
    public const string Fly = LayerId + ".fly";
    public const string FreeCursor = LayerId + ".free-cursor";
    public const string Travel = LayerId + ".travel";
    public const string Board = LayerId + ".board";
    public const string Jump = LayerId + ".jump";
    public const string Sink = LayerId + ".sink";

    private static KeybindingsLayer Registered;

    public static bool Ready => Registered != null;

    /// <summary>
    /// Appends the layer, once. Called from a tick rather than from a hook on
    /// <c>Keybindings</c>'s constructor, because that runs inside
    /// <c>GlobalsInitialization</c> - quite possibly before mods have loaded at all, in
    /// which case such a hook would simply never fire. Ticking until <c>Globals</c> has data
    /// is immune to load order.
    /// </summary>
    public static bool EnsureRegistered(ILogger logger)
    {
        if (Registered != null)
        {
            return true;
        }

        Keybindings keybindings = Globals.Data?.Keybindings;
        if (keybindings == null)
        {
            return false;
        }

        // A hot reload gives this assembly a fresh static while the old layer is still in
        // the array. Ids are strings, so the old layer answers our lookups perfectly well -
        // registering a second one would only put a duplicate section in the settings.
        if (keybindings.KeybindingsById.ContainsKey(Toggle))
        {
            logger?.Info?.Log("First Person: keybindings already registered by a previous load.");
            return true;
        }

        List<Keybinding> bindings = new List<Keybinding>
        {
            // The defaults still collide with the game on purpose in places - jump on Space
            // and the cursor on Tab are the same keys the player already associates with
            // those actions. The difference is that the collision is now visible in the
            // settings screen, rebindable, and resolved by the game: consuming a binding
            // marks every other active binding sharing its key code as consumed too, so
            // whichever is read first wins and the other stays quiet.
            new Keybinding("toggle", new KeySet(FirstPersonTuning.ToggleKey)),
            new Keybinding("free-cursor", new KeySet(FirstPersonTuning.CursorKey)),
            new Keybinding("jump", new KeySet(FirstPersonTuning.JumpKey)),
            new Keybinding("sink", new KeySet(FirstPersonTuning.SinkKey)),
        };

        // A row for a feature a mod has switched off would do nothing, so it is left out
        // entirely. FirstPersonInput must skip reading these to match - looking up an
        // unregistered id throws rather than returning false.
        if (FirstPersonControl.FlightEnabled)
        {
            bindings.Add(new Keybinding("fly", new KeySet(FirstPersonTuning.FlyKey)));
        }

        if (FirstPersonControl.TravelEnabled)
        {
            bindings.Add(new Keybinding("travel", new KeySet(FirstPersonTuning.TravelKey)));
        }

        if (FirstPersonControl.TrainRidingEnabled)
        {
            bindings.Add(new Keybinding("board", new KeySet(FirstPersonTuning.BoardKey)));
        }

        KeybindingsLayer layer = new KeybindingsLayer(LayerId, bindings);

        KeybindingsLayer[] layers = keybindings._Layers;
        Array.Resize(ref layers, layers.Length + 1);
        layers[layers.Length - 1] = layer;
        keybindings._Layers = layers;

        // Both halves of what the constructor does for the built-in layers: the change event
        // so the settings screen refreshes, and the id lookup that ConsumeWasActivated uses.
        layer.Changed.Register(keybindings._Changed.Invoke);

        foreach (Keybinding binding in layer.Bindings)
        {
            keybindings._KeybindingsById[binding.Id] = binding;
        }

        Registered = layer;
        logger?.Info?.Log("First Person: registered " + layer.Bindings.Count + " keybindings.");
        return true;
    }

    /// <summary>
    /// Takes the layer back out on unload, so a disabled mod does not leave a section of
    /// dead rows in the settings screen. The player's rebinds survive in preferences and
    /// come back with the layer.
    /// </summary>
    public static void Unregister()
    {
        KeybindingsLayer layer = Registered;
        Registered = null;

        if (layer == null)
        {
            return;
        }

        Keybindings keybindings = Globals.Data?.Keybindings;
        if (keybindings == null)
        {
            return;
        }

        layer.Changed.Unregister(keybindings._Changed.Invoke);

        foreach (Keybinding binding in layer.Bindings)
        {
            keybindings._KeybindingsById.Remove(binding.Id);
        }

        List<KeybindingsLayer> remaining = new List<KeybindingsLayer>(keybindings._Layers);
        remaining.Remove(layer);
        keybindings._Layers = remaining.ToArray();
    }
}
