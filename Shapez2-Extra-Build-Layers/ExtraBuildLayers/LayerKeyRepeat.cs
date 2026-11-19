using System;
using System.Collections.Generic;
using MonoMod.RuntimeDetour;
using System.Reflection;
using Unity.Mathematics;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Holding a layer key keeps changing layer, instead of one layer per press.
///
/// Every reader of the layer keys asks <c>ConsumeWasActivated</c>, which is true only on the frame
/// a key goes down - the key is in <c>ActiveBindings</c> and not in <c>LastBindings</c>. Fine for
/// three layers, miserable for hundreds.
///
/// The keys have more than one reader. Outside a placement it is <c>LayerController</c>; while a
/// path is being dragged it is the drag's own <c>LayerLiftPlacementTracker</c>, which consumes the
/// press first and also extends the dragged path up or down. That tracker is a generic type and
/// cannot be hooked, and stepping the layer behind its back (the first version of this did) moves
/// the camera while the drag stays on the layer it started on.
///
/// So this does not step layers at all. It makes a held key **look freshly pressed** on each
/// repeat tick: a postfix on <c>InputDownstreamContext.Update</c>, which rebuilds the context once
/// a frame before anything reads it, removes the held binding from <c>LastBindings</c>. Whoever
/// owns the key that frame then handles an ordinary press - the drag extends its path, the layer
/// controller changes layer and plays its sound - exactly as if the player had tapped the key.
///
/// The first repeat comes after <see cref="Delay"/> seconds, then slowly, speeding up to one press
/// a frame the longer the key is held.
/// </summary>
internal sealed class LayerKeyRepeat : IDisposable
{
    private static readonly string[] Keys =
    {
        "camera.select-layer-up",
        "camera.select-layer-down",
        "camera.select-other-layer-up",
        "camera.select-other-layer-down",
    };

    /// <summary>Seconds between repeats when repeating starts, and when it has fully sped up.</summary>
    private const float SlowInterval = 0.12f;
    private const float FastInterval = 0f;

    /// <summary>Seconds of repeating it takes to go from the slow interval to the fast one.</summary>
    private const float RampSeconds = 2.5f;

    /// <summary>Seconds of repeating before space layers start moving more than one a frame.</summary>
    private const float BoostAfter = 3f;

    /// <summary>The most extra space layers added per frame, reached one per second after <see cref="BoostAfter"/>.</summary>
    private const int MaxBoost = 9;

    private sealed class Hold
    {
        public float Started;
        public short StartIslandLayer;
        public float Due;
        public int LastFrame = -1;
    }

    private delegate void UpdateMethod(
        InputDownstreamContext self, IEnumerable<(Keybinding, KeySet)> last, IEnumerable<(Keybinding, KeySet)> active,
        float2 position, float2 delta, float wheel, GameObject hover);

    private delegate void UpdateHook(
        UpdateMethod orig, InputDownstreamContext self, IEnumerable<(Keybinding, KeySet)> last,
        IEnumerable<(Keybinding, KeySet)> active, float2 position, float2 delta, float wheel, GameObject hover);

    private readonly float Delay;
    private readonly ILogger Logger;
    private readonly Hook Hook;
    private readonly Dictionary<string, Hold> Holds = new Dictionary<string, Hold>();
    private bool Complained;

    /// <summary>Layers per overview step; 0 picks one from the unlocked range.</summary>
    private readonly int OverviewStep;

