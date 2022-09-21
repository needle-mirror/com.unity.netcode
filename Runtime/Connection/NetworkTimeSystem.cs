using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    internal struct NetworkTimeSystemStats : IComponentData
    {
        public float timeScale;
        public float interpTimeScale;
        private float averageTimeScale;
        private float averageInterpTimeScale;
        public float currentInterpolationFrames;
        public int timeScaleSamples;
        public int interpTimeScaleSamples;

        public void UpdateStats(float predictionTimeScale, float interpolationTimeScale, float interpolationFrames)
        {
            timeScale += predictionTimeScale;
            ++timeScaleSamples;
            interpTimeScale += interpolationTimeScale;
            ++interpTimeScaleSamples;
            currentInterpolationFrames = interpolationFrames;
        }

        public float GetAverageTimeScale()
        {
            if (timeScaleSamples > 0)
            {
                averageTimeScale = timeScale / timeScaleSamples;
                timeScale = 0;
                timeScaleSamples = 0;
            }

            return averageTimeScale;
        }

        public float GetAverageIterpTimeScale()
        {
            if (interpTimeScaleSamples > 0)
            {
                averageInterpTimeScale = interpTimeScale / interpTimeScaleSamples;
                interpTimeScale = 0;
                interpTimeScaleSamples = 0;
            }
            return averageInterpTimeScale;
        }
    }
