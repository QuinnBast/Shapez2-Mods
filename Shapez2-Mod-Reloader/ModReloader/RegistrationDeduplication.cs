using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core.Content.Islands;
using MonoMod.RuntimeDetour;
using ShapezShifter.Hijack;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Drops registrations that would be applied twice, immediately before they are applied.
///
/// **The leak.** A mod registers its content from its constructor, as rewirers on Shifter's
/// global list. Some of those are consumed by a session build and removed; others are not.
/// Over one session of reloading, `GameScenarioIslandExtender` balanced exactly - 94 added,
/// 94 removed - while `IslandsExtender` went 94 added against 61 removed. The residue builds
/// up a generation at a time.
///
/// Every one of these extenders ends in a `Dictionary.Add` keyed by a definition id, so the
/// moment two of them carry the same id the next session build throws
/// `"An item with the same key has already been added"` and the savegame load dies. Three
/// separate places have now been seen doing it:
/// `gameIslands.DefinitionsById.Add` (island definitions),
/// `IslandsModulesLookup.AddModuleProvider` (island side panels), and the building equivalent.
///
/// **Why identity is found by reflection rather than by type.** Chasing one extender class at
/// a time is how this was first attempted, and it took two crashes to get past two of them.
/// The shape is general: each extender in `ShapezShifter.Flow.Atomic` holds exactly one thing
/// identifying what it registers - an id struct directly, or a definition or builder holding
/// one. So this reads whatever it can find and keys on that. A type it cannot make sense of
/// yields no identity and is left alone, which is also what a future Shifter change degrades
/// to.
///
/// Two rewirers of the same concrete type carrying the same id are duplicates by definition:
/// the game's own lookups are `Dictionary.Add`, so it has never been legal to register the
/// same island's modules twice.
/// </summary>
public class RegistrationDeduplication : IDisposable
{
    /// <summary>
    /// Only these are candidates. Shifter's atomic extenders are the ones that register
    /// content into the game's keyed lookups; a tick handler or a console command rewirer
    /// registers nothing that can collide, and must not be touched.
    /// </summary>
    private const string Namespace = "ShapezShifter.Flow.Atomic";

    /// <summary>
    /// The shape of <c>BakeMetadataIntoRuntime</c>, with <c>this</c> as the first parameter -
    /// which is how MonoMod passes the original to a hook.
    /// </summary>
    private delegate GameIslands BakeOrig(IslandDefinitionFactory factory, IIslandCatalogPair pair, AuthoringIslands meta);

    private readonly ILogger Logger;
    private readonly List<Hook> Hooks = new List<Hook>();

    /// Islands already reported as duplicated, so a repeat does not fill the log.
    private static readonly HashSet<string> Skipped = new HashSet<string>();

