# Show a notification or open a HUD screen

**Problem.** You want to tell the player something, or send them to the research screen,
without building any UI.

**Solution.** `HUDEvents` is a bag of public multicast events that the HUD listens to.
Firing one is the cheapest possible player-facing output.

## Getting hold of `HUDEvents`

There is no static accessor for the HUD. The reliable route is to capture the instance
as the HUD constructs itself — `HUDPart.Construct` is public and receives it:

```csharp
public void Construct(HUDEvents events, Player player, ILogger logger, IAnalyticsTracker analytics)
```

So postfix that and keep the reference:

```csharp
private HUDEvents Events;

public MyMod(ILogger logger)
{
    ConstructHook = DetourHelper.CreatePostfixHook<HUDPart, HUDEvents, Player, ILogger, IAnalyticsTracker>(
        (part, events, player, log, analytics) => part.Construct(events, player, log, analytics),
        (part, events, player, log, analytics) => Events = events);
}
```

This fires once per HUD part, which is many times — you are simply overwriting the same
reference with the same object, which is harmless. `Events` is null until the first HUD
part constructs, so null-check before using it.

## Show a notification

```csharp
Events?.ShowNotification.Invoke(new HUDNotificationData(
    HUDNotificationType.Info,
    new RawText("Overlay enabled")));
```

The full constructor:

```csharp
HUDNotificationData(
    HUDNotificationType type,
    IText text,
    Sprite overrideIcon = null,
    Action action = null,
    float showDuration = 5f)
```

- **`text`** — an `IText`. `new RawText("…")` for literal strings; `"key".T()` for
  anything a player will read in their own language
  ([translations](add-translations.md)).
- **`action`** — a callback invoked if the player clicks the notification. This is the
  cheapest interactive UI in the game: notify *and* offer a jump to the relevant thing.
- **`showDuration`** — seconds. Default 5.

Use `RawText` only for debugging output. Anything shipped should be a translation key.

## Open a HUD screen

The same event bag drives most of the game's screens:

```csharp
Events?.ShowResearch.Invoke();
Events?.ShowStatistics.Invoke();
Events?.ShowBlueprintLibrary.Invoke();
Events?.ShowWiki.Invoke();
Events?.ShowWikiEntry.Invoke(new WikiEntryId("…"));
Events?.ShowResearchShop.Invoke();
Events?.ShowResearchSideQuests.Invoke();
Events?.ShowResearchPlayerLevelAndFocusOn.Invoke(new PlayerLevelGoalId("…"));
Events?.RequestAddBlueprintToLibrary.Invoke(blueprint, slot);
```

`ShowPauseMenu`, `ShowPauseMenuSettings`, and `ShowPauseMenuAdditionalContent` are there
too. Browse <xref:Root.HUDEvents> in the API reference for the current full list — it is a flat class of public fields, so it reads as documentation.

Two patterns these enable without writing UI:

- **Notification with an action that opens a screen** — "3 platforms are starved" →
  click → statistics.
- **Deep-link into the wiki** for your own building, if you register a wiki entry.

## Registering instead of invoking

The same events can be *listened* to, which is how you react to the player opening
something:

```csharp
Events.ShowStatistics.Register(OnStatisticsOpened);
// …
Events.ShowStatistics.Unregister(OnStatisticsOpened);
```

`MultiRegisterEvent` supports many handlers, so registering does not displace the
game's own. Always unregister on dispose.

## Add a button to one of the game's own screens

There is no API for this, but the screens are Unity prefabs and their buttons are fields on
the `HUDPart`, so the cheapest button is a clone of one already there:

```csharp
GameObject clone = UnityEngine.Object.Instantiate(menu.UISaveBtn.gameObject,
                                                  menu.UISaveBtn.transform.parent);
clone.transform.SetSiblingIndex(menu.UISaveBtn.transform.GetSiblingIndex() + 1);
```

A clone inherits the game's styling and its place in the layout, which is most of the work.
Hook the screen's `Show()` to do it - `HUDPauseMenu.Show` is private and non-generic, so
[the publicizer](../publicizer.md) and an ordinary postfix are enough. Its `Construct` is not
a usable hook point: it takes ten dependencies, and `DetourHelper`'s expression overloads
stop at eight.

**The clone is never constructed, and that is the whole difficulty.** The game's DI walks a
prefab's serialized `ChildComponentReferences`, which a runtime clone is not in, so
`[Construct]` never runs on it. Three consequences, in rising order of how long they take to
diagnose:

- **`HUDLocalizedText` throws.** Setting `HUDMenuButton.Text` reaches `UpdateView`, which
  opens with `if (Resolver == null) throw new Exception("Accessing HUDLocalizedText ... which
  has not been constructed yet.")`. Set the underlying `TMP_Text.text` directly instead -
  nothing else calls `UpdateView` on an unconstructed clone, so the text stays put.
