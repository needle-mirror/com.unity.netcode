#if ENABLE_UNITY_NETCODE_PHYSICS
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace DocumentationCodeSamples
{
    public struct LagCompensationEnabled : IComponentData { }

    public struct Character : IComponentData { }

    public struct Hit : IComponentData
    {
        public Entity Victim;
        public NetworkTick Tick;
    }

    public struct CharacterControllerPlayerInput : IInputComponentData
    {
        [GhostField] public InputEvent Jump;
        [GhostField] public InputEvent PrimaryFire;
        [GhostField] public InputEvent SecondaryFire;
    }

    partial class ServerRewind : SystemBase
    {
#pragma warning disable CS0618 // Disable Aspects obsolete warnings
        public readonly partial struct CharacterAspect : IAspect
        {
            public readonly Entity Self;
            readonly RefRO<CharacterControllerPlayerInput> m_Input;
            public CharacterControllerPlayerInput Input => m_Input.ValueRO;
        }
#pragma warning restore CS0618

        protected override void OnUpdate()
        {
            var rayInput = new RaycastInput
            {
                Start = new float3(),
                End = new float3(),
                Filter = CollisionFilter.Default,
            };
            #region Logic
            var collisionHistory = SystemAPI.GetSingleton<PhysicsWorldHistorySingleton>();
            var physicsWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld;
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var lagCompensationEnabledFromEntity = SystemAPI.GetComponentLookup<LagCompensationEnabled>();
            var predictingTick = networkTime.ServerTick;
            // Do not perform hit-scan when rolling back, only when simulating the latest tick
            if (!networkTime.IsFirstTimeFullyPredictingTick)
                return;

            foreach (var (character, interpolationDelay, hitComponent) in SystemAPI.Query<CharacterAspect, RefRO<CommandDataInterpolationDelay>, RefRW<Hit>>().WithAll<Simulate>())
            {
                if (character.Input.SecondaryFire.IsSet)
                {
                    hitComponent.ValueRW.Victim = character.Self;
                    hitComponent.ValueRW.Tick = predictingTick;
                    continue;
                }
                if (!character.Input.PrimaryFire.IsSet)
                {
                    continue;
                }

                // When we fetch the CollisionWorld for ServerTick T, we need to account for the fact that the user
                // raised this input sometime on the previous tick (render-frame, technically).
                const int additionalRenderDelay = 1;

                // Breakdown of timings:
                // - On the client, predicting ServerTick: 100 (for example)
                // - InterpolationDelay: 2 ticks
                // - Rendering Latency (assumption): 1 tick (likely more than 1 due to: double/triple buffering, pipelining, monitor refresh & draw latency)
                // - Client visually sees 97 (-1 for render latency, -2 for lag compensation)
                // - CommandDataInterpolationTick.Delay is a delta between CurrentCommand.Tick vs InterpolationTick, thus -2.
                //   I.e. InterpolationDelay is already accounted for.
                // - On the server, we process this input on ServerTick:100.
                // - CommandDataInterpolationTick.Delay:-2 = 98 (-2)
                // - So the server also needs to subtract the rendering delay to be consistent with what the client sees and queries against (97).
                var delay = lagCompensationEnabledFromEntity.HasComponent(character.Self)
                    ? interpolationDelay.ValueRO.Delay + additionalRenderDelay
                    : additionalRenderDelay;

                collisionHistory.GetCollisionWorldFromTick(predictingTick, delay, ref physicsWorld, out var collWorld, out var expectedTick, out var returnedTick);

                bool hit = collWorld.CastRay(rayInput, out var closestHit);
            }
            #endregion
        }
    }
}
#endif
