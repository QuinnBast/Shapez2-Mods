# Hook shapez 2 behaviour

## Check for an extension point before writing a detour

In order of preference:

1. **Flow** (`ShapezShifter.Flow`) — the builder APIs. One-liners like
   `this.OnTick(dt => …)` cover most needs.
2. **An interceptor + rewirer pair** (Hijack). An interceptor hooks the game's
   construction of something; you implement a rewirer interface to contribute to it.
   Register with `GameRewirers.AddRewirer(...)`, which returns a disposable
   `RewirerHandle`. The full list of interception points is in
   `docs/architecture.md`.
3. **A raw detour**, only when neither covers it.

Prefer 2 over 3 whenever it fits: interceptors are part of ShapezShifter's API surface and
are maintained across game updates. Your detour target is not.

## DetourHelper basics

The target method is expressed as a lambda, so a rename becomes a compile error rather
than a runtime crash.

```csharp
Hook hook = DetourHelper.CreatePostfixHook<MapDrawer, FrameDrawOptionsNoLOD>(
    (drawer, options) => drawer.Draw(options),      // target, as a call expression
    (drawer, options) => Overlay.Draw(options));    // your code, same parameters
```

**The first lambda is never executed.** It is parsed as an expression tree to identify the
method — write it as a normal call on the parameters.

A prefix *returns* the arguments the original will receive; with several, return a tuple:

```csharp
DetourHelper.CreatePrefixHook<SomeType, int, string>(
    (target, count, label) => target.DoThing(count, label),
    (target, count, label) => (count + 1, label.ToUpper()));
```

## Two constructions that compile and then fail at load

Both throw at hook *construction*, which is in the mod constructor — so they take the
game's startup with them unless installation is wrapped.

### A generic declaring type cannot be hooked. At all.

```
System.ArgumentException: Source method is generic, generic hooks are not supported
  at MonoMod.RuntimeDetour.Hook.CheckSupported()
```

Instantiating over a value type makes no difference. `Foo<ShapeId>.Bar()` is rejected
exactly like `Foo<T>.Bar()`, even though a struct instantiation has its own native code and
is intuitively "a concrete method" — `CheckSupported` looks at the declaring type, not the
instantiation. And it compiles perfectly, because
`typeof(Foo<ShapeId>).GetMethod("Bar")` is a valid `MethodBase`.

**The fix is to find a non-generic choke point the calls already pass through.** Game code
that is generic at one layer is usually reached through something that is not — a lane, a
dispatcher, a factory. An item entering a train station passes `DummyLane.CanAcceptItem`
(non-generic) on its way to the generic `TrainBeltToCargoFillingContainer<T>` behind it,
so a guard belongs on the lane.

Two costs to accept when relocating: the choke point is usually hotter than the method you
wanted, so order type tests so the common case falls through cheaply; and it is broader, so
the hook must check it is looking at the case it cares about rather than assuming.

### `CreatePrefixHook` only works on void-returning methods

```
ArgumentException: Target method is not compatible with source method
```

Every `CreatePrefixHook` overload builds an `Action` internally. The trap is that C# will
happily convert a lambda whose body *calls* a non-void method into an `Action`, discarding
the return value without a word:

```csharp
// Compiles. Throws at construction: BakeMetadataIntoRuntime returns GameIslands.
DetourHelper.CreatePrefixHook<IslandDefinitionFactory, IIslandCatalogPair, AuthoringIslands>(
    (factory, pair, meta) => factory.BakeMetadataIntoRuntime(pair, meta), ...);
```

**Use a raw `Hook` with hand-written delegate types**, which is also how you reach a return
value or an `out` parameter:

```csharp
private delegate GameIslands BakeOrig(
    IslandDefinitionFactory factory, IIslandCatalogPair pair, AuthoringIslands meta);

new Hook(
    typeof(IslandDefinitionFactory).GetMethod(nameof(IslandDefinitionFactory.BakeMetadataIntoRuntime)),
    new Func<BakeOrig, IslandDefinitionFactory, IIslandCatalogPair, AuthoringIslands, GameIslands>(
        (orig, factory, pair, meta) =>
        {
            // ... before ...
            return orig(factory, pair, meta);
        }));
```

