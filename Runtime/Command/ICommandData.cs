using Unity.Entities;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    public interface ICommandData<T> : IBufferElementData where T : struct, ICommandData<T>
    {
        uint Tick { get; }
        void Serialize(DataStreamWriter writer);
        void Deserialize(uint tick, DataStreamReader reader, ref DataStreamReader.Context ctx);
        void Serialize(DataStreamWriter writer, T baseline, NetworkCompressionModel compressionModel);

        void Deserialize(uint tick, DataStreamReader reader, ref DataStreamReader.Context ctx, T baseline,
            NetworkCompressionModel compressionModel);
    }

    public static class CommandDataUtility
    {
        public const int k_CommandDataMaxSize = 64;

        public static bool GetDataAtTick<T>(this DynamicBuffer<T> commandArray, uint targetTick, out T commandData)
            where T : struct, ICommandData<T>
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
            where T : struct, ICommandData<T>
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