- **Hovering it is a `NullReferenceException`.** `HUDMenuButton.UpdateActiveState` calls
  `UISoundPlayer.PlayButtonHover()`, guarded only by `interactable`. Copy the field off the
  template: `clone.UISoundPlayer = template.UISoundPlayer`.
- **No click sound or punch animation.** `Construct` is what adds `PlayOnClickAnimation` as a
  listener. Add it yourself, after the sound player is set.
- **Its tooltip opens and says nothing.** This one looks like a missing translation and is not.
  `HUDIconButton` resolves its serialized ids into `_TooltipTitle` / `_TooltipText` in
  `[Construct]`, and `Run()` then copies those two fields onto the `HUDTooltipTarget` — so on a
  clone it writes **null over the target's own perfectly good serialized ids**. Set the backing
  fields and re-run the game's own config method:

  ```csharp
  button._TooltipKeybinding = string.Empty;        // the template's belongs to the template
  button._TooltipTitle = new RawText("Mod Profiler");
  button._TooltipText  = new RawText("Frame time, heap and a flame graph.");
  button.UpdateTooltipConfig();
  ```

  Fields, not the `TooltipTitle` / `TooltipText` properties' siblings: those two setters are
  safe, but `HasTooltip`, `TooltipKeybinding`, `Interactable` and `Highlighted` all end in
  `OnHighlightChanged`, which dereferences `TutorialHighlightProvider` — another thing
  `[Construct]` would have set. `UpdateTooltipConfig` touches only the tooltip target.


Nothing disposes the clone either - `OnDispose` is driven from the same serialized child
list - so its DOTween hover tweens outlive it. Kill them from a postfix on the screen's
`OnDispose`, or they end up pointing at a destroyed transform when the session tears down.

For button *labels*, `RawText` is right here in a way it usually is not: a dev-tool button
has no translation key, and `.T()` on a missing one renders `?the-key`.

## Make your own overlay modal

**Problem.** You drew a panel — IMGUI, or your own canvas — and the game carries on
underneath it. The wheel zooms the camera, a click places a building, and buttons on the
toolbar behind the panel still take the click.

There are two input systems in play and your panel is in neither of them.

**The game's input.** `GameSessionOrchestrator.Update` fills one `InputDownstreamContext`
per frame and walks it through a fixed order:

```
InputManager.OnGameUpdate()        ← the context is built here
DialogStack.Update(context)
HUD.OnGameUpdate(context, …)       ← every HUDPart, in order
PlayerInteractionOrchestrator.OnGameUpdate(context, …)   ← drives CameraController
SystemButtons.OnGameUpdate(context)
InputManager.PostInputsUpdate()
```

Screens block the world by **consuming**. `HUDStatistics.OnGameUpdate` ends with exactly
this, and that is the entire mechanism:

```csharp
context.ConsumeToken("HUDPart$confine_cursor");
context.ConsumeAll();
```

`ConsumeAll` clears the active bindings, the mouse delta and the wheel delta, so everything
downstream sees an idle frame. The token is read by `GameInputManager` as "a fullscreen
overlay is open", which frees the cursor and stops border panning.

A mod is not a `HUDPart`, so it hooks the chain instead. **Postfix `HUD.OnGameUpdate`** —
after it, so the debug console and the rest of the HUD parts still get their input, and
before `PlayerInteractionOrchestrator`, so the camera and placement get nothing:

```csharp
DetourHelper.CreatePostfixHook<HUD, InputDownstreamContext, FrameDrawOptions>(
    (hud, context, options) => hud.OnGameUpdate(context, options),
    (hud, context, options) =>
    {
        if (!Panel.Open) return;
        context.ConsumeToken("HUDPart$confine_cursor");
        context.ConsumeAll();
    });
```

**Escape is the exception, and it has to go in a prefix.** The pause menu is a `HUDPart`, so
a cancel still unconsumed when the parts run opens the pause menu behind you. Take it before
any part sees it:

```csharp
DetourHelper.CreatePrefixHook<HUD, InputDownstreamContext, FrameDrawOptions>(
    (hud, context, options) => hud.OnGameUpdate(context, options),
    (hud, context, options) =>
    {
        if (Panel.Open && context.ConsumeWasActivated("global.cancel")) Panel.Close();
        return (context, options);
    });
```

`ConsumeWasActivated` indexes a dictionary the game owns, so an id this build does not have
throws `KeyNotFoundException` rather than returning false. Guard it, and log once rather
than once a frame — this runs inside the frame loop.

