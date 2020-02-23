using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    public struct RpcQueue<T>
        where T : struct, IRpcCommand
    {
        internal ulong rpcType;
        [ReadOnly] internal NativeHashMap<ulong, int> rpcTypeHashToIndex;

        public unsafe void Schedule(DynamicBuffer<OutgoingRpcDataStreamBufferComponent> buffer, T data)
        {
            DataStreamWriter writer = new DataStreamWriter(UnsafeUtility.SizeOf<T>() + 2 + 1, Allocator.Temp);
            if (buffer.Length == 0)
                writer.WriteByte((byte) NetworkStreamProtocol.Rpc);
            if (!rpcTypeHashToIndex.TryGetValue(rpcType, out var rpcIndex))
                throw new InvalidOperationException("Could not find RPC index for type");
            writer.WriteUShort((ushort)rpcIndex);
            data.Serialize(ref writer);
            var prevLen = buffer.Length;
            buffer.ResizeUninitialized(buffer.Length + writer.Length);
            byte* ptr = (byte*) buffer.GetUnsafePtr();
            ptr += prevLen;
            UnsafeUtility.MemCpy(ptr, writer.AsNativeArray().GetUnsafeReadOnlyPtr(), writer.Length);
        }
    }
}
