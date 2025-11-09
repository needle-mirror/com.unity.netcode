using Unity.Entities;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    namespace MyNamespace
    {
        public struct MyInputComponent : IInputComponentData
        {
            public float horizontal;
            public float vertical;
        }
    }
    partial class thin_clients
    {
        [WorldSystemFilter(WorldSystemFilterFlags.ThinClientSimulation)]
        public partial struct ThinClientInputExampleSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.RequireForUpdate<NetworkId>();
            }

            public void OnUpdate(ref SystemState state)
            {
                #region ThinClientInput
                var myDummyGhostCharacterControllerEntity = state.EntityManager.CreateEntity(typeof(MyNamespace.MyInputComponent), typeof(InputBufferData<MyNamespace.MyInputComponent>));
                var myConnectionEntity = SystemAPI.GetSingletonEntity<NetworkId>();

                // This tells the netcode package which entity it should be sending inputs for.
                state.EntityManager.SetComponentData(myConnectionEntity, new CommandTarget { targetEntity = myDummyGhostCharacterControllerEntity });
                #endregion

                // Just use the same system here, it won't show up in the docs
                // Entities also does not matter for the example
                var thinClientConnectionEntity = new Entity();
                var thinClientsCharacterControllerGhostEntity = new Entity();
                #region ThinClientCommandTarget
                state.EntityManager.SetComponentData(thinClientConnectionEntity, new CommandTarget { targetEntity = thinClientsCharacterControllerGhostEntity });
                #endregion

            }
        }
    }
}
