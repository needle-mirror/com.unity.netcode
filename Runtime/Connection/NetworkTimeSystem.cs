using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.NetCode
{
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    public class NetworkTimeSystem : ComponentSystem
    {
        public static uint TimestampMS =>
            (uint) (System.Diagnostics.Stopwatch.GetTimestamp() / System.TimeSpan.TicksPerMillisecond);

        internal uint interpolateTargetTick;
        internal uint predictTargetTick;
        internal float subInterpolateTargetTick;
        internal float subPredictTargetTick;

        private EntityQuery connectionGroup;
        private uint latestSnapshot;
        private uint latestSnapshotEstimate;
        private int latestSnapshotAge;
        internal float currentInterpolationFrames;
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private float timeScale;
        private int timeScaleSamples;
        private float averageTimeScale;

        internal float GetAverageTimeScale()
        {
            if (timeScaleSamples > 0)
            {
                averageTimeScale = timeScale / timeScaleSamples;
                timeScale = 0;
                timeScaleSamples = 0;
            }

            return averageTimeScale;
        }
        #endif

        private const int KInterpolationTimeNetTicks = 2;
        private const int KInterpolationTimeMS = 0;
        private const uint KMaxPredictAheadTimeMS = 500;

        private const uint kTargetCommandSlack = 2;
        
        protected override void OnCreate()
        {
            connectionGroup = GetEntityQuery(ComponentType.ReadOnly<NetworkSnapshotAckComponent>());
            latestSnapshotEstimate = 0;
            latestSnapshot = 0;
            latestSnapshotAge = 0;
        }

        protected override void OnUpdate()
        {
            if (connectionGroup.IsEmptyIgnoreFilter)
            {
                interpolateTargetTick = predictTargetTick = 0;
                return;
            }

            var tickRate = default(ClientServerTickRate);
            if (HasSingleton<ClientServerTickRate>())
            {
                tickRate = GetSingleton<ClientServerTickRate>();
            }

            tickRate.ResolveDefaults();

            var connections = connectionGroup.ToComponentDataArray<NetworkSnapshotAckComponent>(Allocator.TempJob);
            var ack = connections[0];
            connections.Dispose();

            float deltaTime = Time.DeltaTime;
            float deltaTicks = deltaTime * tickRate.SimulationTickRate;

            var estimatedRTT = ack.EstimatedRTT;
            if (estimatedRTT > KMaxPredictAheadTimeMS)
                estimatedRTT = KMaxPredictAheadTimeMS;
            // FIXME: adjust by latency
            uint interpolationTimeMS = KInterpolationTimeMS;
            if (interpolationTimeMS == 0)
                interpolationTimeMS = (1000 * KInterpolationTimeNetTicks + (uint) tickRate.NetworkTickRate - 1) /
                                      (uint) tickRate.NetworkTickRate;
            float interpolationFrames = 0.5f + kTargetCommandSlack + (((estimatedRTT + 4*ack.DeviationRTT + interpolationTimeMS) / 1000f) * tickRate.SimulationTickRate);

            // What we expect to have this frame based on what was the most recent received previous frames
            if (latestSnapshotEstimate == 0)
            {
                if (ack.LastReceivedSnapshotByLocal == 0)
                {
                    interpolateTargetTick = predictTargetTick = 0;
                    return;
                }

                latestSnapshot = ack.LastReceivedSnapshotByLocal;
                latestSnapshotEstimate = ack.LastReceivedSnapshotByLocal;
                latestSnapshotAge = 0;

                predictTargetTick = latestSnapshotEstimate + kTargetCommandSlack +
                                  ((uint) estimatedRTT * (uint) tickRate.SimulationTickRate + 999) / 1000;

                currentInterpolationFrames = interpolationFrames;
            }
            else
            {
                // Add number of ticks based on deltaTime
                latestSnapshotEstimate += (uint) deltaTicks;
                latestSnapshotAge -= (int) (math.frac(deltaTicks) * 256.0f);
                if (latestSnapshot != ack.LastReceivedSnapshotByLocal)
                {
                    latestSnapshot = ack.LastReceivedSnapshotByLocal;
                    int snapshotAge = (int) (latestSnapshotEstimate - ack.LastReceivedSnapshotByLocal);
                    latestSnapshotAge = (latestSnapshotAge * 7 + (snapshotAge << 8)) / 8;
                }

                int delta = latestSnapshotAge >> 8;
                if (delta < 0)
                    ++delta;
                if (delta != 0)
                {
                    latestSnapshotEstimate -= (uint) delta;
                    latestSnapshotAge -= delta << 8;
                }
            }

            float commandAge = ack.ServerCommandAge / 256.0f + kTargetCommandSlack;
            float predictionTimeScale = 1f;
            if (math.abs(commandAge) < 10)
            {
                predictionTimeScale = math.clamp(1.0f + 0.1f * commandAge, .9f, 1.1f);
                subPredictTargetTick += deltaTicks * predictionTimeScale;
                uint pdiff = (uint) subPredictTargetTick;
                subPredictTargetTick -= pdiff;
                predictTargetTick += pdiff;
            }
            else
            {
                uint curPredict = latestSnapshotEstimate + kTargetCommandSlack +
                                  ((uint) estimatedRTT * (uint) tickRate.SimulationTickRate + 999) / 1000;
                float predictDelta = (float) (curPredict - predictTargetTick) - deltaTicks;
                if (math.abs(predictDelta) > 10)
                {
                    predictTargetTick = curPredict;
                    subPredictTargetTick = 0;
                }
                else
                {
                    predictionTimeScale = math.clamp(1.0f + 0.1f * predictDelta, .9f, 1.1f);
                    subPredictTargetTick += deltaTicks + predictionTimeScale;
                    uint pdiff = (uint) subPredictTargetTick;
                    subPredictTargetTick -= pdiff;
                    predictTargetTick += pdiff;
                }
            }

            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            timeScale += predictionTimeScale;
            ++timeScaleSamples;
            #endif

            //currentInterpolationFrames = currentInterpolationFrames * 7f / 8f + interpolationFrames / 8f;
            currentInterpolationFrames += math.clamp((interpolationFrames-currentInterpolationFrames)*.1f, -.1f, .1f);

            var idiff = (uint)currentInterpolationFrames;
            interpolateTargetTick = predictTargetTick - idiff;
            var subidiff = currentInterpolationFrames - idiff;
            subidiff -= subInterpolateTargetTick+subPredictTargetTick;
            if (subidiff < 0)
            {
                ++interpolateTargetTick;
                subidiff = -subidiff;
            }
            else if (subidiff > 0)
            {
                idiff = (uint)subidiff;
                subidiff -= idiff;
                interpolateTargetTick -= idiff;
                subidiff = 1f-subidiff;
            }
            subInterpolateTargetTick = subidiff;
        }
    }
}
