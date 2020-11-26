using Unity.Entities;

namespace Unity.NetCode
{
    public struct ClientServerTickRate : IComponentData
    {
        public enum FrameRateMode
        {
            Auto,
            BusyWait,
            Sleep
        }

        public int SimulationTickRate;
        public int NetworkTickRate;
        public int MaxSimulationStepsPerFrame;
        public FrameRateMode TargetFrameRateMode;

        public void ResolveDefaults()
        {
            if (SimulationTickRate <= 0)
                SimulationTickRate = 60;
            if (NetworkTickRate <= 0)
                NetworkTickRate = SimulationTickRate;
            if (MaxSimulationStepsPerFrame <= 0)
                MaxSimulationStepsPerFrame = 4;
        }
    }

    public struct ClientServerTickRateRefreshRequest : IComponentData
    {
        public int SimulationTickRate;
        public int NetworkTickRate;
        public int MaxSimulationStepsPerFrame;
    }

    public struct ClientTickRate : IComponentData
    {
        /// <summary>
        /// The number of network ticks to use as an interpolation buffer for interpolated ghosts.
        /// </summary>
        public uint InterpolationTimeNetTicks;
        /// <summary>
        /// The time in ms to use as an interpolation buffer for interpolated ghosts, this will take precedence and override the
        /// interpolation time in ticks if specified.
        /// </summary>
        public uint InterpolationTimeMS;
        /// <summary>
        /// The maximum time in simulation ticks which the client can extrapolate ahead when data is missing.
        /// </summary>
        public uint MaxExtrapolationTimeSimTicks;
        /// <summary>
        /// This is the maximum accepted ping, rtt will be clamped to this value when calculating server tick on the client,
        /// which means if ping is higher than this the server will get old commands.
        /// Increasing this makes the client able to deal with higher ping, but the client needs to run more prediction steps which takes more CPU time
        /// </summary>
        public uint MaxPredictAheadTimeMS;
        /// <summary>
        /// Specifies the number of simulation ticks the client tries to make sure the commands are received by the server
        /// before they are used on the server.
        /// </summary>
        public uint TargetCommandSlack;
    }
}