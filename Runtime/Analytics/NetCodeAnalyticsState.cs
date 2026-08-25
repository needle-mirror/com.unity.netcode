#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine.Assertions;

namespace Unity.NetCode.Analytics
{
    internal static class NetCodeAnalyticsState
    {
        public static uint GetUpdateLength(World world)
        {
            if (!world.EntityManager.CanBeginExclusiveEntityTransaction())
            {
                return 0;
            }
            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostSendSystemAnalyticsData>());
            if (query.CalculateEntityCount() != 1)
            {
                return 0;
            }

            var sendSystemAnalyticsData = world.EntityManager.GetComponentData<GhostSendSystemAnalyticsData>(query.GetSingletonEntity());
            return ComputeAverageUpdateLengths(sendSystemAnalyticsData);
        }

        static uint ComputeAverageUpdateLengths(GhostSendSystemAnalyticsData sendSystemAnalyticsData)
        {
            var sums = new List<uint>();
            foreach (var update in sendSystemAnalyticsData.UpdateLenSums)
            {
                if (update != 0)
                {
                    sums.Add(update);
                }
            }
            if (sums.Count == 0)
            {
                return 0;
            }

            uint average = 0;
            var updates = new List<uint>();
            foreach (var update in sendSystemAnalyticsData.NumberOfUpdates)
            {
                if (update != 0)
                {
                    updates.Add(update);
                }
            }
            Assert.AreEqual(sums.Count, updates.Count);
            for (var index = 0; index < sums.Count; index++)
            {
                var sum = sums[index];
                average += sum / updates[index];
            }

            return average / (uint)sums.Count;
        }
    }
}
#endif
