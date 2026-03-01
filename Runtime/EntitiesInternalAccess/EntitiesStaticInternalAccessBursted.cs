using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace Unity.NetCode.EntitiesInternalAccess
{
    /// <summary>
    /// In order to do some GhostField operations more efficiently and with a better UX for users, we need to use some internal ECS methods.
    /// WARNING for whoever adds methods here. Please consult with the entities team before doing so and make sure they review, as those APIs were not meant
    /// to be used outside the team.
    /// </summary>
    [BurstCompile]
    internal static unsafe class EntitiesStaticInternalAccessBursted
    {
        /// <summary>
        /// WARNING: only use writable pointers locally, don't cache them (since we want to bump change versions on each change).
        /// </summary>
        /// <param name="world"></param>
        /// <param name="entity"></param>
        /// <param name="typeIndex"></param>
        /// <returns></returns>
        [BurstCompile]
        public static void* GetComponentDataRawRW(ref WorldUnmanaged world, in Entity entity, in TypeIndex typeIndex)
        {
            return world.EntityManager.GetComponentDataRawRW(entity, typeIndex);
        }
        /// <summary>
        /// It's ok to cache read pointers, as long as we do a structural change check and race condition check first
        /// </summary>
        /// <param name="world"></param>
        /// <param name="entity"></param>
        /// <param name="typeIndex"></param>
        /// <returns></returns>
        [BurstCompile]
        public static void* GetComponentDataRawRO(ref WorldUnmanaged world, in Entity entity, in TypeIndex typeIndex)
        {
            return world.EntityManager.GetComponentDataRawRO(entity, typeIndex);
        }

        // Need internal access to have a non-generic version of the API.
        // If you're ok with generics, please use the public one
        public static void CompleteDependencyBeforeROExtension(this EntityManager self, TypeIndex typeIndex)
        {
            self.GetUncheckedEntityDataAccess()->DependencyManager->CompleteWriteDependency(typeIndex);
        }
        // Need internal access to have a non-generic version of the API.
        // If you're ok with generics, please use the public one
        public static void CompleteDependencyBeforeRWExtension(this EntityManager self, TypeIndex typeIndex)
        {
            self.GetUncheckedEntityDataAccess()->DependencyManager->CompleteReadAndWriteDependency(typeIndex);
        }

        // In order to know outside of systems whether a given type has any jobs writing to it.
        public static JobHandle GetDependencyForType(EntityManager em, TypeIndex typeIndex, bool readOnly)
        {
            var access = em.GetCheckedEntityDataAccess();
            var deps = access->DependencyManager;
            return deps->GetDependency(&typeIndex, readOnly ? 1 : 0, &typeIndex, readOnly ? 0 : 1, false);
        }
    }
}
