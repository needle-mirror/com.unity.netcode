using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    public struct RpcQueue<TActionSerializer, TActionRequest>
        where TActionRequest : struct, IComponentData
        where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
    {
        internal ulong rpcType;
        [ReadOnly] internal NativeHashMap<ulong, int> rpcTypeHashToIndex;

        public unsafe void Schedule(DynamicBuffer<OutgoingRpcDataStreamBufferComponent> buffer, TActionRequest data)
        {
            var serializer = default(TActionSerializer);
            int maxSize = UnsafeUtility.SizeOf<TActionRequest>() + 4 + 1;
            if (!rpcTypeHashToIndex.TryGetValue(rpcType, out var rpcIndex))
                throw new InvalidOperationException("Could not find RPC index for type");
            while (true)
            {
                DataStreamWriter writer = new DataStreamWriter(maxSize, Allocator.Temp);
                int headerLen = 0;
                if (buffer.Length == 0)
                {
                    headerLen = 1;
                    writer.WriteByte((byte) NetworkStreamProtocol.Rpc);
                }
                writer.WriteUShort((ushort)rpcIndex);
                var lenWriter = writer;
                writer.WriteUShort((ushort)0);
                serializer.Serialize(ref writer, data);
                if (!writer.HasFailedWrites)
                {
                    if (writer.Length-headerLen > ushort.MaxValue)
                        throw new InvalidOperationException("RPC is too large to serialize");
                    lenWriter.WriteUShort((ushort)(writer.Length - 4 - headerLen));
                    var prevLen = buffer.Length;
                    buffer.ResizeUninitialized(buffer.Length + writer.Length);
                    byte* ptr = (byte*) buffer.GetUnsafePtr();
                    ptr += prevLen;
                    UnsafeUtility.MemCpy(ptr, writer.AsNativeArray().GetUnsafeReadOnlyPtr(), writer.Length);
                    break;
                }
                maxSize *= 2;
            }
        }
    }
}
