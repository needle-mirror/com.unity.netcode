// THIS FILE IS AUTO-GENERATED BY NETCODE PACKAGE SOURCE GENERATORS. DO NOT DELETE, MOVE, COPY, MODIFY, OR COMMIT THIS FILE.
// TO MAKE CHANGES TO THE SERIALIZATION OF A TYPE, REFER TO THE MANUAL.
using System.Text;
using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
using Unity.NetCode;
using Unity.NetCode.LowLevel.Unsafe;
#region __GHOST_USING_STATEMENT__
using __GHOST_USING__;
#endregion

#region __GHOST_END_HEADER__
#endregion
namespace __GHOST_NAMESPACE__
{
    [BurstCompile]
    [System.Runtime.CompilerServices.CompilerGenerated]
    [UpdateInGroup(typeof(GhostComponentSerializerCollectionSystemGroup))]
    [CreateAfter(typeof(GhostComponentSerializerCollectionSystemGroup))]
    [BakingVersion(true)]
    public struct GhostComponentSerializerRegistrationSystem : ISystem, IGhostComponentSerializerRegistration
    {
        /// <summary>TODO - Not currently burst compiled due to statics in GhostComponentSerializer.State.</summary>
        /// <param name="state"></param>
        public void OnCreate(ref SystemState state)
        {
            // Manual query as `SystemAPI.GetSingletonRW<GhostComponentSerializerCollectionData>()` is throwing "fail to compile" errors.
            using var builder = new EntityQueryBuilder(Allocator.Temp).WithAllRW<GhostComponentSerializerCollectionData>();
            using var query = state.EntityManager.CreateEntityQuery(builder);
            ref var data = ref query.GetSingletonRW<GhostComponentSerializerCollectionData>().ValueRW;

            ComponentTypeSerializationStrategy ss = default;
            #region __GHOST_SERIALIZATION_STRATEGY_LIST__
            ss = new ComponentTypeSerializationStrategy
            {
                DisplayName = "__GHOST_VARIANT_DISPLAY_NAME__",
                Component = ComponentType.ReadWrite<__GHOST_COMPONENT_TYPE__>(),
                Hash = __GHOST_VARIANT_HASH__,
                SelfIndex = -1,
                SerializerIndex = -1,
                PrefabType = __GHOST_PREFAB_TYPE__,
                SendTypeOptimization = __GHOST_SEND_MASK__,
                SendForChildEntities = __GHOST_SEND_CHILD_ENTITY__,
                IsDefaultSerializer = __GHOST_IS_DEFAULT_SERIALIZER__,
                IsInputComponent = __TYPE_IS_INPUT_COMPONENT__,
                IsInputBuffer = __TYPE_IS_INPUT_BUFFER__,
                IsTestVariant = __TYPE_IS_TEST_VARIANT__,
                HasDontSupportPrefabOverridesAttribute = __TYPE_HAS_DONT_SUPPORT_PREFAB_OVERRIDES_ATTRIBUTE__,
                HasSupportsPrefabOverridesAttribute = __TYPE_HAS_SUPPORTS_PREFAB_OVERRIDES_ATTRIBUTE__,
            };
            data.AddSerializationStrategy(ref ss);
            #endregion

            #region __GHOST_COMPONENT_LIST__
            data.AddSerializer(__GHOST_NAME__GhostComponentSerializer.GetState(ref state));
            #endregion

            #region __GHOST_INPUT_COMPONENT_LIST__
            data.AddInputComponent(ComponentType.ReadWrite<__GHOST_COMPONENT_TYPE__>(), ComponentType.ReadWrite<__GHOST_INPUT_BUFFER_COMPONENT_TYPE__>());
            #endregion
        }

        /// <summary>Ignore. Disables the system.</summary>
        /// <param name="state"></param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Enabled = false;
        }
    }
}
