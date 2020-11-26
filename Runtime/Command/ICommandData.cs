using Unity.Entities;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    /// <summary>
    /// Commands (usually inputs) that must be sent from client to server to control an entity (or any other thing)
    /// should implement the ICommandData interface. Prefer using the ICommandData over Rpc if you need to send a constant
    /// stream of data from client to server
    ///
    /// ICommandData, being a subclass of IBufferElementData, can also be serialized from server to the clients and natively support
    /// the presence of <see cref="GhostComponentAttribute"/> and <see cref="GhostFieldAttribute"/>.
    /// As such, the same rule for buffers apply: if the command buffer must be serialized, then all fields must be annotated
    /// with a <see cref="GhostFieldAttribute"/>. Failure to do so will generate code-generation errors.
    ///
    /// However, differently from normal component, ICommandData buffers are not serialized to all the clients by default.
    /// In particular, in absence of a GhostComponentAttribute governing the serialization behavior the following set of options
    /// are set:
    /// <see cref="GhostComponentAttribute.PrefabType"/> is set to <see cref="GhostPrefabType.All"/>. The buffer is present on all the
    /// ghost variant.
    /// <see cref="GhostComponentAttribute.OwnerPredictedSendType"/> is set to <see cref="GhostSendType.Predicted"/>. Only predicted ghost
    /// can receive the buffer and interpolated variant will have the component stripped or disabled.
    /// <see cref="GhostComponentAttribute.OwnerSendType"/> is set to <see cref="SendToOwnerType.SendToNonOwner"/>. If the ghost
    /// has an owner, is sent only to the clients who don't own the ghost.
    ///
    /// Is generally not recommended to send back to the ghost owner its own commands. For that reason, setting the
    /// <see cref="SendToOwnerType.SendToOwner"/> flag will be reported as a error and ignored.
    /// Also, because they way ICommandData works, some care must be used when setting the <see cref="GhostComponentAttribute.PrefabType"/>
    /// property:
    ///
    /// - Server: While possible, does not make much sense. A warning will be reported.
    /// - Clients: The ICommandData buffer is stripped from the server ghost. A warning will be reported
    /// - InterpolatedClient: ICommandData buffers are stripped from the server and predicted ghost. A warning will be reported
    /// - Predicted: ICommandData buffers are stripped from the server and predicted ghost. A warning will be reported.
    /// - <b>AllPredicted: Interpolated ghost will not have the command buffer.</b>
    /// - <b>All: All ghost will have the command buffer.</b>
    /// </summary>
    public interface ICommandData : IBufferElementData
    {
        [DontSerializeForCommand]
        uint Tick { get; set; }
    }
    public interface ICommandDataSerializer<T> where T: struct, ICommandData
    {
        void Serialize(ref DataStreamWriter writer, in T data);
        void Deserialize(ref DataStreamReader reader, ref T data);
        void Serialize(ref DataStreamWriter writer, in T data, in T baseline, NetworkCompressionModel compressionModel);

        void Deserialize(ref DataStreamReader reader, ref T data, in T baseline, NetworkCompressionModel compressionModel);
    }

    public static class CommandDataUtility
    {
        public const int k_CommandDataMaxSize = 64;

        public static bool GetDataAtTick<T>(this DynamicBuffer<T> commandArray, uint targetTick, out T commandData)
            where T : struct, ICommandData
        {
            int beforeIdx = 0;
            uint beforeTick = 0;
            for (int i = 0; i < commandArray.Length; ++i)
            {
                uint tick = commandArray[i].Tick;
                if (!SequenceHelpers.IsNewer(tick, targetTick) &&
                    (beforeTick == 0 || SequenceHelpers.IsNewer(tick, beforeTick)))
                {
                    beforeIdx = i;
                    beforeTick = tick;
                }
            }

            if (beforeTick == 0)
            {
                commandData = default(T);
                return false;
            }

            commandData = commandArray[beforeIdx];
            return true;
        }

        public static void AddCommandData<T>(this DynamicBuffer<T> commandArray, T commandData)
            where T : struct, ICommandData
        {
            uint targetTick = commandData.Tick;
            int oldestIdx = 0;
            uint oldestTick = 0;
            for (int i = 0; i < commandArray.Length; ++i)
            {
                uint tick = commandArray[i].Tick;
                if (tick == targetTick)
                {
                    // Already exists, replace it
                    commandArray[i] = commandData;
                    return;
                }

                if (oldestTick == 0 || SequenceHelpers.IsNewer(oldestTick, tick))
                {
                    oldestIdx = i;
                    oldestTick = tick;
                }
            }

            if (commandArray.Length < k_CommandDataMaxSize)
                commandArray.Add(commandData);
            else
                commandArray[oldestIdx] = commandData;
        }
    }
}
