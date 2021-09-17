using Unity.Entities;

namespace Unity.NetCode
{
    public struct ClientServerTickRate : IComponentData
    {
        /// <summary>
        /// Enum to control how the simulation should deal with running at a higher frame rate than simulation rate.
        /// </summary>
        public enum FrameRateMode
        {
            /// <summary>
            /// Use `Sleep` if running in a server-only build, otherwise use `BusyWait`.
            /// </summary>
            Auto,
            /// <summary>
            /// Let the game loop run at full frequency and skip simulation updates if the accumulated delta time is less than the simulation frequency.
            /// </summary>
            BusyWait,
            /// <summary>
            /// Use `Application.TargetFrameRate` to limit the game loop frequency to the simulation frequency.
            /// </summary>
            Sleep
        }

        /// <summary>
        /// The fixed simulation frequency on the server and prediction loop. The client can render
        /// at a higher or lower rate than this.
        /// </summary>
        public int SimulationTickRate;
        /// <summary>
        /// The rate at which the server sends snapshots to the clients. This can be lower than than
        /// the simulation frequency which means the server only sends new snapshots to the clients
        /// every N frames. The effect of this on the client is similar to having a higher ping,
        /// on the server it will save CPU time and bandwidth.
        /// </summary>
        public int NetworkTickRate;
        /// <summary>
        /// If the server updates at a lower rate than the simulation tick rate it will perform
        /// multiple ticks in the same frame. This setting puts a limit on how many such updates
        /// it can do in a single frame. When this limit is reached the simulation time will update
        /// slower than real time.
        /// The network tick rate only applies to snapshots, the frequency commands and RPCs is not
        /// affected by this setting.
        /// </summary>
        public int MaxSimulationStepsPerFrame;
        /// <summary>
        /// If the server cannot keep up with the simulation frequency with running `MaxSimulationStepsPerFrame`
        /// ticks it is possible to allow each tick to run with a longer delta time in order to keep the game
        /// time updating correctly. This means that instead of running two ticks with delta time N each, the
        /// system will run a single tick with delta time 2*N. It is a less expensive but more inacurate way
        /// of dealing with server performance spikes, it also requires the game logic to be able to handle it.
        /// </summary>
        public int MaxSimulationLongStepTimeMultiplier;
        /// <summary>
        /// If the server is capable of updating more often than the simulation tick rate it can either
        /// skip the simulation tick for some updates (`BusyWait`) or limit the updates using
        /// `Application.TargetFrameRate` (`Sleep`). `Auto` makes it use `Sleep` for dedicated server
        /// builds and `BusyWait` for client and server builds (as well as the editor).
        /// </summary>
        public FrameRateMode TargetFrameRateMode;
        /// <summary>
        /// If the server has to run multiple simulation ticks in the same frame the server can either
        /// send snapshots for all those ticks or just the last one.
        /// </summary>
        public bool SendSnapshotsForCatchUpTicks;

        public void ResolveDefaults()
        {
            if (SimulationTickRate <= 0)
                SimulationTickRate = 60;
            if (NetworkTickRate <= 0)
                NetworkTickRate = SimulationTickRate;
            if (MaxSimulationStepsPerFrame <= 0)
                MaxSimulationStepsPerFrame = 4;
            if (MaxSimulationLongStepTimeMultiplier <= 0)
                MaxSimulationLongStepTimeMultiplier = 1;
        }
    }

    public struct ClientServerTickRateRefreshRequest : IComponentData
    {
        public int SimulationTickRate;
        public int NetworkTickRate;
        public int MaxSimulationStepsPerFrame;
        public int MaxSimulationLongStepTimeMultiplier;
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