**The game's UI is a separate problem.** The toolbar and top bar are uGUI, routed by an
EventSystem raycast that knows nothing about the input context or about IMGUI. Consuming
input does not stop a button behind your panel taking a click. One transparent full-screen
graphic on a canvas of your own, with a high sorting order, swallows it:

```csharp
Canvas canvas = blocker.AddComponent<Canvas>();
canvas.renderMode = RenderMode.ScreenSpaceOverlay;
canvas.sortingOrder = 30000;
blocker.AddComponent<GraphicRaycaster>();

Image sheet = child.AddComponent<Image>();
sheet.color = new Color(0f, 0f, 0f, 0f);   // invisible; raycastTarget is what matters
```

Draw the dimming in IMGUI instead of tinting that Image. Whether IMGUI lands over or under a
ScreenSpace-Overlay canvas is a build detail, and getting it wrong paints your panel black.

## Make an IMGUI page look like the game's

**Problem.** The overlay works, and it looks like a 2008 toolkit dropped on top of shapez:
grey bevelled buttons, a 16-pixel scrollbar, black text on a light box. Next to Statistics or
Research it reads as a different program.

The game's pages are, visually, four things — a dark translucent ground, near-white text over a
muted blue-grey secondary, soft-cornered surfaces lifted a few percent off that ground, and one
warm accent used *only* to say what is selected. IMGUI ships none of them. Every one is a
texture you generate at startup, and none of it needs a `HUDPart`.

**Rounded surfaces come from a signed distance field, nine-sliced.** One square white mask
serves every rectangle on the page: the corners are copied unscaled and the middle is stretched.

```csharp
float px = Mathf.Abs(x + 0.5f - half) - (half - radius);
float py = Mathf.Abs(y + 0.5f - half) - (half - radius);
float ox = Mathf.Max(px, 0f), oy = Mathf.Max(py, 0f);
float d  = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(px, py), 0f) - radius;

float alpha = Mathf.Clamp01(0.5f - d);                       // the filled shape
// …or, for a one-pixel border: alpha - Mathf.Clamp01(0.5f - (d + 1f))
```

Draw it through a `GUIStyle` whose `border` is set — `GUI.DrawTexture` does not nine-slice:

```csharp
GUIStyle slice = new GUIStyle { normal = { background = mask }, border = new RectOffset(10, 10, 10, 10) };

GUI.color = new Color(1f, 1f, 1f, 0.04f);                    // GUI.color tints style backgrounds
slice.Draw(rect, GUIContent.none, false, false, false, false);
GUI.color = Color.white;
```

`GUIStyle.Draw` only paints during `EventType.Repaint`; guard it, or it throws.

**Sample the palette off a screenshot, and get the polarity right.** The numbers below are from
the Statistics page, and the one that matters is not a colour, it is a direction:

| | sampled |
| --- | --- |
| page ground | `36,55,90` |
| content surface (a stat tile) | `21,35,51` — **darker** than the ground |
| inner well (the chart box in a tile) | `18,29,40` — darker still |
| control, unselected | `66,84,113` — **lighter** than the ground |
| control, selected | `97,105,125` |

Content is recessed and controls lift. That is the opposite of the usual dark-UI habit of
floating cards a few percent of white above the background, and going the wrong way costs you
twice: cards only become visible if you drop the page to roughly half the brightness of a real
one, and then every label on them is fighting a near-black ground. Build the surface as black at
about 35% over the ground, the well at 50%, and let the buttons be the white.

The values live in authored prefabs, so none of this is readable from an assembly — a screenshot
and an eyedropper are the whole method, and they are enough.

**Borrow the game's actual page background.** Every full-screen page in shapez — Statistics,
Research, the Wiki, the blueprint library — holds one `HUDFullscreenDialogBackground`, and that
component is five stacked images: a vignette, a glow at the top, a glow at the bottom, and two
big faint line layers that its `OnUpdate` rotates at 2 and -1.333 degrees a second. The
blue-over-warm wash they produce is most of what a shapez page *looks* like, and no hand-rolled
gradient will pass for it.

Take the sprites, not the component. Cloning it gives you an unconstructed `HUDComponent` whose
`OnUpdate` ends in `ConsumeAll()` — a second thing fighting you for the input your own overlay
already consumes. Read the five layers off the live instance instead:

```csharp
// FindObjectsOfTypeAll, not FindObjectsOfType: every one of these is inactive until its page
// opens (HUDStatistics.Construct ends with gameObject.SetActive(false)).
var source = Resources.FindObjectsOfTypeAll<HUDFullscreenDialogBackground>()[0];
Image image = source.UIVignetteTransform.GetComponent<Image>();
Sprite sprite = image.sprite;

// The sprites are atlas-packed, so the whole texture is the wrong thing to draw.
Rect uv = new Rect(sprite.textureRect.x / sprite.texture.width,
                   sprite.textureRect.y / sprite.texture.height,
                   sprite.textureRect.width / sprite.texture.width,
                   sprite.textureRect.height / sprite.texture.height);

GUI.color = image.color;                     // the tint is authored; take it too
GUI.DrawTextureWithTexCoords(area, sprite.texture, uv, true);
```

