using System;
using System.Collections.Generic;
using Unity.Netcode.Tracing;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor.Tracing.UI.TickInspector
{
    /// <summary>
    /// Builds the heterogeneous tree (SystemGroup -> System -> Ghost -> Component) from the per-tick
    /// <see cref="ManagedSystemData"/>. Pure, so the tree shape is unit-testable.
    /// </summary>
    static class TickInspectorTreeBuilder
    {
        // Disjoint high ranges so ghost/component ids never collide with the (small) system execution-order ids.
        public const int GhostIdBase = 1 << 28;
        public const int ComponentIdBase = 1 << 29;

        // Missing (server-only) system rows keep their server execution order
        public const int MissingSystemIdBase = 1 << 27;

        // Keys a server component value by system + ghost + component type, for pairing with the client side.
        public readonly struct ServerComponentKey : IEquatable<ServerComponentKey>
        {
            public readonly string SystemName;
            public readonly int GhostId;
            public readonly Type ComponentType;

            public ServerComponentKey(string systemName, int ghostId, Type componentType)
            {
                SystemName = systemName;
                GhostId = ghostId;
                ComponentType = componentType;
            }

            public bool Equals(ServerComponentKey other) =>
                SystemName == other.SystemName && GhostId == other.GhostId && ComponentType == other.ComponentType;
            public override bool Equals(object obj) => obj is ServerComponentKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(SystemName, GhostId, ComponentType);
        }

        public struct Result
        {
            public List<TreeViewItemData<TickInspectorNode>> Roots;
            // Rows to auto-expand: only those whose subtree contains a diff.
            public List<int> ItemsToExpand;
        }

        // Reasons no system/ghost/component row can carry surface in the tick metadata foldout, not here.
        public static Result Build(IReadOnlyList<ManagedSystemData> systems,
            IReadOnlyDictionary<ServerComponentKey, TracedComponent> serverValues = null,
            DiffInfo.DiffReasons enabledReasons = DiffInfo.AllDiffReasons,
            float fuzzyDiffThreshold = 0f)
        {
            var roots = new List<TreeViewItemData<TickInspectorNode>>();
            var itemsToExpand = new List<int>();
            var nextChildId = 0;

            if (systems == null)
                return new Result { Roots = roots, ItemsToExpand = itemsToExpand };

            var childrenByParent = new Dictionary<int, List<ManagedSystemData>>();
            foreach (var s in systems)
            {
                if (!childrenByParent.TryGetValue(s.ParentExecutionOrder, out var list))
                    childrenByParent[s.ParentExecutionOrder] = list = new List<ManagedSystemData>();
                list.Add(s);
            }

            if (childrenByParent.TryGetValue(WorldDataManagedExtensions.NoParentExecutionOrder, out var rootData))
            {
                var ancestors = new HashSet<int>();
                foreach (var r in rootData)
                    roots.Add(BuildTreeItem(r, childrenByParent, ancestors, itemsToExpand, serverValues, enabledReasons, fuzzyDiffThreshold, ref nextChildId, out _));
            }

            return new Result { Roots = roots, ItemsToExpand = itemsToExpand };
        }

        // A row is highlighted when it has a diff whose reasons intersect the enabled mask.
        static bool IsHighlighted(bool hasDiff, DiffInfo.DiffReasons flags, DiffInfo.DiffReasons enabledReasons)
            => hasDiff && (flags == DiffInfo.DiffReasons.Undefined || (flags & enabledReasons) != 0);

        // ancestors guards against cycles / duplicate execution orders. subtreeHasDiff = this node or
        // anything below it has a (filter-passing) diff, used to drive auto-expand up to ancestor groups.
        static TreeViewItemData<TickInspectorNode> BuildTreeItem(ManagedSystemData systemData, Dictionary<int, List<ManagedSystemData>> childrenByParent, HashSet<int> ancestors, List<int> itemsToExpand, IReadOnlyDictionary<ServerComponentKey, TracedComponent> serverValues, DiffInfo.DiffReasons enabledReasons, float fuzzyDiffThreshold, ref int nextChildId, out bool subtreeHasDiff)
        {
            var children = new List<TreeViewItemData<TickInspectorNode>>();
            var systemHighlighted = IsHighlighted(systemData.HasDiff, systemData.DiffReasonFlags, enabledReasons);
            subtreeHasDiff = systemHighlighted;
            var subtreeReasons = systemData.RowDiffReasonFlags;

            if (childrenByParent.TryGetValue(systemData.ExecutionOrder, out var childData))
            {
                ancestors.Add(systemData.ExecutionOrder);
                foreach (var c in childData)
                {
                    if (ancestors.Contains(c.ExecutionOrder))
                        continue;
                    var childItem = BuildTreeItem(c, childrenByParent, ancestors, itemsToExpand, serverValues, enabledReasons, fuzzyDiffThreshold, ref nextChildId, out var childHasDiff);
                    children.Add(childItem);
                    subtreeHasDiff |= childHasDiff;
                    subtreeReasons |= childItem.data.InclusiveDiffReasonFlags;
                }
                ancestors.Remove(systemData.ExecutionOrder);
            }

            // Leaf systems only: groups also carry GhostComponents and would otherwise duplicate the ghosts.
            if (!systemData.IsSystemGroup && systemData.GhostComponents != null)
            {
                foreach (var ghostData in OrderGhosts(systemData.GhostComponents))
                {
                    var ghostItem = BuildGhostItem(ghostData, systemData.SystemType, systemData.SystemName, itemsToExpand, serverValues, enabledReasons, fuzzyDiffThreshold, systemData.FromServerWorld, ref nextChildId, out var ghostSubtreeHasDiff);
                    children.Add(ghostItem);
                    subtreeHasDiff |= ghostSubtreeHasDiff;
                    subtreeReasons |= ghostItem.data.InclusiveDiffReasonFlags;
                }
            }

            var node = new TickInspectorNode
            {
                Id = systemData.ExecutionOrder,
                NodeType = systemData.IsSystemGroup ? TickInspectorNodeType.SystemGroup : TickInspectorNodeType.System,
                DisplayName = systemData.SystemNameShort,
                HasDiff = systemHighlighted,
                DiffReasonFlags = systemData.RowDiffReasonFlags,
                InclusiveDiffReasonFlags = subtreeReasons,
                SelectedDiffReasons = enabledReasons,
                OwningSystemType = systemData.SystemType,
                FromClientWorld = !systemData.FromServerWorld,
            };

            if (subtreeHasDiff)
                itemsToExpand.Add(node.Id);

            return new TreeViewItemData<TickInspectorNode>(node.Id, node, children);
        }

        static TreeViewItemData<TickInspectorNode> BuildGhostItem(GhostComponentData ghostData, Type owningSystemType, string owningSystemName, List<int> itemsToExpand, IReadOnlyDictionary<ServerComponentKey, TracedComponent> serverValues, DiffInfo.DiffReasons enabledReasons, float fuzzyDiffThreshold, bool fromServerWorld, ref int nextChildId, out bool ghostSubtreeHasDiff)
        {
            var componentChildren = new List<TreeViewItemData<TickInspectorNode>>();
            var subtreeReasons = ghostData.RowDiffReasonFlags;
            var ghostFromServer = fromServerWorld || ghostData.FromServerWorld;
            var anyComponentHighlighted = false;
            if (ghostData.Components != null)
            {
                var ghostId = ghostData.EntityId.value.ghostId;
                foreach (var component in OrderComponents(ghostData.Components))
                {
                    // A missing (server-only) component row under a client system carries server data
                    var componentFromServer = ghostFromServer || component.Value.FromServerWorld;
                    var componentHighlighted = IsHighlighted(component.Value.HasDiff, component.Value.DiffReasonFlags, enabledReasons);
                    anyComponentHighlighted |= componentHighlighted;
                    subtreeReasons |= component.Value.RowDiffReasonFlags;
                    var componentNode = new TickInspectorNode
                    {
                        Id = ComponentIdBase + nextChildId++,
                        NodeType = TickInspectorNodeType.Component,
                        DisplayName = component.Key.Name,
                        HasDiff = componentHighlighted,
                        DiffReasonFlags = component.Value.RowDiffReasonFlags,
                        InclusiveDiffReasonFlags = component.Value.RowDiffReasonFlags,
                        SelectedDiffReasons = enabledReasons,
                        OwningSystemType = owningSystemType,
                        ComponentType = component.Key,
                        FromClientWorld = !componentFromServer,
                    };

                    // Every component owns a detail table; only a diffing one auto-expands (and shows red).
                    // Non-diffing values still matter: they may differ within the fuzzy threshold.
                    var tableNode = BuildTableNode(component.Key, component.Value, owningSystemName, ghostId, serverValues, componentFromServer, fuzzyDiffThreshold, ref nextChildId);
                    var tableChild = new List<TreeViewItemData<TickInspectorNode>> { new(tableNode.Id, tableNode) };
                    if (componentHighlighted)
                        itemsToExpand.Add(componentNode.Id);

                    componentChildren.Add(new TreeViewItemData<TickInspectorNode>(componentNode.Id, componentNode, tableChild));
                }
            }

            var ghostRowHighlighted = IsHighlighted(ghostData.HasDiff, ghostData.DiffReasonFlags, enabledReasons);
            ghostSubtreeHasDiff = ghostRowHighlighted || anyComponentHighlighted;
            var ghostNode = new TickInspectorNode
            {
                Id = GhostIdBase + nextChildId++,
                NodeType = TickInspectorNodeType.Ghost,
                DisplayName = ghostData.GhostName,
                HasDiff = ghostRowHighlighted,
                DiffReasonFlags = ghostData.RowDiffReasonFlags,
                InclusiveDiffReasonFlags = subtreeReasons,
                SelectedDiffReasons = enabledReasons,
                OwningSystemType = owningSystemType,
                GhostEntityId = ghostData.EntityId,
                FromClientWorld = !ghostFromServer,
            };

            if (ghostSubtreeHasDiff)
                itemsToExpand.Add(ghostNode.Id);

            return new TreeViewItemData<TickInspectorNode>(ghostNode.Id, ghostNode, componentChildren);
        }

        static TickInspectorNode BuildTableNode(Type componentType, TracedComponent tracedComponent, string owningSystemName, int ghostId, IReadOnlyDictionary<ServerComponentKey, TracedComponent> serverValues, bool fromServerWorld, float fuzzyDiffThreshold, ref int nextChildId)
        {
            // A missing (server-only) system's traces fill the server column and leave the client column empty
            var clientComponent = fromServerWorld ? default : tracedComponent;
            var serverComponent = fromServerWorld ? tracedComponent : default;
            if (!fromServerWorld)
                serverValues?.TryGetValue(new ServerComponentKey(owningSystemName, ghostId, componentType), out serverComponent);

            var detail = new MispredictionDetail
            {
                ComponentType = componentType,
                ClientBefore = clientComponent.BeforeValue,
                ClientAfter = clientComponent.AfterValue,
                ServerBefore = serverComponent.BeforeValue,
                ServerAfter = serverComponent.AfterValue,
                FuzzyDiffThreshold = fuzzyDiffThreshold,
            };

            return new TickInspectorNode
            {
                Id = ComponentIdBase + nextChildId++,
                NodeType = TickInspectorNodeType.MispredictionTable,
                Detail = detail,
                FromClientWorld = !fromServerWorld,
            };
        }

        // Stable row order — dictionaries enumerate non-deterministically.
        static List<GhostComponentData> OrderGhosts(Dictionary<int, GhostComponentData> ghosts)
        {
            var list = new List<GhostComponentData>(ghosts.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.GhostName, b.GhostName));
            return list;
        }

        static List<KeyValuePair<Type, TracedComponent>> OrderComponents(Dictionary<Type, TracedComponent> components)
        {
            var list = new List<KeyValuePair<Type, TracedComponent>>(components);
            list.Sort((a, b) => string.CompareOrdinal(a.Key.Name, b.Key.Name));
            return list;
        }
    }
}