#endif

    internal struct NetworkTimeSystemData : IComponentData
    {
        public NetworkTick interpolateTargetTick;
        public NetworkTick predictTargetTick;
        public float subInterpolateTargetTick;
        public float subPredictTargetTick;
        public float currentInterpolationFrames;
    }

    /// <summary>
    /// <para>System responsible for estimating the <see cref="NetworkTime.ServerTick"/> and <see cref="NetworkTime.InterpolationTick"/>
    /// using the current round trip time (see <see cref="NetworkSnapshotAckComponent"/>) and feedback from the server (see <see cref="NetworkSnapshotAckComponent.ServerCommandAge"/>).</para>
    /// <para>The system tries to keep the server tick (present on the client) ahead of the server, such that input commands (see <see cref="ICommandData"/> and <see cref="IInputComponentData"/>)
    /// are received <i>before</i> the server needs them for the simulation.
    /// The system speeds up and slows down the client simulation elapsed delta time to compensate for changes in the network condition, and makes the reported
    /// <see cref="NetworkSnapshotAckComponent.ServerCommandAge"/> close to the <see cref="ClientTickRate.TargetCommandSlack"/>.</para>
    /// <para>This time synchronization start taking place as soon as the first snapshot is received by the client. Because of that,
    /// until the client <see cref="NetworkStreamConnection"/> is not set in-game (see <see cref="NetworkStreamInGame"/>), the calculated
    /// server tick and interpolated are always 0.</para>
    /// <para>In the case where the client and server world are on the same process, and an IPC connection is used (see <see cref="TransportType.IPC"/>),
    /// some special optimizations can be applied. E.g. In this case the client should always run 1 tick per frame (server and client update in tandem).</para>
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ThinClientSimulation)]
#if UNITY_DOTSRUNTIME
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast=true)] // FIXME: cannot get access to the dots runtime version of UpdateWorldTimeSystem here, so just put it last
#else
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(UpdateWorldTimeSystem))]
#endif
    unsafe public partial struct NetworkTimeSystem : ISystem, ISystemStartStop
    {
        /// <summary>
        /// A new <see cref="ClientTickRate"/> instance initialized with good and sensible default values.
        /// </summary>
        public static ClientTickRate DefaultClientTickRate => new ClientTickRate
        {
            InterpolationTimeNetTicks = 2,
            MaxExtrapolationTimeSimTicks = 20,
            MaxPredictAheadTimeMS = 500,
            TargetCommandSlack = 2,
            CommandAgeCorrectionFraction = 0.1f,
            PredictionTimeScaleMin = 0.9f,
            PredictionTimeScaleMax = 1.1f,
            InterpolationDelayJitterScale = 3,
            InterpolationDelayMaxDeltaTicksFraction = 0.1f,
            InterpolationDelayCorrectionFraction = 0.1f,
            InterpolationTimeScaleMin = 0.85f,
            InterpolationTimeScaleMax = 1.1f
        };

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal static uint s_FixedTimestampMS{get{return s_FixedTime.Data.FixedTimestampMS;} set{s_FixedTime.Data.FixedTimestampMS = value;}}
        private struct FixedTime
        {
            public uint FixedTimestampMS;
            internal uint PrevTimestampMS;
            internal uint TimestampAdjustMS;
        }
        private static readonly SharedStatic<FixedTime> s_FixedTime = SharedStatic<FixedTime>.GetOrCreate<FixedTime>();

        /// <summary>
        /// Return a low precision real-time stamp that represents the number of milliseconds since the process started.
        /// In Development build and Editor, the maximum reported delta in between two calls of the TimestampMS is capped
        /// to 100 milliseconds.
        /// <remarks>
        /// The TimestampMS is mostly used for sake of time synchronization (for calculting the RTT).
        /// </remarks>
        /// </summary>
        public static uint TimestampMS
        {
            get
            {
                // If fixed timestamp is set, use that
                if (s_FixedTime.Data.FixedTimestampMS != 0)
                    return s_FixedTime.Data.FixedTimestampMS;
                //FIXME If the stopwatch is not high resolution means that it is based on the system timer, witch have a precision of about 10ms
                //This can be a little problematic for computing the right timestamp in general
                var cur = (uint)TimerHelpers.GetCurrentTimestampMS();
                // If more than 100ms passed since last timestamp heck, increase the adjustment so the reported time delta is 100ms
                if (s_FixedTime.Data.PrevTimestampMS != 0 && (cur - s_FixedTime.Data.PrevTimestampMS) > 100)
                {
                    s_FixedTime.Data.TimestampAdjustMS += (cur - s_FixedTime.Data.PrevTimestampMS) - 100;
                }
                s_FixedTime.Data.PrevTimestampMS = cur;
                return cur - s_FixedTime.Data.TimestampAdjustMS;
            }
        }
#else
        /// <summary>
        /// Return a low precision real-time stamp that represents the number of milliseconds since the process started.
        /// In Development build and Editor, the maximum reported delta in between two calls of the TimestampMS is capped
        /// to 100 milliseconds.
        /// <remarks>
        /// The TimestampMS is mostly used for sake of time synchronization (for calculting the RTT).
        /// </remarks>
        /// </summary>
        public static uint TimestampMS =>
            (uint)TimerHelpers.GetCurrentTimestampMS();
#endif

        private NetworkTick latestSnapshot;
        private NetworkTick latestSnapshotEstimate;
        private int latestSnapshotAge;
        //The average of the delta ticks in between snapshot. Is the current perceived estimate of the SimulationTickRate/SnapshotTickRate. Ex:
        //If the server send at 30hz and the sim is 60hz the avg ratio should be 2
        private float avgNetTickRate;
        //The "std" deviation / jitter (actually an approximation of it) of the perceived netTickRate.
        private float devNetTickRate;
        private const int CommandAgeAdjustmentLength = 64;
        private fixed float commandAgeAdjustment[CommandAgeAdjustmentLength];
        private int commandAgeAdjustmentSlot;

        /// <summary>
        /// Create the <see cref="NetworkTimeSystemData"/> singleton and reset the initial system state.
        /// </summary>
        /// <param name="state"></param>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            latestSnapshotEstimate = NetworkTick.Invalid;
            latestSnapshot = NetworkTick.Invalid;
            latestSnapshotAge = 0;
            fixed (void* commandAgePtr = commandAgeAdjustment)
                UnsafeUtility.MemClear(commandAgePtr, CommandAgeAdjustmentLength*sizeof(float));

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var types = new NativeArray<ComponentType>(2, Allocator.Temp);
            types[0] = ComponentType.ReadWrite<NetworkTimeSystemData>();
            types[1] = ComponentType.ReadWrite<NetworkTimeSystemStats>();
#else
            var types = new NativeArray<ComponentType>(1, Allocator.Temp);
            types[0] = ComponentType.ReadWrite<NetworkTimeSystemData>();
#endif
            var netTimeStatEntity = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(types));
            FixedString64Bytes singletonName = "NetworkTimeSystemData";
            state.EntityManager.SetName(netTimeStatEntity, singletonName);
            state.RequireForUpdate<NetworkSnapshotAckComponent>();
        }

        /// <summary>
        /// Empty method, implement the <see cref="ISystem"/> interface.
        /// </summary>
        /// <param name="state"></param>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
        }

        /// <summary>
        /// Empty method, implement the <see cref="ISystem"/> interface.
        /// </summary>
        /// <param name="state"></param>
        [BurstCompile]
        public void OnStartRunning(ref SystemState state)
        {
        }

        /// <summary>
        /// Reset the <see cref="NetworkTimeSystemData"/> data and some internal variables.
        /// </summary>
        /// <param name="state"></param>
        [BurstCompile]
        public void OnStopRunning(ref SystemState state)
        {
            latestSnapshotEstimate = NetworkTick.Invalid;
            SystemAPI.SetSingleton(new NetworkTimeSystemData());
        }

        /// <summary>
        /// Implements all the time synchronization logic on the main thread.
        /// </summary>
        /// <param name="state"></param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            var clientTickRate = DefaultClientTickRate;
            if (SystemAPI.HasSingleton<ClientTickRate>())
                clientTickRate = SystemAPI.GetSingleton<ClientTickRate>();

            state.CompleteDependency(); // We complete the dependency. This is needed because NetworkSnapshotAckComponent is written by a job in NetworkStreamReceiveSystem

            var ack = SystemAPI.GetSingleton<NetworkSnapshotAckComponent>();
            bool isInGame = SystemAPI.HasSingleton<NetworkStreamInGame>();

            float deltaTime = SystemAPI.Time.DeltaTime;
            if(isInGame && ClientServerBootstrap.HasServerWorld.Data != 0)
            {
                var maxDeltaTicks = (uint)tickRate.MaxSimulationStepsPerFrame * (uint)tickRate.MaxSimulationStepBatchSize;
                if (deltaTime > (float) maxDeltaTicks / (float) tickRate.SimulationTickRate)
                    deltaTime = (float) maxDeltaTicks / (float) tickRate.SimulationTickRate;
            }
            float deltaTicks = deltaTime * tickRate.SimulationTickRate;
            //If the client is using an IPC connection within an inprocess server we know that
            // latency is 0
            // jitter is 0
            // not packet loss
            //
            // That imply the average/desired command slack is 0 (predict only the next tick)
            // and the (ideal) output are
            // predictTargetTick = latestSnapshot + 1
            // interpolationTicks = max(SimulationRate/NetworkTickRate, clientTickRate.InterpolationTimeNetTicks) (or its equivalent ms version)
            // interpolateTargetTick = latestSnapshot - interpolationTicks
            //
            // However, because the client run at variable frame rate (it is not in sync with the server)
            // - there will be partial ticks
            // - the interpolation tick would vary a little bit (some fraction)
            //
            // We can probably force the InterpolationFrames to be constants but we preferred to have all the code path
            // shared, instead of preferential logic, as much as possible.
            // This can be a further optimasation that can be added later.

            var driverType = SystemAPI.GetSingleton<NetworkStreamDriver>().DriverStore.GetDriverType(NetworkDriverStore.FirstDriverId);
            if (driverType == TransportType.IPC)
            {
                //override this param with 0. The predicted target tick is the latest snapshot received + 1 (the next server tick)
                clientTickRate.TargetCommandSlack = 0;
                //these are 0 and we enforce that here
                ack.DeviationRTT = 0f;
                ack.EstimatedRTT = 1000f/tickRate.SimulationTickRate;
            }

            var estimatedRTT = math.min(ack.EstimatedRTT, clientTickRate.MaxPredictAheadTimeMS);
            var netTickRate = (tickRate.SimulationTickRate + tickRate.NetworkTickRate - 1) / tickRate.NetworkTickRate;
            // The minimm number of interpolation frames depend on the ratio in between the simulation and the network tick rate
            // ex: if the server run the sim at 60hz but send at 20hz we need to stay back at least 3 ticks, or
            // any integer multiple of that
            var interpolationTimeTicks = (int)clientTickRate.InterpolationTimeNetTicks;
            if (clientTickRate.InterpolationTimeMS != 0)
                interpolationTimeTicks = (int)((clientTickRate.InterpolationTimeMS * tickRate.NetworkTickRate + 999) / 1000);
            //The number of interpolation frames is expressed as number of simulation ticks. This is why it is necessary to use the netTickRate
            //to convert the units here.
            float minInterpolationFrames = math.max(netTickRate, interpolationTimeTicks*netTickRate);
            // Reset the latestSnapshotEstimate if not in game
            if (latestSnapshotEstimate.IsValid && !isInGame)
                latestSnapshotEstimate = NetworkTick.Invalid;

            ref var netTimeData = ref SystemAPI.GetSingletonRW<NetworkTimeSystemData>().ValueRW;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ref var  netTimeDataStats = ref SystemAPI.GetSingletonRW<NetworkTimeSystemStats>().ValueRW;
