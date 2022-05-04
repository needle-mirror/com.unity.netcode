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
        [ReadOnly] internal NativeParallelHashMap<ulong, int> rpcTypeHashToIndex;
        internal bool dynamicAssemblyList;

        public unsafe void Schedule(DynamicBuffer<OutgoingRpcDataStreamBufferComponent> buffer,
            ComponentDataFromEntity<GhostComponent> ghostFromEntity, TActionRequest data)
        {
            var serializer = default(TActionSerializer);
            var serializerState = new RpcSerializerState {GhostFromEntity = ghostFromEntity};
            var msgHeaderLen = dynamicAssemblyList ? 10 : 4;
            int maxSize = UnsafeUtility.SizeOf<TActionRequest>() + msgHeaderLen + 1;
            int rpcIndex = 0;
            if (!dynamicAssemblyList && !rpcTypeHashToIndex.TryGetValue(rpcType, out rpcIndex))
                throw new InvalidOperationException("Could not find RPC index for type");
            while (true)
            {
                DataStreamWriter writer = new DataStreamWriter(maxSize, Allocator.Temp);
                if (dynamicAssemblyList)
                    writer.WriteULong(rpcType);
                else
                    writer.WriteUShort((ushort)rpcIndex);
                var lenWriter = writer;
                writer.WriteUShort((ushort)0);
                serializer.Serialize(ref writer, serializerState, data);
                if (!writer.HasFailedWrites)
                {
                    if (writer.Length > ushort.MaxValue)
                        throw new InvalidOperationException("RPC is too large to serialize");
                    lenWriter.WriteUShort((ushort)(writer.Length - msgHeaderLen));
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
