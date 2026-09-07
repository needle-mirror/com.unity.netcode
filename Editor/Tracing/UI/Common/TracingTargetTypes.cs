using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace Unity.Netcode.Editor.Tracing.UI
{
    internal enum TracingTargetKind : byte
    {
        System = 0,
        Component = 1
    }

    internal enum ComponentDataShapeKind : byte
    {
        NotApplicable = 0,
        Data = 1,
        Tag = 2
    }

    /// <summary>
    /// Reflection-based facts about candidate tracing target types — what counts as an ECS system or
    /// component, which systems run in the prediction loop, which worlds a system can exist in — shared by
    /// the target selection window, the toolbar filter and the tracing config translation.
    /// </summary>
    internal static class TracingTargetTypes
    {
        static readonly bool k_NetcodePhysicsPresent =
            Type.GetType("Unity.Netcode.PredictedPhysicsConfigSystem, Unity.NetCode.Physics") != null;

        /// <summary>By name because Unity.NetCode.Editor does not reference the optional Unity.Physics assembly.</summary>
        const string k_PhysicsSystemGroupAssemblyQualifiedName = "Unity.Physics.Systems.PhysicsSystemGroup, Unity.Physics";

        static readonly Dictionary<Type, bool> s_IsPredictionSystemCache = new();
        static HashSet<Type> s_NetcodePhysicsMovedFixedStepSystems;

        /// <summary>
        /// True for managed <see cref="ComponentSystemBase"/> systems and concrete unmanaged <see cref="ISystem"/>
        /// types, matching what the selection window collects and how persisted tracing targets are resolved.
        /// </summary>
        internal static bool IsTracingEcsSystemType(Type type) =>
            type != null && (
                (typeof(ComponentSystemBase).IsAssignableFrom(type) && type.IsClass)
                || (typeof(ISystem).IsAssignableFrom(type) && type.IsValueType && !type.IsAbstract));

        internal static bool IsUnmanagedComponentType(Type type) => type is { IsValueType: true };

        /// <summary>
        /// Tag vs data classification matches the Entities manual: unmanaged <see cref="IComponentData"/> with no
        /// instance fields is a tag; managed (class) components are always treated as data.
        /// See https://docs.unity3d.com/Packages/com.unity.entities@6.5/manual/components-tag.html
        /// </summary>
        internal static ComponentDataShapeKind GetComponentDataShape(Type type)
        {
            if (!typeof(IComponentData).IsAssignableFrom(type) || type.IsInterface)
            {
                return ComponentDataShapeKind.NotApplicable;
            }

            if (type.IsClass || type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0)
            {
                return ComponentDataShapeKind.Data;
            }

            return ComponentDataShapeKind.Tag;
        }

        internal static string TypeKey(Type type) => !string.IsNullOrEmpty(type.AssemblyQualifiedName) ? type.AssemblyQualifiedName : type.FullName;

        static WorldSystemFilterFlags GetWorldFilterFlags(Type type)
        {
            var visited = new List<Type>();
            var systemFlags = WorldSystemFilterFlags.Default;
            if (Attribute.IsDefined(type, typeof(WorldSystemFilterAttribute), true))
            {
                systemFlags = type.GetCustomAttribute<WorldSystemFilterAttribute>(true).FilterFlags;
            }
            if ((systemFlags & WorldSystemFilterFlags.Default) != 0)
            {
                systemFlags &= ~WorldSystemFilterFlags.Default;
                visited.Clear();
                systemFlags |= GetParentGroupDefaultFilterFlagsForDisplay(type, visited);
            }

            if (Attribute.IsDefined(type, typeof(ExecuteAlways)))
            {
                systemFlags |= WorldSystemFilterFlags.Editor;
            }

            return systemFlags;
        }

        /// <summary>
        /// True when resolved <see cref="WorldSystemFilterFlags"/> allow <see cref="ClientServerBootstrap"/> to
        /// create the system in a netcode world: server worlds use <c>ServerSimulation</c>, client worlds
        /// <c>ClientSimulation | Presentation</c>. Systems without any <see cref="WorldSystemFilterAttribute"/>
        /// resolve to LocalSimulation|ServerSimulation|ClientSimulation and therefore run in both. Only systems
        /// that can never exist in those worlds are excluded: editor/baking, local-only and thin-client-only.
        /// </summary>
        internal static bool SystemHasClientOrServerWorldFilter(Type systemType) =>
            (GetWorldFilterFlags(systemType)
                & (WorldSystemFilterFlags.ServerSimulation
                    | WorldSystemFilterFlags.ClientSimulation
                    | WorldSystemFilterFlags.Presentation)) != 0;

        /// <summary>
        /// True for systems that run in the prediction loop — the only ones tracing can capture:
        /// <see cref="PredictedSimulationSystemGroup"/> itself, any system whose transitive
        /// <see cref="UpdateInGroupAttribute"/> chain reaches it, systems netcode itself injects into the loop at
        /// world creation (<see cref="PredictedGhostSpawnSystem"/>, statically a GhostSpawnSystemGroup member),
        /// and, when netcode physics is installed, the fixed-step systems <c>PredictedPhysicsConfigSystem</c>
        /// reparents into the prediction loop (see <see cref="GetNetcodePhysicsMovedFixedStepSystems"/>).
        /// </summary>
        internal static bool IsPredictionSystem(Type systemType)
        {
            if (systemType == null)
            {
                return false;
            }

            if (s_IsPredictionSystemCache.TryGetValue(systemType, out var cached))
            {
                return cached;
            }

            // Seed so UpdateInGroup cycles terminate (cycles are reported by the world-flags walker).
            s_IsPredictionSystemCache[systemType] = false;

            var result = systemType == typeof(PredictedSimulationSystemGroup)
                || systemType == typeof(PredictedGhostSpawnSystem)
                || (k_NetcodePhysicsPresent && GetNetcodePhysicsMovedFixedStepSystems().Contains(systemType));
            if (!result)
            {
                foreach (var updateInGroup in systemType.GetCustomAttributes<UpdateInGroupAttribute>(true))
                {
                    if (IsPredictionSystem(updateInGroup.GroupType))
                    {
                        result = true;
                        break;
                    }
                }
            }

            s_IsPredictionSystemCache[systemType] = result;
            return result;
        }

        /// <summary>
        /// Mirrors the runtime move done by <c>PredictedPhysicsConfigSystem</c> / <c>MovePhysicsSystemUtilities</c>:
        /// starting from <c>PhysicsSystemGroup</c>, every direct member of
        /// <see cref="FixedStepSimulationSystemGroup"/> whose <see cref="UpdateBeforeAttribute"/> /
        /// <see cref="UpdateAfterAttribute"/> targets an already-moved system is reparented into the prediction
        /// loop at world creation, until a fixpoint. Systems nested inside a moved group are covered by the
        /// <see cref="UpdateInGroupAttribute"/> walk in <see cref="IsPredictionSystem"/>.
        /// </summary>
        static HashSet<Type> GetNetcodePhysicsMovedFixedStepSystems()
        {
            if (s_NetcodePhysicsMovedFixedStepSystems != null)
            {
                return s_NetcodePhysicsMovedFixedStepSystems;
            }

            var moved = new HashSet<Type>();
            s_NetcodePhysicsMovedFixedStepSystems = moved;
            var physicsGroup = Type.GetType(k_PhysicsSystemGroupAssemblyQualifiedName);
            if (physicsGroup == null)
            {
                return moved;
            }

            moved.Add(physicsGroup);

            var candidates = new List<Type>();
            void CollectDirectFixedStepGroupMembers(IEnumerable<Type> systemTypes)
            {
                foreach (var type in systemTypes)
                {
                    if (!IsTracingEcsSystemType(type) || type.IsAbstract)
                    {
                        continue;
                    }

                    foreach (var updateInGroup in type.GetCustomAttributes<UpdateInGroupAttribute>(true))
                    {
                        if (updateInGroup.GroupType == typeof(FixedStepSimulationSystemGroup))
                        {
                            candidates.Add(type);
                            break;
                        }
                    }
                }
            }

            CollectDirectFixedStepGroupMembers(TypeCache.GetTypesDerivedFrom<ComponentSystemBase>());
            CollectDirectFixedStepGroupMembers(TypeCache.GetTypesDerivedFrom<ISystem>());

            var didMove = true;
            while (didMove)
            {
                didMove = false;
                foreach (var candidate in candidates)
                {
                    if (moved.Contains(candidate) || !HasOrderingConstraintAgainstAny(candidate, moved))
                    {
                        continue;
                    }

                    moved.Add(candidate);
                    didMove = true;
                }
            }

            return moved;
        }

        static bool HasOrderingConstraintAgainstAny(Type systemType, HashSet<Type> targets)
        {
            foreach (var attribute in systemType.GetCustomAttributes(true))
            {
                var target = attribute switch
                {
                    UpdateBeforeAttribute before => before.SystemType,
                    UpdateAfterAttribute after => after.SystemType,
                    _ => null
                };

                if (target != null && targets.Contains(target))
                {
                    return true;
                }
            }

            return false;
        }

        static WorldSystemFilterFlags GetParentGroupDefaultFilterFlagsForDisplay(Type type, List<Type> visitedSystemGroupsList)
        {
            if (!Attribute.IsDefined(type, typeof(UpdateInGroupAttribute), true))
            {
                return WorldSystemFilterFlags.LocalSimulation
                    | WorldSystemFilterFlags.ServerSimulation
                    | WorldSystemFilterFlags.ClientSimulation;
            }

            WorldSystemFilterFlags systemFlags = default;
            foreach (var updateInGroupAttribute in type.GetCustomAttributes<UpdateInGroupAttribute>(true))
            {
                var groupType = updateInGroupAttribute.GroupType;
                if (visitedSystemGroupsList.Contains(groupType))
                {
                    var sb = new StringBuilder();
                    sb.Append("The following systems form a cycle in their UpdateInGroup attributes: ");
                    var cycleStart = visitedSystemGroupsList.IndexOf(groupType);
                    for (var i = cycleStart; i < visitedSystemGroupsList.Count; i++)
                    {
                        sb.Append($"{visitedSystemGroupsList[i]} -> ");
                    }

                    sb.Append($"{groupType}");
                    Debug.LogError(sb.ToString());
                    continue;
                }

                visitedSystemGroupsList.Add(groupType);
                var groupFlags = WorldSystemFilterFlags.Default;
                if (Attribute.IsDefined(groupType, typeof(WorldSystemFilterAttribute), true))
                {
                    groupFlags = groupType.GetCustomAttribute<WorldSystemFilterAttribute>(true).ChildDefaultFilterFlags;
                }
                if ((groupFlags & WorldSystemFilterFlags.Default) != 0)
                {
                    groupFlags &= ~WorldSystemFilterFlags.Default;
                    groupFlags |= GetParentGroupDefaultFilterFlagsForDisplay(groupType, visitedSystemGroupsList);
                }

                visitedSystemGroupsList.RemoveAt(visitedSystemGroupsList.Count - 1);
                systemFlags |= groupFlags;
            }

            return systemFlags;
        }

        static readonly (WorldSystemFilterFlags flag, string label)[] k_WorldFlagLabels =
        {
            (WorldSystemFilterFlags.ServerSimulation, "Server"),
            (WorldSystemFilterFlags.ClientSimulation, "Client"),
            (WorldSystemFilterFlags.ThinClientSimulation, "Thin client"),
            (WorldSystemFilterFlags.Presentation, "Presentation"),
            (WorldSystemFilterFlags.LocalSimulation, "Local"),
            (WorldSystemFilterFlags.BakingSystem, "Baking"),
            (WorldSystemFilterFlags.Editor, "Editor"),
            (WorldSystemFilterFlags.Streaming, "Streaming"),
        };

        /// <summary>The worlds a system can run in, as shown in the World column and matched by search.</summary>
        internal static string WorldDisplayText(Type systemType)
        {
            var flags = GetWorldFilterFlags(systemType);
            var parts = new List<string>();
            foreach (var (flag, label) in k_WorldFlagLabels)
            {
                if ((flags & flag) != 0)
                {
                    parts.Add(label);
                }
            }

            if (parts.Count > 0)
            {
                return string.Join(", ", parts);
            }

            return (flags & WorldSystemFilterFlags.Disabled) != 0 ? "Disabled" : "—";
        }
    }
}