#endif

            if (!latestSnapshotEstimate.IsValid)
            {
                if (!ack.LastReceivedSnapshotByLocal.IsValid)
                {
                    netTimeData = default(NetworkTimeSystemData);
                    avgNetTickRate = 0f;
                    devNetTickRate = 0f;
                    return;
                }
                latestSnapshot = ack.LastReceivedSnapshotByLocal;
                latestSnapshotEstimate = ack.LastReceivedSnapshotByLocal;
                latestSnapshotAge = 0;
                netTimeData.predictTargetTick = latestSnapshotEstimate;
                netTimeData.predictTargetTick.Add(clientTickRate.TargetCommandSlack +
                                               ((uint) estimatedRTT * (uint) tickRate.SimulationTickRate + 999) / 1000);
                //initial guess estimate for the interpolation frame. Uses the DeviatioRTT as a measurement of the jitter in the snapshot rate
                avgNetTickRate = netTickRate;
                devNetTickRate = (ack.DeviationRTT * netTickRate / 1000f);
                netTimeData.currentInterpolationFrames = minInterpolationFrames + clientTickRate.InterpolationDelayJitterScale*devNetTickRate;
                netTimeData.interpolateTargetTick = latestSnapshotEstimate;
                netTimeData.interpolateTargetTick.Subtract((uint)netTimeData.currentInterpolationFrames);
                netTimeData.subPredictTargetTick = 0f;

                for (int i = 0; i < CommandAgeAdjustmentLength; ++i)
                    commandAgeAdjustment[i] = 0;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                netTimeDataStats = default(NetworkTimeSystemStats);
#endif
            }
            else
            {
                // Add number of ticks based on deltaTime
                latestSnapshotEstimate.Add((uint) deltaTicks);

                //If ack.LastReceivedSnapshotByLocal is 0, it mean that a desync has been detected.
                //Updating the estimates using deltas in that case is completely wrong
                if (latestSnapshot != ack.LastReceivedSnapshotByLocal && ack.LastReceivedSnapshotByLocal.IsValid)
                {
                    var snapshotDeltaSimTicks = ack.LastReceivedSnapshotByLocal.TicksSince(latestSnapshot);
                    //snapshotAge is a measure of the average down link latency jitter.
                    int snapshotAge = latestSnapshotEstimate.TicksSince(ack.LastReceivedSnapshotByLocal);
                    latestSnapshot = ack.LastReceivedSnapshotByLocal;
                    latestSnapshotAge = (latestSnapshotAge * 7 + (snapshotAge << 8)) / 8;

                    var newSnapshotRatioDeviation = math.abs(snapshotDeltaSimTicks - avgNetTickRate);
                    //The perceived tick rate moving average should react a little faster to changes than the snapshot age.
                    //This help avoiding the situation where the client 'consumes' snapshot packets at double the rate of the server
                    //in case the server run at low frame rate. We are using double the TCP spec (0.125) as factor for this.
                    avgNetTickRate = (1.0f - 0.25f)*avgNetTickRate + 0.25f*snapshotDeltaSimTicks;
                    devNetTickRate = (1.0f - 0.25f)*devNetTickRate + 0.25f*newSnapshotRatioDeviation;
                }

                latestSnapshotAge -= (int) (math.frac(deltaTicks) * 256.0f);
                int delta = latestSnapshotAge >> 8;
                if (delta < 0)
                    ++delta;
                if (delta != 0)
                {
                    latestSnapshotEstimate.Subtract((uint) delta);
                    latestSnapshotAge -= delta << 8;
                }
            }

            float predictionTimeScale = 1f;
            float commandAge = ack.ServerCommandAge / 256.0f + clientTickRate.TargetCommandSlack;

            // Check which slot in the circular buffer of command age adjustments the current data should go in
            // use the latestSnapshot and not the LastReceivedSnapshotByLocal because the latter can be reset to 0, causing
            // a wrong reset of the adjustments
            int curSlot = (int)(latestSnapshot.TickIndexForValidTick % CommandAgeAdjustmentLength);
            // If we moved to a new slot, clear the data between previous and new
            if (curSlot != commandAgeAdjustmentSlot)
            {
                for (int i = (commandAgeAdjustmentSlot + 1) % CommandAgeAdjustmentLength;
                    i != (curSlot+1) % CommandAgeAdjustmentLength;
                    i = (i+1) % CommandAgeAdjustmentLength)
                {
                    commandAgeAdjustment[i] = 0;
                }
                commandAgeAdjustmentSlot = curSlot;
            }
            // round down to whole ticks performed in one rtt
            int rttInTicks = (int)(((uint) estimatedRTT * (uint) tickRate.SimulationTickRate) / 1000);
            if (rttInTicks > CommandAgeAdjustmentLength)
                rttInTicks = CommandAgeAdjustmentLength;
            for (int i = 0; i < rttInTicks; ++i)
                commandAge -= commandAgeAdjustment[(CommandAgeAdjustmentLength+commandAgeAdjustmentSlot-i) % CommandAgeAdjustmentLength];

            if (math.abs(commandAge) < 10)
            {
                predictionTimeScale = math.clamp(1.0f + clientTickRate.CommandAgeCorrectionFraction * commandAge, clientTickRate.PredictionTimeScaleMin, clientTickRate.PredictionTimeScaleMax);
                netTimeData.subPredictTargetTick += deltaTicks * predictionTimeScale;
                uint pdiff = (uint) netTimeData.subPredictTargetTick;
                netTimeData.subPredictTargetTick -= pdiff;
                netTimeData.predictTargetTick.Add(pdiff);
            }
            else
            {
                var curPredict = latestSnapshotEstimate;
                curPredict.Add(clientTickRate.TargetCommandSlack +
                                  ((uint) estimatedRTT * (uint) tickRate.SimulationTickRate + 999) / 1000);
                float predictDelta = (float)(curPredict.TicksSince(netTimeData.predictTargetTick)) - deltaTicks;
                if (math.abs(predictDelta) > 10)
                {
                    //Attention! this may rollback in case we have an high difference in estimate (about 10 ticks greater)
                    //and predictDelta is negative (client is too far ahead)
                    if (predictDelta < 0.0f)
                    {
                        SystemAPI.GetSingleton<NetDebug>().LogError($"Large serverTick prediction error. Server tick rollback to {curPredict} delta: {predictDelta}");
                    }
                    netTimeData.predictTargetTick = curPredict;
                    netTimeData.subPredictTargetTick = 0;
                    for (int i = 0; i < CommandAgeAdjustmentLength; ++i)
                        commandAgeAdjustment[i] = 0;
                }
                else
                {
                    predictionTimeScale = math.clamp(1.0f + clientTickRate.CommandAgeCorrectionFraction * predictDelta, clientTickRate.PredictionTimeScaleMin, clientTickRate.PredictionTimeScaleMax);
                    netTimeData.subPredictTargetTick += deltaTicks * predictionTimeScale;
                    uint pdiff = (uint) netTimeData.subPredictTargetTick;
                    netTimeData.subPredictTargetTick -= pdiff;
                    netTimeData.predictTargetTick.Add(pdiff);
                }
            }

            commandAgeAdjustment[commandAgeAdjustmentSlot] += deltaTicks * (predictionTimeScale - 1.0f);
            //What is the frame we are going to receive next?
            //Our current best estimate is the "latestSnapshotEstimate", that try to guess what is the next frame we are going receive from the server.
            //The interpolation tick should be based on our latestSnapshotEstimate guess and delayed by the some interpolation frame.
            //We use latestSnapshotEstimate as base for the interpolated tick instead of the predicted tick for the following reasons:
            // - The fact the client increase the predicted tick faster, should not cause a faster increment of the interpolation
            // - It more accurately reflect the latest received data, instead of trying to approximate the target from the prediction, that depend on other factors
            //
            // The interpolation frames are calculated as follow:
            // frames = E[avgNetTickRate] + K*std[avgNetTickRate]
            // interpolationTick = latestSnapshotEstimate - frames
            //
            // avgNetTickRate: is calculated based on the delta ticks in between the received snapshot and account for
            //  - packet loss (the interpolation delay should increase)
            //  - server network tick rate changes (the server run slower)
            //  - multiple packets per frames (the interpolation delay should increase)
            // latestSnapshotEstimate: account for latency changes, because it is adjusted based on the delta in between the current estimated and what has been received.
            //
            // Together, latestSnapshotEstimate and avgNetTickRate compensate for the factors that affect the most the increase/decrease of the interpolation delay.

            minInterpolationFrames = math.max(avgNetTickRate, minInterpolationFrames);
            float interpolationFrames = minInterpolationFrames + devNetTickRate*clientTickRate.InterpolationDelayJitterScale;
            var delayChangeLimit = deltaTicks*clientTickRate.InterpolationDelayMaxDeltaTicksFraction;
            //move slowly toward the compute target frames
            netTimeData.currentInterpolationFrames += math.clamp((interpolationFrames-netTimeData.currentInterpolationFrames)*delayChangeLimit, -delayChangeLimit, delayChangeLimit);

            var newInterpolationTargetTick = latestSnapshotEstimate;
            newInterpolationTargetTick.Subtract((uint)netTimeData.currentInterpolationFrames);
            var targetTickDelta = newInterpolationTargetTick.TicksSince(netTimeData.interpolateTargetTick) - netTimeData.subInterpolateTargetTick - deltaTicks;
            var interpolationTimeScale = math.clamp(1.0f + targetTickDelta*clientTickRate.InterpolationDelayCorrectionFraction,
                clientTickRate.InterpolationTimeScaleMin, clientTickRate.InterpolationTimeScaleMax);

            //Try to smoothly move interpolation tick toward the target and never move the interpolation tick backward.
            //TODO: we may conside if the delta is very big to jump toward the target more aggressively
            netTimeData.subInterpolateTargetTick += deltaTicks * interpolationTimeScale;
            uint idiff = (uint) netTimeData.subInterpolateTargetTick;
            netTimeData.interpolateTargetTick.Add(idiff);
            netTimeData.subInterpolateTargetTick -= idiff;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            netTimeDataStats.UpdateStats(predictionTimeScale, interpolationTimeScale, netTimeData.currentInterpolationFrames);
#endif
        }
    }
}
