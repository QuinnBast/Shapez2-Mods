using System;
using Game.Content.Features.SpacePaths.IslandIO;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// The blackbox's notch connectors: the vanilla ones, made to say yes to anything.
///
/// A blackbox declares four connectors on every notch - belt and pipe, in and out - so the player
/// can wire anything to any side. That is fine while the game is running, because placement asks
/// for connectors *by type* and finds all four. It falls apart when a save is read back, because
/// connection resolution asks a different question:
///
///     Definition.CustomData.Get&lt;IIslandConnectorData&gt;()
///         .TryGetConnector&lt;IIslandConnector&gt;(localChunkPivot, out var connection)
///
/// The base interface, not a type. And <c>IslandConnectorData</c> answers with the **first**
/// connector declared at that pivot - so every notch resolved to the belt input, and the vanilla
/// belt input only agrees to connect to a belt output. Belts feeding in restored; belts taking
/// out, and pipes in either direction, silently became conflicting connections. A box came back
/// from a save with its shape inputs working and nothing else, which deadlocked it.
///
/// What makes this fixable in four small classes rather than a redesign is that compatibility is
/// checked from **both** ends:
///
///     if (!IO.Connector.IsCompatibleConnection(other.IO.Connector)
///         &amp;&amp; !other.IO.Connector.IsCompatibleConnection(IO.Connector)) return false;
///
/// Either side saying yes is enough. So the connector that happens to be first only has to be
/// permissive, and the ambiguity stops mattering: whichever one the lookup picks, it agrees, and
/// the connection stands. The far side still decides what it is - a belt is a belt - and the
/// simulation still routes items through the properly typed chunk connectors, which exist for all
/// four kinds at every notch.
///
/// The methods being overridden are not virtual, which is why each of these re-declares the
/// interfaces. Re-implementing an interface in a derived class rebinds it for interface dispatch,
/// and every call site here goes through <c>IIslandConnector</c> or <c>IEntityConnector</c> - the
/// connector is only ever held as <c>EntityIO&lt;GlobalChunkPivot, IIslandConnector&gt;</c>. The
/// concrete type is untouched, so <c>is SpaceBeltInputConnector</c> still holds and the game's own
/// dispatch to shape or fluid handling is unaffected.
/// </summary>
public static class BlackboxConnectors
{
    /// <summary>
    /// Whether a connector is one a blackbox notch will accept.
    ///
    /// Any space path connector, in either direction. Not literally anything: a notch should still
    /// refuse something that has no business there, and saying yes to everything would let the
    /// game offer connections it could never carry.
    /// </summary>
    private static bool Accepts(object other)
    {
        return other is ISpacePathInputConnector || other is ISpacePathOutputConnector;
    }

    public class BeltInput : SpaceBeltInputConnector, IIslandConnector, IEntityConnector
    {
        public new bool IsCompatibleConnection(IIslandConnector other)
        {
            return Accepts(other);
        }

        public new bool IsCompatibleConnector<TConnector>(TConnector other)
            where TConnector : IEntityConnector
        {
            return Accepts(other);
        }
    }

    public class BeltOutput : SpaceBeltOutputConnector, IIslandConnector, IEntityConnector
    {
        public new bool IsCompatibleConnection(IIslandConnector other)
        {
            return Accepts(other);
        }

        public new bool IsCompatibleConnector<TConnector>(TConnector other)
            where TConnector : IEntityConnector
        {
            return Accepts(other);
        }
    }

    public class PipeInput : SpacePipeInputConnector, IIslandConnector, IEntityConnector
    {
        public new bool IsCompatibleConnection(IIslandConnector other)
        {
            return Accepts(other);
        }

        public new bool IsCompatibleConnector<TConnector>(TConnector other)
            where TConnector : IEntityConnector
        {
            return Accepts(other);
        }
    }

    public class PipeOutput : SpacePipeOutputConnector, IIslandConnector, IEntityConnector
    {
        public new bool IsCompatibleConnection(IIslandConnector other)
        {
            return Accepts(other);
        }

        public new bool IsCompatibleConnector<TConnector>(TConnector other)
            where TConnector : IEntityConnector
        {
            return Accepts(other);
        }
    }
}