The first parameter is a delegate matching the original, with `this` as its first argument.
Calling it runs the method you hooked; *not* calling it replaces the method.

## Wrap installation — then count what installed

Wrapping each hook so one failure cannot cost the others is right. But it turns both
failures above into a line in `Player.log` and a feature that silently never runs.

So make the count visible:

```csharp
Logger.Info?.Log($"MyMod: sweep installed on {Hooks.Count} of 3 hook points.");
```

Applying several related hooks is also not atomic. If the third throws, the first two are
live and the mod is half-patched — sometimes worse than not patching at all, because the
pieces were designed to work together:

```csharp
try
{
    Add(target1, hook1);
    Add(target2, hook2);
    Add(target3, hook3);   // throws
}
catch
{
    Dispose();             // drop the ones that did apply
    throw;
}
```

## Chain delegates, never replace them

Whenever you take over a delegate the game owns — a lane's `AcceptHook`, a `DrawHooks`
delegate, an event — save the previous value and call it:

```csharp
AcceptHookDelegate saved = lane.AcceptHook;
lane.AcceptHook = delegate(IItemReceiver receiver, ref IBeltItem item, ref Ticks remaining_T)
{
    Observe(item);
    saved?.Invoke(receiver, ref item, ref remaining_T);
};
```

Replacing outright is the most common way to break a machine while your own code looks
correct — the cutter that no longer cuts, the belt that no longer moves.

## Choosing a target

- **Prefer meaningful signatures.** `MapDrawer.Draw(FrameDrawOptionsNoLOD)` hands you
  everything; a private helper taking three primitives tells you nothing and moves between
  versions.
- **Prefer once-per-frame or once-per-session over once-per-item.** A hook on a hot path
  multiplies its cost by the size of the player's factory.
- **Hook the widest point that works.** One hook on the draw entry point beats twenty on
  individual drawers.
- **Avoid compiler-generated members** — lambdas, iterator state machines, local functions
  (`<>c__DisplayClass…`). Their names are not stable across builds.

To find the right target, `grep -rn` the decompiled assemblies for a method name and read
every vanilla caller. That shows intended usage far faster than reading signatures.

## Session-scoped hooks must be removed per session

Anything installed while a save is open — lane hooks, event registrations — must be undone
when the map goes away, or it leaks into the next save and holds a reference to a map that
no longer exists. Compare against the last map you saw rather than using a `bool
initialized` flag, which silently keeps stale state when the player loads a second save.
See `docs/howto/run-code-when-game-loads.md`.

Global hooks come off in `IMod.Dispose()`:

```csharp
public void Dispose()
{
    DrawHook?.Dispose();
    TickHandle?.Dispose();
}
```

## Staying compatible

The shipped assemblies carry no compatibility promise.

- **Keep hooks few and central.** A mod with two hooks survives an update that a mod with
  twenty will not.
- **Isolate them** in one file, so a version bump is one file to review.
- **Never assume a hook installed.** Log the failure and let the rest of the mod carry on
  degraded rather than dying at load.
- **Watch for `[Obsolete]`.** The game marks types it intends to replace — `DrawHooks`,
  `IGameSessionManagers`, `IslandModel.LayoutQuery`, `GameHelper.Core`. They work today and
  are exactly what will change tomorrow. Keep each to one place.
- **Declare `GameVersionSupportRange`** in `manifest.json` rather than letting the game load
  the mod into a version you have never tested.

## Deeper reading

Long-form pages in the community docs repo (`shapez2-modding-docs`):


`docs/hooking.md` ·
`docs/architecture.md` ·
`docs/publicizer.md` ·
`docs/howto/run-code-when-game-loads.md` ·
`docs/rendering.md`