    public RegistrationDeduplication(ILogger logger)
    {
        Logger = logger;

        // Three choke points, because the registrations are applied at three different times
        // and a single sweep at the first would miss whatever is added after it. Each is the
        // method Shifter's own interceptor postfixes, so this runs immediately before the
        // extenders it is cleaning up after.
        //
        // This first one is a raw Hook with a hand-written delegate type rather than one of
        // DetourHelper's helpers, and that is not a style choice. Every CreatePrefixHook
        // overload builds an Action, so the method it hooks must return void -
        // BakeMetadataIntoRuntime returns GameIslands. The mismatch compiles, because C#
        // will convert a call to a non-void method into an Action lambda without complaint,
        // and then fails at construction with "Target method is not compatible with source
        // method" - logged, caught, and easy to miss, leaving the whole dedupe silently
        // inert. It was inert for two crashes before the log showed why.
        Install(() =>
        {
            MethodInfo bake = typeof(IslandDefinitionFactory).GetMethod(
                nameof(IslandDefinitionFactory.BakeMetadataIntoRuntime),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (bake == null)
            {
                throw new MissingMethodException("IslandDefinitionFactory.BakeMetadataIntoRuntime");
            }

            return new Hook(bake, new Func<BakeOrig, IslandDefinitionFactory, IIslandCatalogPair, AuthoringIslands, GameIslands>(
                (orig, factory, pair, meta) =>
                {
                    Deduplicate("island definitions");
                    return orig(factory, pair, meta);
                }));
        });

        Install(() => DetourHelper.CreatePrefixHook<GameSessionOrchestrator, IslandsModulesLookup>(
            (orchestrator, lookup) => orchestrator.InjectIslandsModuleProviders(lookup),
            (orchestrator, lookup) =>
            {
                Deduplicate("island modules");
                return lookup;
            }));

        Install(() => DetourHelper.CreatePrefixHook<GameSessionOrchestrator, BuildingsModulesLookup>(
            (orchestrator, lookup) => orchestrator.InjectBuildingsModuleProviders(lookup),
            (orchestrator, lookup) =>
            {
                Deduplicate("building modules");
                return lookup;
            }));

        // A backstop, because the sweep above can only drop what it can *identify*. It reads
        // an id out of each extender by reflection and leaves alone anything it cannot make
        // sense of, so a Shifter extender shaped differently from the ones it knows slips
        // through and the session build dies on `Dictionary.Add`.
        //
        // This makes that survivable rather than fatal: a second provider for an island that
        // already has one is dropped, with the island named once. Dropping the later one is
        // right for the same reason the sweep keeps the newest rewirer is right - here the
        // first to arrive is the one already wired into the lookup, and replacing it would
        // leave the earlier reference dangling.
        //
        // A duplicate is still a bug worth chasing; it is just not worth losing the session
        // over, and "cannot return to the main menu" is a particularly bad way to find out.
        Install(() => DetourHelper.CreatePrefixHook<IslandsModulesLookup, IslandDefinitionId, IIslandModuleDataProvider>(
            (lookup, definition, provider) => lookup.AddModuleProvider(definition, provider),
            (lookup, definition, provider) =>
            {
                if (lookup.IslandModulesMap.ContainsKey(definition))
                {
                    if (Skipped.Add(definition.Name))
                    {
                        Logger.Warning?.Log(
                            "Mod Reloader: '" + definition.Name + "' already has an island module "
                            + "provider; dropping a duplicate registration that would have thrown.");
                    }

                    return (definition, null);
                }

                return (definition, provider);
            }));

        // Reported because the failure mode this had was silence: the hook threw at
        // construction, the exception went to the log, and everything carried on as if the
        // dedupe were running. Three is the number that means it is.
        Logger.Info?.Log("Mod Reloader: duplicate-registration sweep installed on " + Hooks.Count + " of 4 hook points.");
    }

    public void Dispose()
    {
        foreach (Hook hook in Hooks)
        {
            hook?.Dispose();
        }

        Hooks.Clear();
    }

    /// One hook failing to install must not cost the other two.
    private void Install(Func<Hook> create)
    {
        try
        {
            Hooks.Add(create());
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    /// <summary>
    /// Keeps the newest registration of each kind and drops the rest.
    ///
    /// Newest wins because it belongs to the most recently loaded assembly - reloading a mod
    /// is a statement that its new code is the one that should run. Handles come from an
    /// incrementing counter, so the highest is the latest.
    /// </summary>
    private void Deduplicate(string stage)
    {
        try
        {
            Dictionary<string, List<RewirerHandle>> byIdentity = new Dictionary<string, List<RewirerHandle>>();

            foreach (KeyValuePair<RewirerHandle, IRewirer> entry in GameRewirers.RewirersMap)
            {
                string identity = IdentityOf(entry.Value);

                if (identity == null)
                {
                    continue;
                }

                if (!byIdentity.TryGetValue(identity, out List<RewirerHandle> handles))
                {
                    handles = new List<RewirerHandle>();
                    byIdentity[identity] = handles;
                }

                handles.Add(entry.Key);
            }

            List<RewirerHandle> doomed = new List<RewirerHandle>();
            int kinds = 0;

            foreach (KeyValuePair<string, List<RewirerHandle>> pair in byIdentity)
            {
                if (pair.Value.Count < 2)
                {
                    continue;
                }

                kinds++;
                pair.Value.Sort((a, b) => HandleId(a).CompareTo(HandleId(b)));

                for (int i = 0; i < pair.Value.Count - 1; i++)
                {
                    doomed.Add(pair.Value[i]);
                }
            }

            if (doomed.Count == 0)
            {
                return;
            }

            foreach (RewirerHandle handle in doomed)
            {
                try
                {
                    GameRewirers.RemoveRewirer(handle);
                }
                catch (Exception exception)
                {
                    Logger.Exception?.LogException(exception);
                }
            }

            Logger.Info?.Log("Mod Reloader: dropped " + doomed.Count + " stale registration(s) across "
                             + kinds + " definition(s) before " + stage + " were applied.");
        }
        catch (Exception exception)
        {
            // Never take the session build down. A duplicate that slips through causes the
            // crash this exists to prevent; a throw here would cause it unconditionally.
            Logger.Exception?.LogException(exception);
        }
    }

    /// <summary>
    /// What a rewirer is *about* - the ids that would collide, and its concrete type.
    ///
    /// The type is part of the key on purpose: the island definition for CargoStore and the
    /// island *modules* for CargoStore are two different registrations, both wanted, and only
    /// a same-type pair is a duplicate.
    /// </summary>
    private static string IdentityOf(IRewirer rewirer)
    {
        Type type = rewirer?.GetType();

        if (type?.Namespace != Namespace)
        {
            return null;
        }

        List<string> parts = null;

        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            string id = IdFrom(field.GetValue(rewirer));

            if (id == null)
            {
                continue;
            }

            parts = parts ?? new List<string>();
            parts.Add(field.Name + "=" + id);
        }

        return parts == null ? null : type.FullName + "|" + string.Join(",", parts.ToArray());
    }

    /// <summary>
    /// Pulls a definition id out of whatever an extender happens to hold.
    ///
    /// Three shapes cover every extender seen: the id struct itself
    /// (<c>IslandSimulationExtender.DefinitionId</c>), a definition that exposes one
    /// (<c>IslandModulesExtender.IslandDefinition.Id</c>), and a builder holding one
    /// (<c>IslandsExtender.IslandBuilder.DefinitionId</c>). Deliberately one level deep - a
    /// recursive walk would start matching on unrelated inner state.
    /// </summary>
    private static string IdFrom(object value)
    {
        if (value == null)
        {
            return null;
        }

        Type type = value.GetType();

        // The id structs are named IslandDefinitionId, IslandDefinitionGroupId,
        // BuildingDefinitionId and so on. Their ToString is the name that collides.
        if (type.IsValueType && type.Name.EndsWith("Id", StringComparison.Ordinal))
        {
            return value.ToString();
        }

        object nested = Member(value, "Id") ?? Member(value, "DefinitionId");

        if (nested == null)
        {
            return null;
        }

        Type nestedType = nested.GetType();

        return nestedType.IsValueType && nestedType.Name.EndsWith("Id", StringComparison.Ordinal)
            ? nested.ToString()
            : null;
    }

    private static object Member(object target, string name)
    {
        Type type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        FieldInfo field = type.GetField(name, flags);

        if (field != null)
        {
            return field.GetValue(target);
        }

        PropertyInfo property = type.GetProperty(name, flags);

        return property != null && property.CanRead ? property.GetValue(target) : null;
    }

    private static int HandleId(RewirerHandle handle)
    {
        FieldInfo info = typeof(RewirerHandle).GetField(
            "Id", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        return info == null ? 0 : (int)info.GetValue(handle);
    }
}
