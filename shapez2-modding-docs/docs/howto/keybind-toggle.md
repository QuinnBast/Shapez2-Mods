# Bind a key

**Problem.** Your overlay needs an on/off key.

There are two routes, and the honest summary is: the quick one takes five minutes, the
proper one requires reaching into the game's keybinding registry.

## Quick: legacy Unity input

The game uses `KeyCode`-based bindings, so `UnityEngine.Input` works. Add the reference:

```xml
<Reference Include="UnityEngine.InputLegacyModule">
  <HintPath>$(SPZ2_PATH)\UnityEngine.InputLegacyModule.dll</HintPath>
  <Private>False</Private>
</Reference>
```

Then poll in your tick:

```csharp
using UnityEngine;

private void Tick(float deltaTime)
{
    if (Input.GetKeyDown(KeyCode.F4))
    {
        Enabled = !Enabled;
        Logger.Info?.Log($"overlay {(Enabled ? "on" : "off")}");
    }
}
```

Because `OnTick` only fires during a session, this is automatically inert in menus.

**What you give up:** the key is hard-coded, invisible in the game's keybinding
settings, and can collide with a vanilla or another mod's binding. Fine for a personal
tool or a first iteration; not what you want to ship widely.

Pick something unlikely to collide (`F` keys are safer than letters) and make it
configurable via your [mod save data](save-data.md) or a config file if players will
use it.

## Proper: a real keybinding

Registered bindings appear in the settings UI, are rebindable, respect modifiers, and
route through the same consumption model as vanilla — which matters, because that is
what stops your key firing while a dialog or text field has focus.

The pieces involved:

```csharp
// Game.Core
public class Keybinding
{
    public string Id { get; }
    public IText Title       => ("keybinding." + Id).T();
    public IText Description => ("keybinding." + Id + ".description").T();
}

new Keybinding("toggle-research", new KeySet(KeyCode.T));
```

Defaults are produced by `DefaultKeybindings.ComputeDefaults()`, which returns layers of
bindings; `Keybindings` copies those layers at construction and indexes them into
`KeybindingsById`. Consumption happens through `InputDownstreamContext`:

```csharp
if (context.ConsumeWasActivated("my-mod.toggle-overlay")) { … }
```

`ConsumeWasActivated` marks the input as handled so nothing downstream sees it;
`IsActivated` checks without consuming.

You will also want translation entries for `keybinding.<id>` and
`keybinding.<id>.description`, or the settings UI shows the raw key.

> [!WARNING]
> ShapezShifter has no keybinding registration API at the time of writing, and
> `DetourHelper`'s postfix overloads cannot rewrite a return value — so you cannot
> simply postfix `ComputeDefaults()` and append. Getting a binding registered means
> reaching the `Keybindings` instance after construction (its fields are reachable via
> the [publicizer](../publicizer.md)) and adding to it. That is unverified territory:
> confirm against the assemblies for your game version before relying on it.

## Pressing a keybinding on the player's behalf

Sometimes the thing you want to drive has no API you can reach. It is worth being sure of
that first — `Toolbar.dll` and `Game.Hud.View.dll` are not in the usual decompile set, and
"no API" turned out to mean "not decompiled" for the toolbar: `HUDToolbarView` has a
perfectly good depth-parameterised `TryCycleChildSlots`, and driving that directly beats
synthesising `next-toolbar`, which only reaches the one depth it is wired to.

When the technique *is* the right answer, it looks like this. `toolbar.select-slot-0` …
`select-slot-14` are ordinary keybindings, so you can press one instead of reaching for the
internals.

The three conditions `ConsumeWasActivated` tests are all writable state on the context:

```csharp
private static void Press(InputDownstreamContext context, string id)
{
    if (!context.AllBindings.TryGetValue(id, out Keybinding binding)) return;

    context.LastBindings.Remove(binding);       // "was not held last frame"
    context.ConsumedBindings.Remove(binding);   // "nobody has taken it yet"
    context.ActiveBindings[binding] = KeySet.EMPTY;
}
```

All three collections are `protected`, so this needs the [publicizer](../publicizer.md).

**Use `KeySet.EMPTY`, not the binding's real key set.** `TryConsume` de-duplicates
bindings that share a key:

```csharp
if (keySet.Code != 0 && activeBinding.Value.Code == keySet.Code) ConsumedBindings.Add(activeBinding.Key);
```

`KeySet.EMPTY` has `Code == KeyCode.None`, which is `0`, so neither that branch nor the
controller one fires and consuming your synthetic press cannot swallow an unrelated
binding that happens to sit on the same key.

**Inject it before whoever reads it.** The context is walked in a fixed order —
`InputManager` → `DialogStack` → `HUD` (every `HUDPart`) → `PlayerInteractionOrchestrator`
→ `SystemButtons`. For anything a HUD part consumes, that means a *prefix* on
`HUD.OnGameUpdate`; from a hook in `PlayerInteractionOrchestrator` you are already too
late, and nothing will happen.

The virtue of this over calling into the UI is that it keeps working when the UI changes:
you are using the same entry point the keyboard uses.

## Gotchas

- Legacy `Input` does not know about UI focus. If the player is typing in a dialog, your
  key still fires. Check for an open dialog before acting on it, or accept the rough
  edge.
- Do not poll input from a draw hook. Draw hooks can run more than once per frame in
  some configurations; a tick fires once.
