using System;
using Game.Content.Features.Fluids;
using Game.Content.Features.SpacePaths;
using Game.Core.Serialization;
using Game.Core.Simulation;
using Game.Core.Trains;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// One partially filled package per layer, saved.
    ///
    /// Both directions and both item types want exactly this and nothing else, so it is one
    /// class. A packager is filling the package, an unpackager is emptying it; either way the
    /// state is a CargoPackage per layer, which is the same shape a train station keeps inside
    /// TrainCargoExchangerState. The container tracks and bridge lanes a station also keeps are
    /// absent here because there is no train to hand anything to.
    ///
    /// TrainCargoFillingContainerState.Sync does visitor.Sync(ref Package), which resolves to the
    /// game's CargoPackageSerializer&lt;TItem&gt;. Nothing new has to be registered.
    ///
    /// Abstract because a saved state has to be a concrete type with its own
    /// SyncableIdentifier - see the subclasses.
    public abstract class CargoLayerState<TItem> : ISimulationState, ISyncable
        where TItem : unmanaged, IEquatable<TItem>
    {
        public readonly TrainCargoFillingContainerState<TItem>[] Layers;

        protected CargoLayerState()
        {
            Layers = new TrainCargoFillingContainerState<TItem>[SpacePathConstants.NumLayers];
            for (int i = 0; i < Layers.Length; i++)
            {
                Layers[i] = new TrainCargoFillingContainerState<TItem>();
            }
        }

        public void Sync(ISerializationVisitor visitor)
        {
            for (int i = 0; i < Layers.Length; i++)
            {
                Layers[i].Sync(visitor);
            }
        }
    }

    // The four saved states. They add nothing to the base and exist as distinct concrete types
    // purely because of how saving works: PolymorphicSerializer reads SyncableIdentifier with
    // `inherit: false` and keys its table by the exact runtime type, so an identifier on the
    // base would be invisible, and one identifier shared between two islands would make a saved
    // packager deserialize as an unpackager.
    //
    // Kept together so the four identifiers can be read at a glance. They are save-format
    // constants: renaming one silently orphans every island of that type in every existing save.

    [SyncableIdentifier("TrainCargoToolsShapePackagerState")]
    public sealed class ShapeCargoPackagerState : CargoLayerState<ShapeId>
    {
    }

    [SyncableIdentifier("TrainCargoToolsFluidPackagerState")]
    public sealed class FluidCargoPackagerState : CargoLayerState<FluidId>
    {
    }

    [SyncableIdentifier("TrainCargoToolsShapeUnpackagerState")]
    public sealed class ShapeCargoUnpackagerState : CargoLayerState<ShapeId>
    {
    }

    [SyncableIdentifier("TrainCargoToolsFluidUnpackagerState")]
    public sealed class FluidCargoUnpackagerState : CargoLayerState<FluidId>
    {
    }
}
