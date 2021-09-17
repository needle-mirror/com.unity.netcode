
/** A connection is represented by an entity having a NetworkStreamConnection.
 * If the entity does not have a NetworkIdComponent it is to be considered connecting.
 * It is possible to add more tags to signal the state of the connection, for example
 * adding an InGame component to signal loading being complete.
 *
 * In addition to these components all connections have a set of incoming and outgoing
 * buffers associated with them.
 */

using Unity.Entities;
using Unity.Networking.Transport;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.NetCode
{
    public struct NetworkStreamConnection : IComponentData
    {
        public NetworkConnection Value;
        public int ProtocolVersionReceived;
    }

    public struct NetworkStreamInGame : IComponentData
    {
    }

    public struct NetworkStreamSnapshotTargetSize : IComponentData
    {
        public int Value;
    }

    public enum NetworkStreamDisconnectReason
    {
        ConnectionClose,
        Timeout,
        MaxConnectionAttempts,
        ClosedByRemote,
        BadProtocolVersion,
        InvalidRpc,
    }

    public struct NetworkStreamDisconnected : IComponentData
    {
        public NetworkStreamDisconnectReason Reason;
    }
    public struct NetworkStreamRequestDisconnect : IComponentData
    {
        public NetworkStreamDisconnectReason Reason;
    }

    /// <summary>
    /// This buffer stores a single incoming command packet. One per NetworkStream (client).
    /// A command packet contains commands for CommandSendSystem.k_InputBufferSendSize (default 4) ticks where 3 of them are delta compressed.
    /// It also contains some timestamps etc for ping calculations.
    /// </summary>
    public struct IncomingCommandDataStreamBufferComponent : IBufferElementData
    {
        public byte Value;
    }
    /// <summary>
    /// This buffer stores a single outgoing command packet without the headers for timestamps and ping.
    /// A command packet contains commands for CommandSendSystem.k_InputBufferSendSize (default 4) ticks where 3 of them are delta compressed.
    /// It also contains some timestamps etc for ping calculations.
    /// </summary>
    public struct OutgoingCommandDataStreamBufferComponent : IBufferElementData
    {
        public byte Value;
    }

    /// <summary>
    /// One per NetworkConnection.
    /// Stores the incoming, yet-to-be-processed snapshot stream data for a connection.
    /// Each snapshot is designed to fit inside <see cref="NetworkParameterConstants.MTU"/>,
    /// so expect this to be MTU or less.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct IncomingSnapshotDataStreamBufferComponent : IBufferElementData
    {
        public byte Value;
    }
    public static class NetCodeBufferComponentExtensions
    {
        public static unsafe DataStreamReader AsDataStreamReader<T>(this DynamicBuffer<T> self)
            where T: struct, IBufferElementData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<T>() != 1)
                throw new System.InvalidOperationException("Can only convert DynamicBuffers of size 1 to DataStreamWriters");
#endif
            var na = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(self.GetUnsafeReadOnlyPtr(), self.Length, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(self.AsNativeArray());
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref na, safety);
#endif
            return new DataStreamReader(na);
        }
        public static unsafe void Add<T>(this DynamicBuffer<T> self, ref DataStreamReader reader)
            where T: struct, IBufferElementData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<T>() != 1)
                throw new System.InvalidOperationException("Can only Add to DynamicBuffers of size 1 from DataStreamReaders");
#endif
            var oldLen = self.Length;
            var length = reader.Length - reader.GetBytesRead();
            self.ResizeUninitialized(oldLen + length);
            reader.ReadBytes((byte*)self.GetUnsafePtr() + oldLen, length);
        }
    }
}
