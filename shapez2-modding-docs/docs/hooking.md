# Hooking the Game

> **You need this when** neither Flow nor an interceptor covers what you want, and you
> have to patch a method directly.


When the game exposes no extension point for what you want, you patch it. ShapezShifter
wraps [MonoMod](https://github.com/MonoMod/MonoMod) in `DetourHelper`, which expresses
the target method as a lambda instead of a reflection string — so a rename becomes a
compile error rather than a runtime crash.

## Postfix — run after

```csharp
using ShapezShifter.SharpDetour;
using MonoMod.RuntimeDetour;

Hook hook = DetourHelper.CreatePostfixHook<MapDrawer, FrameDrawOptionsNoLOD>(
    (drawer, options) => drawer.Draw(options),      // target, as a call expression
    (drawer, options) => Overlay.Draw(options));    // your code, same parameters
```

The first lambda is **never executed** — it is parsed as an expression tree to identify
the method. Write it as a normal call on the parameters.

Overloads exist for zero to seven arguments, for `void` and for value-returning
methods, and for static methods (`CreateStaticPrefixHook`):

```csharp
CreatePostfixHook<TObject>(Expression<Action<TObject>>, Action<TObject>)
CreatePostfixHook<TObject, TArg0>(Expression<Action<TObject, TArg0>>, Action<TObject, TArg0>)
CreatePostfixHook<TObject, TArg0, TArg1>(…)
// …up to TArg6
```

## Prefix — run before, and optionally rewrite the arguments

A prefix **returns** the arguments the original will receive. With one argument you
return the new value; with several you return a tuple:

```csharp
DetourHelper.CreatePrefixHook<SomeType, float>(
    (target, amount) => target.DoThing(amount),
    (target, amount) => amount * 2.0f);          // the original now sees double

DetourHelper.CreatePrefixHook<SomeType, int, string>(
    (target, count, label) => target.DoThing(count, label),
    (target, count, label) => (count + 1, label.ToUpper()));
```

For a `void`-target with no arguments the prefix is a plain `Action<TObject>` — no
return value to rewrite.

## Choosing a target

The method you hook is a contract you are inventing, so pick a stable one:

- **Prefer methods with meaningful signatures.** `MapDrawer.Draw(FrameDrawOptionsNoLOD)`
  hands you everything you need; a private helper with three primitive parameters tells
  you nothing and moves between versions.
- **Prefer things called once per frame or once per session** over things called per
  item. A hook on a hot path multiplies its cost by the size of the player's factory.
- **Hook the widest point that works.** One hook on the draw entry point beats twenty on
  individual drawers.
- **Avoid compiler-generated members** — lambdas, iterator state machines, local
  functions (`<>c__DisplayClass…`). Their names are not stable across builds.

### Generic methods cannot be hooked at all

This one is a hard stop, not a preference. MonoMod refuses any method whose declaring
type is generic:

```
System.ArgumentException: Source method is generic, generic hooks are not supported
  at MonoMod.RuntimeDetour.Hook.CheckSupported()
```

Being instantiated over a value type makes no difference — `Foo<ShapeId>.Bar()` is
rejected exactly like `Foo<T>.Bar()`, even though a struct instantiation has its own
native code and is intuitively "a concrete method". `Hook.CheckSupported` looks at the
declaring type, not the instantiation.

It fails at construction, so a hook set up in a mod constructor throws during mod load
and takes the game's startup with it. It also compiles perfectly — `typeof(Foo<ShapeId>)
.GetMethod("Bar")` is a valid `MethodBase`, so nothing warns you until launch.

**The way round it is to find a non-generic choke point the calls already pass through.**
Game code that is generic at one layer is usually reached through something that is not:
a lane, a dispatcher, a factory. For example, an item entering a train station passes
`DummyLane.CanAcceptItem` — non-generic — on its way to the generic
`TrainBeltToCargoFillingContainer<T>` behind it, so a guard belongs on the lane.

Two costs to accept when you relocate a hook this way: the choke point is usually hotter
than the method you wanted, so order your type tests so the common case falls through
cheaply; and it is broader, so the hook must check it is looking at the case it cares
about rather than assuming.

### `CreatePrefixHook` only works on methods that return void

Every `DetourHelper.CreatePrefixHook` overload builds an `Action` internally:

```csharp
return new Hook(GetRuntimeMethod<TObject>(original),
                new Action<Action<TObject, TArg0>, TObject, TArg0>(Patch));
```

So the method it hooks must return `void`. Point one at a method with a return value and it
fails at hook construction:

```
ArgumentException: Target method is not compatible with source method
```

**It compiles perfectly**, which is the trap. The `original` parameter is an
`Expression<Action<...>>`, and C# will convert a lambda whose body *calls* a non-void method
into an `Action` without a word — the return value is simply discarded:

```csharp
// Compiles. Throws at construction: BakeMetadataIntoRuntime returns GameIslands.
DetourHelper.CreatePrefixHook<IslandDefinitionFactory, IIslandCatalogPair, AuthoringIslands>(
    (factory, pair, meta) => factory.BakeMetadataIntoRuntime(pair, meta), ...);
```

Worse, the throw is easy to swallow. A mod that wraps each hook installation in a `try`/`catch`
so one failure cannot cost the others — which is the [right thing to do](#unwind-a-partial-set-of-hooks)
— turns this into a line in `Player.log` and a feature that silently never runs. Look for the
message above near mod load before assuming a hook is working.

**The way round it is a raw `Hook` with hand-written delegate types**, which is also how you
reach a return value or an `out` parameter:

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

The first parameter is a delegate matching the original, with `this` as its first argument;
calling it is what runs the method you hooked, and skipping the call is what replaces it.

Since a failed hook is invisible, count them and say so:

```csharp
Logger.Info?.Log($"sweep installed on {Hooks.Count} of 3 hook points.");
```

### Unwind a partial set of hooks

Applying several related hooks is not atomic. If the third throws, the first two are live
and the mod is half-patched — sometimes worse than not patching at all, because the pieces
were designed to work together. Wrap the set and dispose what you applied:

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

## Interceptors and rewirers

Before writing a detour, check whether Hijack already covers the structure you want to
extend. The pattern is a pair: an **interceptor** that hooks the game's construction of
something, and a **rewirer** interface you implement to contribute to it.

```csharp
public interface ITickRewirer : IRewirer
{
    void Tick(float deltaTime);
}
```

Register with `GameRewirers.AddRewirer(...)`, which returns a disposable
`RewirerHandle`. The full list of interception points is in
[Architecture](architecture.md#hijack--shapezshifterhijack).

This is worth preferring over a raw detour whenever it fits: interceptors are part of
ShapezShifter's API surface, so they are maintained across game updates, whereas your
detour target is not.

Ticks have a one-line wrapper, which is all most mods need:

```csharp
using ShapezShifter.Flow;
RewirerHandle handle = this.OnTick(dt => Update(dt));
```

## Chaining, not replacing

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

## Disposing

Every hook is disposable and every rewirer registration hands back a handle. Undo both
in `IMod.Dispose()`:

```csharp
public void Dispose()
{
    DrawHook?.Dispose();
    TickHandle?.Dispose();
}
```

## Staying compatible

The game's internals are not a public API, and the shipped assemblies carry no
compatibility promise. Practical mitigations:

- **Keep hooks few and central.** A mod with two hooks survives an update that a mod
  with twenty will not.
- **Isolate them.** One file that installs every hook, so a version bump is one file to
  review.
- **Never assume a hook installed.** Wrap installation in `try`/`catch`, log the
  failure, and let the rest of the mod carry on degraded rather than dying at load.
- **Watch for `[Obsolete]`.** The game marks types it intends to replace —
  `DrawHooks`, `IGameSessionManagers`, `IslandModel.LayoutQuery`. They work today, and
  they are exactly what will change tomorrow.
- **Declare your range** in `manifest.json` (`GameVersionSupportRange`) rather than
  letting the game load your mod into a version you have never tested.
