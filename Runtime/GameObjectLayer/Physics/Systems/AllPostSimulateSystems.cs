using Unity.Entities;
using Unity.Netcode.NetcodeTime;
using UnityEngine;

namespace Unity.Netcode
{
    abstract partial class GhostRigidbodyGameObjectToEntitySyncSystemBase : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<GhostRigidbodyData>();
        }

        protected override void OnUpdate()
        {
            var time = SystemAPI.GetSingleton<NetworkTime>();
            if (time.IsPartialTick)
                return;
            // All of this is done as late as possible in the update loop to give time to other gameplay code to change this and still be saved
            // We save the simulation results all at once, without touching the rigidbody. PhysX is very delicate and sleep/velocity/etc state can change with simple position changes.
            foreach(var (dataRef, goTracker) in SystemAPI.Query<RefRW<GhostRigidbodyData>, GhostRigidbodyGameObjectTracker>().WithAll<Simulate, PredictedGhost>())
            {
                Rigidbody rigidbody = goTracker.Rigidbody.Value;

                // TODO-next@physics the below should happen engine side. Should have a batch apply, reading from physX new velocity and writing directly to chunk data.
                ref var data = ref dataRef.ValueRW;
                data.AngularVelocity = rigidbody.angularVelocity;
                data.LinearVelocity = rigidbody.linearVelocity;
                data.IsKinematic = rigidbody.isKinematic;
                // data.WakeCounter = rigidbody.wakeCounter; // TODO-next@physicsSleepWake
                data.IsSleeping = rigidbody.IsSleeping();

                // We need to make sure we don't touch or reset the rigidbody after this, as the above is going to be used by debug rendering later in the frame.
            }

            if (!World.IsServer())
            {
                if (time.IsFinalPredictionTick)
                {
                    // at the end, we reset everyone to their original kinematic status. This way editor tooling reports the right state. This applies
                    // to non-simulate entities as well.
                    foreach (var (rbData, goTracker) in SystemAPI.Query<GhostRigidbodyData, GhostRigidbodyGameObjectTracker>())
                    {
                        goTracker.Rigidbody.Value.isKinematic = rbData.IsKinematic; // this can wake things up
                    }
                }
            }
        }
    }

    // Copy game object rigidbody to entities on the server so they are sent
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast=true)] // UpdateBefore is not enough, OrderLast is required to be as close to the send as possible, to give time for the GO to update its transform
    // Need to execute before GhostSendSystem and GhostObjectGameObjectToEntityTransformSystem. No UpdateBefore needed right now, but if we move this system
    // this needs to be taken into account
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class ServerGhostObjectGameObjectToEntityRigidbodySystem : GhostRigidbodyGameObjectToEntitySyncSystemBase
    {}

    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostBehaviourPredictionSystem))]
    [UpdateBefore(typeof(PredictedGhostObjectGameObjectToEntityTransformSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class ClientPredictedGhostObjectGameObjectToEntityRigidbodySystem : GhostRigidbodyGameObjectToEntitySyncSystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            if (World.IsHost())
            {
                Enabled = false;
                return;
            }
        }
    }
}
