using System;
using System.Collections.Generic;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Stops a second module registration for an island killing the session.
    ///
    /// `IslandsModulesLookup.AddModuleProvider` is a `Dictionary.Add`, so registering one island
    /// twice throws `An item with the same key has already been added`. That comes out of
    /// `GameSessionOrchestrator.Init_6_PlayerInteraction`, which runs for **every** session
    /// build including the main menu's own - so the symptom is that returning to the menu kills
    /// the game, with the save left untouched and no way back.
    ///
    /// **Why a second registration happens at all.** ShapezShifter's `AtomicIslandExtender.Build`
    /// re-arms its whole chain once every branch has propagated, and the bookkeeping is
    /// `WaitAllRewirers`: a `HashSet` of links, emptied one at a time, and never refilled.
    /// `InvokeCallbackIfAllExtendersHaveBeenApplied` fires whenever the set is empty - so once it
    /// has emptied, any later propagation fires the re-arm again and a second chain is armed
    /// alongside the first. Both then register the same islands into the next session's lookup.
    ///
    /// This is a **guard, not a cure**. The duplicate is still a bug and still worth chasing; it
    /// is simply not worth losing a session over, and the island it names is the thread to pull
    /// on. Keeping it here rather than in the Mod Reloader matters too: the reloader is a
    /// development tool that a player will not have installed, and this crash reaches players.
    internal sealed class CargoModuleGuard : IDisposable
    {
        private readonly Hook Guard;

        private readonly HashSet<string> Reported = new();

        private readonly ILogger Log;

        /// A full hook, not a prefix. A prefix can only rewrite the arguments, and the original
        /// still runs - `AddModuleProvider` is `Dictionary.Add`, which throws on the duplicate
        /// key whatever value is handed to it. The call has to be skipped outright.
        private delegate void AddOrig(
            IslandsModulesLookup self, IslandDefinitionId definition,
            IIslandModuleDataProvider provider);

        private delegate void AddHook(
            AddOrig orig, IslandsModulesLookup self, IslandDefinitionId definition,
            IIslandModuleDataProvider provider);

        public CargoModuleGuard(ILogger logger)
        {
            Log = logger;

            try
            {
                Guard = new Hook(
                    typeof(IslandsModulesLookup).GetMethod(
                        nameof(IslandsModulesLookup.AddModuleProvider)),
                    new AddHook(Add));
            }
            catch (Exception exception)
            {
                // Never take mod loading down for a guard. A throw in a mod constructor is not
                // contained by ModLoader - it comes out through ModLoadingStep.LoadMods and kills
                // the game's whole mod loading step.
                Log.Exception?.LogException(exception);
            }
        }

        public void Dispose()
        {
            Guard?.Dispose();
        }

        private void Add(
            AddOrig orig, IslandsModulesLookup self, IslandDefinitionId definition,
            IIslandModuleDataProvider provider)
        {
            if (self.IslandModulesMap.ContainsKey(definition))
            {
                Report(definition.Name);
                return;
            }

            orig(self, definition, provider);
        }

        private void Report(string island)
        {
            if (!Reported.Add(island))
            {
                return;
            }

            Log.Warning?.Log(
                $"'{island}' was registered for island modules twice. The duplicate is dropped so "
                + "the session survives, but something is registering this island more than once.");
        }
    }
}