What you cannot take is **where each layer goes**. The prefab's rectangles are authored, and
reading the live instance's transforms back gives you a pose rather than a layout — `Construct`
parks the vignette at half height and the glows at three times width until `Show` animates them
in. Compose the layers yourself against a screenshot; the art and the colours are what carry it.

Rotation is `GUIUtility.RotateAroundPivot(degrees, pivot)` with `GUI.matrix` saved and restored
around it, and `Time.unscaledTime` so it keeps moving while the game is paused behind you.

**Do not cache "I found it" as a bool.** Those sprites belong to the session's HUD and are
destroyed on a quit to the main menu. A destroyed `UnityEngine.Object` compares equal to null, so
the references tell you the truth for free — derive availability from them every time you ask.
Caching the answer gives you a flag that says yes while every layer silently draws nothing, and
whatever retry you wrote never fires again.

**Keep a mask per corner size.** A nine-sliced texture cannot be drawn smaller than the sum of
its own borders without squashing them, so a radius-8 mask with a 10-pixel border is wrong for a
6-pixel scrollbar or a 1-pixel-wide flame graph frame. Three masks (say radius 8, 4 and 3) cover
a whole page; anything narrower than its own radius should just be a plain rectangle.

**`GUILayout.BeginVertical(style)` is the only way to get a background behind a group whose
height you do not know yet** — which is every section of a page built from tables. The style's
`padding` becomes the card's inset and its `margin` the gap to the next one.

**The scrollbar cannot be restyled in place.** `GUI.VerticalScrollbar` resolves its thumb by
looking up `style.name + "thumb"` in `GUI.skin`, so a custom thumb means replacing `GUI.skin` —
which is process-wide and shared with the game's own debug console. Pass `GUIStyle.none` for
both scrollbars instead and draw your own; wheel scrolling still works, because `GUI.EndScrollView`
handles it regardless of the scrollbar styles.

**A floating dropdown has to be split in two.** Immediate mode does input and drawing in one
pass, so a menu drawn last is on top but has already let every click through to the content
underneath it. Handle the menu's clicks *before* the content is laid out, and paint it *after*:

```csharp
DrawToolbar(bar);                       // the button that opens it
if (PickerOpen) HandlePicker();         // hit-tests rows, Event.current.Use()
…content…
if (PickerOpen) PaintPicker();          // drawn last, so it is on top
```

**Only Latin-1 glyphs are safe.** Which characters a face carries is not knowable from a mod —
IMGUI's built-in font and whatever the game's typeface turns out to be do not have to agree — and
a missing glyph renders as a box. `×` (U+00D7) is fine and is a better close button than an ex.
`▾`, `▸`, `●` are not; generate a mask and draw the arrow.

**The typeface is a probe, not a given.** Every string the game draws goes through TextMeshPro,
which uses a `TMP_FontAsset` — a baked atlas, not a font — and IMGUI needs a `UnityEngine.Font`.
The one bridge between them is `TMP_FontAsset.sourceFontFile`, the TTF the atlas was generated
from, which a build keeps only if the asset was imported with its font data included:

```csharp
foreach (TMP_FontAsset asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
{
    Font source = asset?.sourceFontFile;

    if (source != null && source.dynamic) { … }   // non-dynamic faces render nothing off-atlas
}
```

So write the page to look right in IMGUI's built-in face and treat the game's own as an upgrade
if it is there. Mod Profiler reports what it found through `prof.fonts`.

**Rich text is how one label gets two colours.** A `GUIStyle` carries exactly one, so greying a
unit or turning a value amber when a frame is slow would otherwise need a style per state. Set
`richText = true` and write `"412.4 <color=#7a869a>MiB</color>"`.

## Gotchas

- **Do not spam notifications.** They queue and stack on screen. Rate-limit anything
  driven by a per-tick condition, and prefer one summary notification over one per
  building.
- `Events` is `null` before the HUD exists — during mod construction and at the main
  menu. Null-check every call, as above.
- Hooking `HUDPart.Construct` is a broad hook on a hot-ish path. It runs once per HUD
  part at session start, not per frame, so the cost is fine — but keep the postfix body
  to an assignment.
- Dispose the hook in `IMod.Dispose()`.

> [!NOTE]
> The `HUDPart.Construct` capture is the cleanest route I could verify. If ShapezShifter
> later exposes a HUD accessor, prefer it — this is reaching into the game's DI, and it
> is exactly the kind of thing an update can move.
