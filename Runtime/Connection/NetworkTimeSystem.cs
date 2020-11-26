using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.NetCode
{
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
#if !UNITY_DOTSRUNTIME
    [UpdateAfter(typeof(UpdateWorldTimeSystem))]
#endif
    public class NetworkTimeSystem : SystemBase
    {
        public static ClientTickRate DefaultClientTickRate => new ClientTickRate
        {
            InterpolationTimeNetTicks = 2,
            MaxExtrapolationTimeSimTicks = 20,
            MaxPredictAheadTimeMS = 500,
            TargetCommandSlack = 2
        };
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public static uint s_FixedTimestampMS = 0;
        private static uint s_PrevTimestampMS = 0;
        private static uint s_TimestampAdjustMS = 0;
        public static uint TimestampMS
        {
            get
            {
                // If fixed timestamp is set, use that
                if (s_FixedTimestampMS != 0)
                    return s_FixedTimestampMS;
                var cur = (uint) (System.Diagnostics.Stopwatch.GetTimestamp() / System.TimeSpan.TicksPerMillisecond);
                // If more than 100ms passed since last timestamp heck, increase the adjustment so the reported time delta is 100ms
                if (s_PrevTimestampMS != 0 && (cur - s_PrevTimestampMS) > 100)
                {
                    s_TimestampAdjustMS += (cur - s_PrevTimestampMS) - 100;
                }
                s_PrevTimestampMS = cur;
                return cur - s_TimestampAdjustMS;
            }
        }
#else
        public static uint TimestampMS =>
            (uint) (System.Diagnostics.Stopwatch.GetTimestamp() / System.TimeSpan.TicksPerMillisecond);
#endif

        internal uint interpolateTargetTick;
        internal uint predictTargetTick;
        internal float subInterpolateTargetTick;
        internal float subPredictTargetTick;

        private uint latestSnapshot;
        private uint latestSnapshotEstimate;
        private int latestSnapshotAge;
        internal float currentInterpolationFrames;
        private NativeArray<float> commandAgeAdjustment;
        private int commandAgeAdjustmentSlot;
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

        protected override void OnCreate()
        {
            latestSnapshotEstimate = 0;
            latestSnapshot = 0;
            latestSnapshotAge = 0;
            commandAgeAdjustment = new NativeArray<float>(64, Allocator.Persistent);
            RequireSingletonForUpdate<NetworkSnapshotAckComponent>();
        }
        protected override void OnDestroy()
        {
            commandAgeAdjustment.Dispose();
        }
        protected override void OnStopRunning()
        {
            interpolateTargetTick = predictTargetTick = 0;
            latestSnapshotEstimate = 0;
        }
        protected override void OnUpdate()
        {
            var tickRate = default(ClientServerTickRate);
            if (HasSingleton<ClientServerTickRate>())
            {
                tickRate = GetSingleton<ClientServerTickRate>();
            }

            tickRate.ResolveDefaults();

            var clientTickRate = DefaultClientTickRate;
            if (HasSingleton<ClientTickRate>())
                clientTickRate = GetSingleton<ClientTickRate>();

            var ack = GetSingleton<NetworkSnapshotAckComponent>();

            float deltaTime = Time.DeltaTime;
            if (deltaTime > (float) tickRate.MaxSimulationStepsPerFrame / (float) tickRate.SimulationTickRate)
            {
                deltaTime = (float) tickRate.MaxSimulationStepsPerFrame / (float) tickRate.SimulationTickRate;
            }
            float deltaTicks = deltaTime * tickRate.SimulationTickRate;

            var estimatedRTT = math.min(ack.EstimatedRTT, clientTickRate.MaxPredictAheadTimeMS);
            // FIXME: adjust by latency
            uint interpolationTimeMS = clientTickRate.InterpolationTimeMS;
            if (interpolationTimeMS == 0)
                interpolationTimeMS = (1000 * clientTickRate.InterpolationTimeNetTicks + (uint) tickRate.NetworkTickRate - 1) /
                                      (uint) tickRate.NetworkTickRate;
            float interpolationFrames = 0.5f + clientTickRate.TargetCommandSlack + (((estimatedRTT + 4*ack.DeviationRTT + interpolationTimeMS) / 1000f) * tickRate.SimulationTickRate);

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

                predictTargetTick = latestSnapshotEstimate + clientTickRate.TargetCommandSlack +
                                  ((uint) estimatedRTT * (uint) tickRate.SimulationTickRate + 999) / 1000;

                currentInterpolationFrames = interpolationFrames;
                for (int i = 0; i < commandAgeAdjustment.Length; ++i)
                    commandAgeAdjustment[i] = 0;
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
            // Check which slot in the circular buffer of command age adjustments the current data should go in
            int curSlot = (int)(ack.LastReceivedSnapshotByLocal % commandAgeAdjustment.Length);
            // If we moved to a new slot, clear the data between previous and new
            if (curSlot != commandAgeAdjustmentSlot)
            {
                for (int i = (commandAgeAdjustmentSlot + 1) % commandAgeAdjustment.Length;
                    i != (curSlot+1) % commandAgeAdjustment.Length;
                    i = (i+1) % commandAgeAdjustment.Length)
                {
                    commandAgeAdjustment[i] = 0;
                }
                commandAgeAdjustmentSlot = curSlot;
            }

            float commandAge = ack.ServerCommandAge / 256.0f + clientTickRate.TargetCommandSlack;
            // round down to whole ticks performed in one rtt
            int rttInTicks = (int)(((uint) estimatedRTT * (uint) tickRate.SimulationTickRate) / 1000);
            if (rttInTicks > commandAgeAdjustment.Length)
                rttInTicks = commandAgeAdjustment.Length;
            for (int i = 0; i < rttInTicks; ++i)
                commandAge -= commandAgeAdjustment[(commandAgeAdjustment.Length+commandAgeAdjustmentSlot-i) % commandAgeAdjustment.Length];
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
                uint curPredict = latestSnapshotEstimate + clientTickRate.TargetCommandSlack +
                                  ((uint) estimatedRTT * (uint) tickRate.SimulationTickRate + 999) / 1000;
                float predictDelta = (float)((int)curPredict - (int)predictTargetTick) - deltaTicks;
                if (math.abs(predictDelta) > 10)
                {
                    //Attention! this may can rollback in case we have an high difference in estimate (about 10 ticks greater)
                    //and predictDelta is negative (client is too far ahead)
                    if (predictDelta < 0.0f)
                    {
                        UnityEngine.Debug.LogError($"Large serverTick prediction error. Server tick rollback to {curPredict} delta: {predictDelta}");
                    }
                    predictTargetTick = curPredict;
                    subPredictTargetTick = 0;
                    for (int i = 0; i < commandAgeAdjustment.Length; ++i)
                        commandAgeAdjustment[i] = 0;
                }
                else
                {
                    predictionTimeScale = math.clamp(1.0f + 0.1f * predictDelta, .9f, 1.1f);
                    subPredictTargetTick += deltaTicks * predictionTimeScale;
                    uint pdiff = (uint) subPredictTargetTick;
                    subPredictTargetTick -= pdiff;
                    predictTargetTick += pdiff;
                }
            }
            commandAgeAdjustment[commandAgeAdjustmentSlot] += deltaTicks * (predictionTimeScale - 1.0f);

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
