using System;
using ShapezShifter.Hijack;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Keeps a hand-registered rewirer armed for every scenario load, and can be switched off.
    ///
    /// ShapezShifter's chain links unregister themselves the moment they have applied, and
    /// `AtomicIslandExtender` copes by rebuilding its whole chain afterwards. Anything registered
    /// outside that chain has to re-arm the same way or it silently stops working on the second
    /// scenario load.
    ///
    /// `RewirerChain.BeginRewiringWith` does the arming, but keeps the `RewirerHandle` to itself,
    /// so a mod that used it could never unregister again - the loop would outlive the mod and
    /// keep re-arming against a disposed instance. That matters for anyone running a hot-reloader,
    /// and for any loader that disposes mods at all. So this owns the handle instead.
    ///
    /// Release happens exactly once per registration: `GameRewirers.RemoveRewirer` logs an error
    /// for a handle it has already dropped, so a double release would be noise in every user's
    /// log.
    internal sealed class ReArmingRewirer<TPropagated> : IDisposable
    {
        private readonly Func<IChainableRewirer<TPropagated>> CreateRewirer;

        private IChainableRewirer<TPropagated> Armed;
        private RewirerHandle Handle;
        private bool Stopped;

        public ReArmingRewirer(Func<IChainableRewirer<TPropagated>> createRewirer)
        {
            CreateRewirer = createRewirer;
            Arm();
        }

        private void Arm()
        {
            Armed = CreateRewirer();
            Handle = GameRewirers.AddRewirer(Armed);
            Armed.AfterHijack.Register(OnApplied);
        }

        private void OnApplied(TPropagated propagated)
        {
            Release();

            if (!Stopped)
            {
                Arm();
            }
        }

        private void Release()
        {
            if (Armed == null)
            {
                return;
            }

            Armed.AfterHijack.Unregister(OnApplied);
            GameRewirers.RemoveRewirer(Handle);
            Armed = null;
        }

        public void Dispose()
        {
            Stopped = true;
            Release();
        }
    }

    /// The same, for rewirers that propagate nothing.
    internal sealed class ReArmingRewirer : IDisposable
    {
        private readonly Func<IChainableRewirer> CreateRewirer;

        private IChainableRewirer Armed;
        private RewirerHandle Handle;
        private bool Stopped;

        public ReArmingRewirer(Func<IChainableRewirer> createRewirer)
        {
            CreateRewirer = createRewirer;
            Arm();
        }

        private void Arm()
        {
            Armed = CreateRewirer();
            Handle = GameRewirers.AddRewirer(Armed);
            Armed.AfterHijack.Register(OnApplied);
        }

        private void OnApplied()
        {
            Release();

            if (!Stopped)
            {
                Arm();
            }
        }

        private void Release()
        {
            if (Armed == null)
            {
                return;
            }

            Armed.AfterHijack.Unregister(OnApplied);
            GameRewirers.RemoveRewirer(Handle);
            Armed = null;
        }

        public void Dispose()
        {
            Stopped = true;
            Release();
        }
    }
}
