using System;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Flags used by <see cref="NetworkTime"/> singleton to add some properties to the current simulated tick.
    /// See the individual flags documentation for further information.
    /// </summary>
    [Flags]
    public enum NetworkTimeFlags : byte
    {
        /// <summary>
        /// Indicate that the current <see cref="NetworkTime.ServerTick"/> is a predicted one and the simulation is running inside the prediction group.
        /// </summary>
        IsInPredictionLoop = 1 << 0,
        /// <summary>
        /// Only valid inside the prediction loop, the server tick the prediction is starting from.
        /// </summary>
        IsFirstPredictionTick = 1 << 2,
        /// <summary>
        /// Only valid inside the prediction loop, the current server tick which will be the last tick to predict.
        /// </summary>
        IsFinalPredictionTick = 1 << 3,
        /// <summary>
        /// Only valid inside the prediction loop, the current server tick is the last full tick we are predicting. If IsFinalPredictionTick is set
        /// the IsPartial flag must be false. The IsFinalPredictionTick can be also set if the current server tick we are predicting is a full tick.
        /// </summary>
        IsFinalFullPredictionTick = 1 << 4,
        /// <summary>
        /// Only valid on server. True when the current simulated tick is running with a variabled delta time to recover from
        /// a previous long running frame.
        /// </summary>
        IsCatchUpTick = 1 << 5,
        /// <summary>
        /// Only valid inside the prediction loop, the current server tick is a full tick and this is the first time it is being predicting as a non-partial tick.
        /// The IsPartial flag must be false.
        /// This is frequently used to make sure effects which cannot easily be rolled back, such as spawning objects / particles / vfx or playing sounds, only happens once and are not repeated.
        /// </summary>
        IsFirstTimeFullyPredictingTick = 1 << 6,
    }
    /// <summary>
    /// Present on both client and server world, singleton component that contains all the timing characterist of the client/server simulation loop.
    /// </summary>
    public struct NetworkTime : IComponentData
    {
        /// <summary>
        /// The current simulated server tick the server will run this frame. Always start from 1. 0 is considered an invalid value.
        /// The ServerTick value behaves differently on client vs the server.
        /// On the server:
        ///  - it is always a "full" tick
        ///  - i.e. it is strict and monotone, and only increments on the server DGS render (i.e. UnityEngine) frame rate (which you can configure to be the same as this).
        ///  - is therefore the same inside or outside the prediction loop
        /// On the client:
        ///  - it is the tick the client is currently predicting the server should simulate on this frame.
        ///  I.e. Depends on current <see cref="NetworkSnapshotAck.EstimatedRTT"/> and <see cref="ClientTickRate.TargetCommandSlack"/> values.
        ///  - can be either a full or partial (see details on client partial ticks in docs).
        ///  - it is not monotone:
        ///      - in some rare/recovery situations, it may rollback, or jump forward (due to time/lag adjustments).
        ///      - during the prediction loop, the ServerTick value is changed to match either the last full simulated tick or,
        ///      in case of a rollback (because a snapshot has been received), to the oldest received tick among all entities.
        ///  - in both cases, this value will be reset to current predicted server tick at the end of the prediction loop.
        /// </summary>
        /// <remarks>
        /// Use <see cref="InputTargetTick"/> (not <see cref="ServerTick"/>!) when assigning a tick value to
        /// your command data values for <see cref="CommandDataUtility.AddCommandData{T}"/>.
        /// </remarks>
        public NetworkTick ServerTick;

        /// <summary>
        /// The tick we should be gathering (i.e. raising, sending) input commands for, for them to arrive in time
        /// to be processed by the server.
        /// It is identical to the <see cref="ServerTick"/> except; a) when using <see cref="ClientTickRate.MaxPredictAheadTimeMS"/>
        /// with a very high ping connection, b) when using <see cref="ClientTickRate.ForcedInputLatencyTicks"/>, c) when in an "off" frame with no prediction in
        /// <see cref="NetCodeConfig.HostWorldMode.SingleWorld"/> mode (in this case, <see cref="InputTargetTick"/> is for the next tick, as we're accumulating inputs for it in those off frames).
        /// The four timelines are therefore in this order: <c>Interpolation Tick (oldest) -> Snapshot Arrival Tick (from the server)
        /// -> ServerTick (client prediction) -> InputTargetTick (i.e. inputs being sent)</c>.
        /// </summary>
        /// <remarks>
        /// Use this variable (not <see cref="ServerTick"/>) when assigning a tick value to your command data
        /// values for <see cref="CommandDataUtility.AddCommandData{T}"/>.
        /// </remarks>
        public NetworkTick InputTargetTick
        {
            get
            {
                if (ServerTick.IsValid)
                {
                    var networkTick = ServerTick;
                    networkTick.Add(EffectiveInputLatencyTicks);
                    if (IsOffFrame)
                        networkTick.Add(1);
                    return networkTick;
                }
                return NetworkTick.Invalid;
            }
        }

        /// <summary>
        /// The current effective <see cref="ClientTickRate.ForcedInputLatencyTicks"/> value (in ticks). <b>Client-only!</b>
        /// </summary>
        /// <remarks>
        /// Note: This value will increase if/when the clients ping value is greater than <see cref="ClientTickRate.MaxPredictAheadTimeMS"/>.
        /// </remarks>
        public uint EffectiveInputLatencyTicks;
        /// <summary>
        /// Only meaningful on the client that run at variable step rate. On the server is always 1.0. Always in range is (0.0 and 1.0].
        /// </summary>
        public float ServerTickFraction;
        /// <summary>
        /// The current interpolated tick (integral part). Always less than the ServerTick on the Client (and equal to ServerTick on the server).
        /// </summary>
        public NetworkTick InterpolationTick;
        /// <summary>
        /// The fractional part of the tick (XXX.fraction). Always in between (0.0, 1.0]
        /// </summary>
        public float InterpolationTickFraction;
        /// <summary>
        /// The number of simulation steps this tick is scaled with. This is used to make one update which covers
        /// N ticks in order to reduce CPU cost. This is always 1 for partial ticks in the prediction loop, but can be more than 1 for partial ticks outside the prediction loop.
        /// </summary>
        public int SimulationStepBatchSize;
        /// <summary>
        ///  For internal use only, special flags that add context and properties to the current server tick value.
        /// </summary>
        internal NetworkTimeFlags Flags;
        /// <summary>
        ///  For internal use only, the total elapsed network time since the world has been created. Different for server and client:
        /// - On the server is advanced at fixed time step interval (depending on ClientServerTickRate)
        /// - On the client use the computed network delta time based on the predicted server tick. The time time is not monotone.
        /// </summary>
        internal double ElapsedNetworkTime;
        /// <summary>
        /// True if the current tick is running with delta time that is a fraction of the ServerTickDeltaTime. Only true on the client when
        /// running at variable frame rate.
        /// </summary>
        public bool IsPartialTick => ServerTickFraction < 1f;
        /// <summary>
        /// Indicate that the current <see cref="NetworkTime.ServerTick"/> is a predicted one and the simulation is running inside the prediction group.
        /// </summary>
        public bool IsInPredictionLoop => (Flags & NetworkTimeFlags.IsInPredictionLoop) != 0;
        /// <summary>
        /// Only valid inside the prediction loop. The server tick the prediction is starting from.
        /// </summary>
        public bool IsFirstPredictionTick => (Flags & NetworkTimeFlags.IsFirstPredictionTick) != 0;
        /// <summary>
        ///  Only valid inside the prediction loop. The current server tick which will be the last tick to predict
        /// </summary>
        public bool IsFinalPredictionTick => (Flags & NetworkTimeFlags.IsFinalPredictionTick) != 0;
        /// <summary>
        /// Only valid inside the prediction loop. The current server tick which will be the last full tick we are predicting
        /// </summary>
        public bool IsFinalFullPredictionTick => (Flags & NetworkTimeFlags.IsFinalFullPredictionTick) != 0;
        /// <summary>
        /// Only valid inside the prediction loop. True when this `ServerTick` is being predicted in full for the first time.
        /// "In full" meaning the first non-partial simulation tick. I.e. Partial ticks don't count.
        /// </summary>
        public bool IsFirstTimeFullyPredictingTick => (Flags & NetworkTimeFlags.IsFirstTimeFullyPredictingTick) != 0;
        /// <summary>
        /// Only valid on server. When the server determines that it is behind by more than one tick,
        /// it queries <see cref="ClientServerTickRate.MaxSimulationStepBatchSize"/> and
        /// <see cref="ClientServerTickRate.MaxSimulationStepsPerFrame"/> to determine how to catch up.
        /// If your configuration causes the server to simulate two or more ticks within a single frame, all non-final
        /// ticks will have the catchup flag set to true.
        /// <br/>Note: Batching multiple ticks into one tick will not - by itself - be considered a catch up tick.
        /// </summary>
        /// <remarks>
        /// This flag is used to limit the sending of snapshots (via <see cref="GhostSendSystem"/>) when
        /// <see cref="ClientServerTickRate.SendSnapshotsForCatchUpTicks"/> is false.
        /// </remarks>
        public bool IsCatchUpTick => (Flags & NetworkTimeFlags.IsCatchUpTick) != 0;
        /// <summary>
        /// Counts the number of predicted ticks that have been triggered on this frame (while inside the prediction loop).
        /// Thus, client only, and increments BEFORE the tick occurs (i.e. the first predicted tick will have a value of 1).
        /// Outside the prediction loop, records the current or last frames prediction tick count (until prediction restarts).
        /// </summary>
        public int PredictedTickIndex { get; internal set; }
        /// <summary>
        /// Counts the number of predicted ticks expected to trigger on this frame, ignoring batching.
        /// Client side: written at the start of the prediction loop (see <see cref="PredictedSimulationSystemGroup"/>) and is set BEFORE the first tick occurs.
        /// Server side: written right before the simulation group (<see cref="SimulationSystemGroup"/>).
        /// </summary>
        /// <remarks>
        /// With Single World Host, it's possible to have "off" frames where no game prediction group executes. If there will be or if there has been a
        /// prediction group execution this frame, this value will be set. This value is only set during the SimulationSystemGroup.
        /// To see if a tick will execute this frame, use <see cref="IsOffFrame"/>
        /// </remarks>
        public int NumPredictedTicksExpected { get; internal set; }
        /// <summary>
        /// Indicates whether we're in an "off" frame where no netcode tick is executing.
        /// Always false on clients worlds, since they always have partial ticks.
        /// For server and host worlds, this will depend on the tick rate and <see cref="ClientServerTickRate.FrameRateMode"/>
        /// This value is updated in <see cref="UpdateNetworkTimeSystem"/> so make sure to read it after this system executes
        /// </summary>
        public bool IsOffFrame;

        /// <summary>Helper to debug NetworkTime issues via logs.</summary>
        /// <returns>Formatted string containing NetworkTime data.</returns>
        public FixedString512Bytes ToFixedString()
        {
            var commandInterpolationDelay = ServerTick.IsValid && InterpolationTick.IsValid ? ServerTick.TicksSince(InterpolationTick) : 0;
            FixedString512Bytes flags = default;
            if (Flags == default)
                flags = "0";
            else
            {
                if (IsInPredictionLoop) flags.Append((FixedString32Bytes) $"|{nameof(IsInPredictionLoop)}");
                if (IsFirstPredictionTick) flags.Append((FixedString32Bytes) $"|{nameof(IsFirstPredictionTick)}");
                if (IsFinalPredictionTick) flags.Append((FixedString32Bytes) $"|{nameof(IsFinalPredictionTick)}");
                if (IsFinalFullPredictionTick) flags.Append((FixedString32Bytes) $"|{nameof(IsFinalFullPredictionTick)}");
                if (IsFirstTimeFullyPredictingTick) flags.Append((FixedString64Bytes) $"|{nameof(IsFirstTimeFullyPredictingTick)}");
                if (IsCatchUpTick) flags.Append((FixedString32Bytes) $"|{nameof(IsCatchUpTick)}");
            }
            FixedString32Bytes partial = IsPartialTick ? "PARTIAL" : "FULL";
            return $"NetworkTime[ServerTick:{ServerTick.ToFixedString()}|{(int) (ServerTickFraction * 100)}%|{partial}|+{SimulationStepBatchSize}|{PredictedTickIndex}/{NumPredictedTicksExpected}, InputTargetTick:{InputTargetTick.ToFixedString()}|+{EffectiveInputLatencyTicks}, InterpolationTick:{InterpolationTick.ToFixedString()}|{(int) (InterpolationTickFraction * 100)}%|D{commandInterpolationDelay}, Flags:{flags}]";
        }

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }

    /// <summary>
    /// Component added to the NetworkTime singleton entity when it is created in a client world. Contains the unscaled application
    /// ElapsedTime and DeltaTime.
    /// </summary>
    public struct UnscaledClientTime : IComponentData
    {
        /// <summary>
        /// The current unscaled elapsed time since the World has been created. Reliably traking the real elapsed time and
        /// it is always consistent in all the client states (connected/disconnected/ingame).
        /// </summary>
        public double UnscaleElapsedTime;
        /// <summary>
        /// The current unscaled delta time since since last frame.
        /// </summary>
        public float UnscaleDeltaTime;
    }

    static class NetworkTimeHelper
    {
        /// <summary>
        /// Return the current ServerTick value if is a full tick, otherwise the previous one. The returned
        /// server tick value is correctly wrap around (server tick never equal 0)
        /// </summary>
        /// <param name="networkTime"></param>
        /// <returns></returns>
        static public NetworkTick LastFullServerTick(in NetworkTime networkTime)
        {
            var targetTick = networkTime.ServerTick;
            if (targetTick.IsValid && networkTime.IsPartialTick)
            {
                targetTick.Decrement();
            }
            return targetTick;
        }

        /// <summary>
        /// Returns the current InputTargetTick value if is a full tick, otherwise the previous one (for a partial tick).
        /// </summary>
        /// <param name="networkTime"></param>
        /// <returns></returns>
        public static NetworkTick LastFullInputTargetTick(in NetworkTime networkTime)
        {
            var targetTick = networkTime.InputTargetTick;
            if (targetTick.IsValid && networkTime.IsPartialTick)
            {
                targetTick.Decrement();
            }
            return targetTick;
        }
    }

    /// <summary>
    /// System in charge of updating some network time values in advance, so they can be used outside the normal <see cref="SimulationSystemGroup"/>
    /// In order to get <see cref="NetworkTime.IsOffFrame"/>, make sure your system executes after this system.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct UpdateNetworkTimeSystem : ISystem
    {
        /// <inheritdoc cref="ISystem.OnUpdate"/>
        public void OnUpdate(ref SystemState state)
        {
            // need to set IsOffFrame outside of the SimulationGroup rate managers, so it can be accessed by user systems outside of that group. That's because
            // server worlds just don't run the simulation group, so users would never be able to read a valid value.
            ref var networkTime = ref SystemAPI.GetSingletonRW<NetworkTime>().ValueRW;
            var rateManager = state.World.GetExistingSystemManaged<SimulationSystemGroup>().RateManager;
            if (state.World.IsServer())
            {
                if (state.World.IsHost())
                {
                    var hostRateManager = rateManager as NetcodeHostRateManager;
                    networkTime.IsOffFrame = !hostRateManager.WillUpdateInternal();
                }
                else
                {
                    var serverRateManager = rateManager as NetcodeServerRateManager;
#pragma warning disable CS0618 // Type or member is obsolete
                    networkTime.IsOffFrame = !serverRateManager.WillUpdateInternal();
#pragma warning restore CS0618 // Type or member is obsolete
                }
            }
            else
            {
                networkTime.IsOffFrame = false; // clients have partial ticks, they always tick
            }
        }
    }
}
