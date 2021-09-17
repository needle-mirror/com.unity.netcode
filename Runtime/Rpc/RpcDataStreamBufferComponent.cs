using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// One per NetworkConnection. Stores queued, outgoing RPC data.
    /// Thus, buffer size is related to client-authored RPC count * size.
    /// InternalBufferCapacity is zero as RPC's can vary in size, and we don't want to constantly
    /// move the RPC data into and out of the chunk.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct OutgoingRpcDataStreamBufferComponent : IBufferElementData
    {
        public byte Value;
    }

    /// <summary>
    /// One per NetworkConnection. Stores queued, incoming RPC data.
    /// Thus, buffer size is related to inbound-from-server RPC count * size.
    /// InternalBufferCapacity is zero as RPC's can vary in size, and we don't want to constantly
    /// move the RPC data into and out of the chunk.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct IncomingRpcDataStreamBufferComponent : IBufferElementData
    {
        public byte Value;
    }
}
