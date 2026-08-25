using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.NetCode
{
    // This runs only on client, we don't want to affect the server simulation. Same as transform syncing where the entity to GO only happens client side
    // We need to set shouldAutoWake to false before any transform updates
    // Group ordering (GhostGameObjectSystemGroup is OrderLast in GhostSimulationSystemGroup)
    // guarantees we run after GhostGameObjectSpawnSystem
    [UpdateInGroup(typeof(GhostGameObjectSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateBefore(typeof(GhostObjectEntityToGameObjectTransformSystem))] // TODO-release@entitiesIntegration once we have transformRef integration, this should update before GhostUpdateSystem? This way updating transforms from transformRef won't wake up rigidbodies for nothing?
    internal partial class GhostPrepareRigidbodySystem : GhostPrepareRigidbodySystemBase
    {
    }
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateBefore(typeof(PredictedGhostObjectEntityToGameObjectTransformSystem))]
    internal partial class PredictedGhostPrepareRigidbodySystem : GhostPrepareRigidbodySystemBase
    {
    }

    abstract partial class GhostPrepareRigidbodySystemBase : SystemBase
    {
        protected override void OnCreate()
        {
            if (World.IsHost())
            {
                Enabled = false;
                return;
            }
            RequireForUpdate<GhostRigidbodyData>();
        }

        protected override void OnUpdate()
        {
            var time = SystemAPI.GetSingleton<NetworkTime>();

            if (time.IsPartialTick)
                return;
            if (time.IsFirstPredictionTick)
            {
                // All ghosts that are not simulating are reset to kinematic on first tick. Then as ticks are progressing, we're reenabling ghosts
                // This needs to be changed only once per frame for all ghosts, not flip flop every single tick.
                // This system still executes every tick, but the ghosts that are iterated on here should be changed only once per frame.
                // TODO-next@physics have batch operation to set isKinematic for all ghosts at once
                // Thing is, you don't want to do partial physics simulation at all... you don't want an object to hit against a kinematic other object when the other is supposed to move... this is mostly "just in case" where users' setup doesn't allow to resim everything.
                foreach (var goTracker in SystemAPI.Query<GhostRigidbodyGameObjectTracker>().WithNone<Simulate>())
                {
                    Rigidbody rigidbody = goTracker.Rigidbody.Value;
                    rigidbody.isKinematic = true; // TODO-next@physicsSleepWake this potentially messes with sleep syncing. Unity's isKinematic wakes rigidbodies
                }
            }

            // foreach (var (data, goTracker) in SystemAPI.Query<GhostRigidbodyData, GhostRigidbodyGameObjectTracker>().WithAll<Simulate>())
            // {
            //     var rigidbody = goTracker.rigidbody;
            //     // rigidbody.shouldAutoWake = false; // to prevent transform pos changes from waking the rigidbody while we're moving things around // TODO-next@physicsSleepWake
            // }
        }
    }

    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)] // needs to be symmetrical to GO->Entity system. shouldn't restore in fixed group, since we'd be restoring the same state multiple times (since we backup only at the end of the prediction group
    [UpdateBefore(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedGhostObjectEntityToGameObjectTransformSystem))] // This is so we put things back to sleep after contacts were gained or lost
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    partial class ClientPredictedGhostObjectEntityToGameObjectRigidbodySystem : SystemBase
    {
        // General notes
        // As soon as you add a bit of lag, performance degrades. This is inherent to calling "simulate" multiple times per frame. And since we resimulate and physics is not deterministic, running resimulations leads to different results locally even without receiving a snapshot that'd invalidate this...
        // replaying multiple ticks every snapshot means you need to be super deterministic in your simulation, else you'll get jitter.
        // Replaying only on misprediction would mitigate this a bit (since it'd replay less often, you'd get bigger corrections instead of a bunch of tiny useless small ones)
        // This should be less of a problem once we have fuzzy equality checks in prediction.

        protected override void OnCreate()
        {
            if (World.IsHost())
            {
                Enabled = false;
                return;
            }

            RequireForUpdate<GhostRigidbodyData>();
        }

        // TODO-next@physics a lot of this update should be replaced with engine side full state overwrite. Will need to checkin with the physics team, see what's doable
        protected override void OnUpdate()
        {
            var time = SystemAPI.GetSingleton<NetworkTime>();
            if (time.IsPartialTick) return;

            // rigidbody ghost fields could have changed, we need to reapply them to the GO Rigidbody so they are propagated to PhysX
            // TODO-next@physics this should be done in one batch engine side.
            //      Updating GameObject rigidbody state every tick messes with internal sleep state. Since physics isn't stateless, PhysX assumes things. For example when setting a velocity, it'll remove a rigidbody from sleep candidates.

            var currentTickMinusOne = time.ServerTick;
            currentTickMinusOne.Decrement();
            // doing all the operations that could wake neighbours first to make sure any sleeping we do afterward doesn't get cancelled by subsequent iterations of the same loop by neighbors waking up
            foreach (var (data, goTracker, predictedGhost) in SystemAPI.Query<GhostRigidbodyData, GhostRigidbodyGameObjectTracker, PredictedGhost>().WithAll<Simulate>())
            {
                Rigidbody rigidbody = goTracker.Rigidbody.Value;

                // we only update the GO if we have outside data affecting it like backup or like snapshot. We don't want to touch it if unnecessary to avoid waking it up for nothing.
                // The assumption is that users should only use the GameObject API in the prediction update. The underlying ECS rigidbody component shouldn't be accessible.
                if (predictedGhost.PredictionStartTick == currentTickMinusOne)
                    rigidbody.isKinematic = data.IsKinematic; // this needs to run before velocity setting. Else the ghost won't get woken up if we set velocity while it's kinematic

                if (!data.IsKinematic) // getting warnings if setting velocity on kinematic rigidbody
                {
                    // TODO-next@physicsSleepWake We need a way to set a velocity that's non-zero, but that's under the sleep threshold (and so shouldn't awaken the rigidbody).
                    // PhysX only offers to not awaken when the velocity is zero. So we can have a scenario where a cube is settling down, with a small velocity
                    // (and so its wakeCount decreasing or even zero), making it a candidate for sleep. But setting velocity (an old velocity since we're rolling back)
                    // that's non zero (like 0.0001 < sleepThreshold) will remove that rigidbody from being a sleep candidate, even with a wakeCounter set to 0.
                    // The rigidbody will go to sleep only the tick after.
                    //      We can't artificially set velocity to 0 if we're under the sleep threshold, cause then the rb won't wake up if it's in the air and needs to fall
                    if (math.length(data.LinearVelocity) > 0f || math.length(data.AngularVelocity) > 0f)
                    {
                        // this action wakes up the rigidbody
                        if (!data.LinearVelocity.Equals(rigidbody.linearVelocity)) // can't use Vector3 equal check, it has an epsilon. Need to use float3's.
                        {
                            rigidbody.linearVelocity = data.LinearVelocity;
                        }

                        if (!data.AngularVelocity.Equals(rigidbody.angularVelocity))
                        {
                            rigidbody.angularVelocity = data.AngularVelocity;
                        }
                    }

                    {
                        // TODO-next@physics when we sync forces, this should be done here
                    }
                }
            }

            // apply synced data in second step after reset, in case velocity set wakes neighbors
            foreach (var (data, goTracker) in SystemAPI.Query<GhostRigidbodyData, GhostRigidbodyGameObjectTracker>().WithAll<Simulate>())
            {
                if (!data.IsKinematic)
                {
                    // above velocity change can change wakecounter on neighbors too. Doing wake counter all in one go here to make sure this is consistent even for touching ghosts
                    // else we'd have a situation where ghostA and ghostB are touching, where we set wakeCounter for ghostA to 0, but then set the velocity for ghostB which would wake up neighbors, resetting ghostA's wakeCounter.
                    Rigidbody rigidbody = goTracker.Rigidbody.Value;
                    // if (data.WakeCounter > 0 && data.WakeCounter != rigidbody.wakeCounter) rigidbody.wakeCounter = data.WakeCounter; // TODO-next@physicsSleepWake
                }
            }

            foreach (var (data, goTracker) in SystemAPI.Query<GhostRigidbodyData, GhostRigidbodyGameObjectTracker>().WithAll<Simulate>())
            {
                Rigidbody rigidbody = goTracker.Rigidbody.Value;
                if (!data.IsKinematic) // getting warnings if setting velocity on kinematic rigidbody
                {
                    if (math.length(data.LinearVelocity) <= 0f && math.length(data.AngularVelocity) <= 0f)
                    {
                        if (!data.LinearVelocity.Equals(rigidbody.linearVelocity) && rigidbody.linearVelocity.magnitude > 0f)
                        {
                            // TODO-next@physicsSleepWake the problem here is if you have a wakeCounter to 0 and a velocity < sleep threshold but > 0, you'll still be removed from sleep candidates since we're setting velocity to a non-zero value. This behaviour is different client/server side when rolling back and so creates small misprediction when objects are about to sleep.
                            rigidbody.linearVelocity = data.LinearVelocity;
                        }

                        if (!data.AngularVelocity.Equals(rigidbody.angularVelocity) && rigidbody.angularVelocity.magnitude > 0f)
                        {
                            rigidbody.angularVelocity = data.AngularVelocity;
                        }
                    }

                    // rigidbody.AddForce(data.accumulatedForce, ForceMode.Force);
                    // rigidbody.AddTorque(data.accumulatedTorque, ForceMode.Force);
                    if (data.IsSleeping && !rigidbody.IsSleeping())
                        rigidbody.Sleep();
                    // if (data.WakeCounter <= 0 && data.WakeCounter != rigidbody.wakeCounter) rigidbody.wakeCounter = data.WakeCounter; // TODO-next@physicsSleepWake
                }
            }
        }
    }
}
