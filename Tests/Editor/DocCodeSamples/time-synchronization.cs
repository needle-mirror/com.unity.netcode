using Unity.Entities;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    partial class time_synchronization
    {
        public partial struct TimeExampleSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.RequireForUpdate<NetworkTime>();
            }

            public void OnUpdate(ref SystemState state)
            {
                #region GetTime
                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                var currentTick = networkTime.ServerTick;
                // ...
                #endregion
            }
        }
    }
}
