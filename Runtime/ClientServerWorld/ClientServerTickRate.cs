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
            if (NetworkTickRate <= 0)
                NetworkTickRate = 60;
            if (SimulationTickRate <= 0)
                SimulationTickRate = 60;
            if (MaxSimulationStepsPerFrame <= 0)
                MaxSimulationStepsPerFrame = 4;
        }
    }

    public struct FixedClientTickRate : IComponentData
    {
    }
}