    public LayerKeyRepeat(float delay, int overviewStep, ILogger logger)
    {
        Delay = Mathf.Max(0.05f, delay);
        OverviewStep = Math.Max(0, overviewStep);
        Logger = logger;

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            MethodInfo target = typeof(InputDownstreamContext).GetMethod(
                nameof(InputDownstreamContext.Update), BindingFlags.Instance | BindingFlags.Public);
            if (target == null)
            {
                logger.Error?.Log("Extra Build Layers: InputDownstreamContext.Update not found; holding a layer key will not repeat.");
                return;
            }

            Hook = new Hook(target, new UpdateHook(Detour));
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            logger.Error?.Log("Extra Build Layers: could not hook input; holding a layer key will not repeat.");
        }
    }

    private void Detour(
        UpdateMethod orig, InputDownstreamContext context, IEnumerable<(Keybinding, KeySet)> last,
        IEnumerable<(Keybinding, KeySet)> active, float2 position, float2 delta, float wheel, GameObject hover)
    {
        orig(context, last, active, position, delta, wheel, hover);

        try
        {
            float now = Time.unscaledTime;
            int frame = Time.frameCount;

            if (InOverview(out IGameSessionManagers session))
            {
                Overview(context, session, now, frame);
                return;
            }

            foreach (string id in Keys)
            {
                if (!context.AllBindings.TryGetValue(id, out Keybinding binding))
                {
                    continue;
                }

                if (!context.ActiveBindings.ContainsKey(binding))
                {
                    Holds.Remove(id);
                    continue;
                }

                if (!Holds.TryGetValue(id, out Hold hold))
                {
                    Holds[id] = new Hold { Started = now, Due = now + Delay, StartIslandLayer = CurrentIslandLayer() };
                    continue;
                }

                // Once a frame at most, even if the context is updated twice in one.
                if (now < hold.Due || hold.LastFrame == frame)
                {
                    continue;
                }

                Boost(id, hold, now - hold.Started - Delay);

                if (context.LastBindings.Remove(binding))
                {
                    hold.LastFrame = frame;
                    float repeating = now - hold.Started - Delay;
                    hold.Due = now + Mathf.Lerp(SlowInterval, FastInterval, Mathf.Clamp01(repeating / RampSeconds));
                }
            }
        }
        catch (Exception exception)
        {
            if (!Complained)
            {
                Complained = true;
                Logger.Exception?.LogException(exception);
            }
        }
    }

    /// <summary>
    /// Past one press a frame, which is where the fake-press approach tops out (about 60 layers a
    /// second, so 15 to 30 seconds to climb 999), this moves the viewport's space layer further
    /// on its own: the extra steps grow by one a second, to <see cref="MaxBoost"/> a frame.
    ///
    /// Only when the press last frame really moved the space layer one step the held way - which
    /// says the key is driving space layers, not floors - and never while placing, because a drag
    /// follows one press at a time and would be left behind. Clamped to the layers the player has
    /// unlocked, the same bounds the layer controller enforces.
    /// </summary>
    private void Boost(string key, Hold hold, float repeating)
    {
#pragma warning disable CS0618 // A key repeat has nothing to inject into.
        IGameSessionManagers session = StaticGameCoreAccessor.HasInstance() ? StaticGameCoreAccessor.G : null;
#pragma warning restore CS0618
        Viewport viewport = session?.Viewport;
        if (viewport == null || repeating < BoostAfter || session.LocalPlayer == null ||
            session.LocalPlayer.InteractionState.PlacingAnything)
        {
            return;
        }

        // Whether this key is driving space layers at all: it is if the space layer has moved the
        // held way since the hold began. Floors move a different value, so holding a key that
        // steps floors never boosts space layers. Compared with the start of the hold, not the
        // previous frame - the camera eases and the layer controller applies changes on its own
        // tick, so a one-frame difference is not a reliable signal.
        int direction = key.EndsWith("-up", StringComparison.Ordinal) ? 1 : -1;
        short layer = viewport.IslandLayer;
        if ((layer - hold.StartIslandLayer) * direction <= 0)
        {
            return;
        }

        int extra = Math.Min(MaxBoost, 1 + (int)(repeating - BoostAfter));
        int min = session.InteractionMode.GetMinimumAllowedIslandLayer(session.LocalPlayer);
        int max = session.InteractionMode.GetMaximumAllowedIslandLayer(session.LocalPlayer);
        short target = (short)Math.Max(min, Math.Min(max, layer + direction * extra));
        if (target != layer)
        {
            viewport.IslandLayer = target;
        }
    }

    private static bool InOverview(out IGameSessionManagers session)
    {
#pragma warning disable CS0618 // Input has nothing to inject into.
        session = StaticGameCoreAccessor.HasInstance() ? StaticGameCoreAccessor.G : null;
#pragma warning restore CS0618
        return session?.Viewport != null && session.LocalPlayer != null && session.InteractionMode != null &&
               session.Viewport.TargetZoom >= 1500f && !session.LocalPlayer.InteractionState.PlacingAnything;
    }

    /// <summary>
    /// In the overview the layer keys move by a whole step, not one layer: the player there is
    /// travelling, not working on a layer, and one layer at a time across hundreds is no use. The
    /// press, and every repeat while held, moves the space layer by the step directly, and the key
    /// is then hidden from the rest of the frame - removed from <c>ActiveBindings</c> - so the
    /// layer controller does not add its own single step on top.
    ///
    /// The step is <see cref="OverviewStep"/> if set, otherwise a tenth of the unlocked range:
    /// about 100 at 999 layers, 1 with vanilla's handful.
    /// </summary>
    private void Overview(InputDownstreamContext context, IGameSessionManagers session, float now, int frame)
    {
        int min = session.InteractionMode.GetMinimumAllowedIslandLayer(session.LocalPlayer);
        int max = session.InteractionMode.GetMaximumAllowedIslandLayer(session.LocalPlayer);
        int step = OverviewStep > 0 ? OverviewStep : Math.Max(1, (int)Math.Round((max - min) / 10f));

        foreach (string id in Keys)
        {
            if (!context.AllBindings.TryGetValue(id, out Keybinding binding))
            {
                continue;
            }

            if (!context.ActiveBindings.ContainsKey(binding))
            {
                Holds.Remove(id);
                continue;
            }

            bool move;
            if (!Holds.TryGetValue(id, out Hold hold))
            {
                Holds[id] = hold = new Hold { Started = now, Due = now + Delay, LastFrame = frame };
                move = true;
            }
            else if (now >= hold.Due && hold.LastFrame != frame)
            {
                hold.LastFrame = frame;
                float repeating = now - hold.Started - Delay;
                hold.Due = now + Mathf.Lerp(SlowInterval, 0.05f, Mathf.Clamp01(repeating / RampSeconds));
                move = true;
            }
            else
            {
                move = false;
            }

            if (move)
            {
                int direction = id.EndsWith("-up", StringComparison.Ordinal) ? 1 : -1;
                int layer = session.Viewport.IslandLayer;
                session.Viewport.IslandLayer = (short)Math.Max(min, Math.Min(max, layer + direction * step));
            }

            context.ActiveBindings.Remove(binding);
        }
    }

    private static short CurrentIslandLayer()
    {
#pragma warning disable CS0618
        return StaticGameCoreAccessor.HasInstance() && StaticGameCoreAccessor.G.Viewport != null
            ? StaticGameCoreAccessor.G.Viewport.IslandLayer
            : (short)0;
#pragma warning restore CS0618
    }

    public void Dispose()
    {
        Hook?.Dispose();
    }
}
