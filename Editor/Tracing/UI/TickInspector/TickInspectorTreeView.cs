using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Entities;
using Unity.NetCode.Tracing;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI.TickInspector
{
    /// <summary>
    /// The heterogeneous TreeView (SystemGroup -> System -> Ghost -> Component) inside the
    /// <see cref="TickInspectorView"/>.
    /// </summary>
    class TickInspectorTreeView : DataObserverView
    {
        protected override string UssClassName => TickInspectorUssClasses.TreeView;
        TreeView m_TreeView;
        FrameID m_SelectedFrameID = default(FrameID);
        TickID m_SelectedTickID = default(TickID);

        // Ids to auto-expand after (re)building (diff-bearing subtrees only).
        readonly List<int> m_ItemsToExpand = new();

        // Raised whenever the tree is (re)built
        public event Action<DiffInfo.DiffReasons> TickReasonsComputed;

        TracingViewFilters m_TracingViewFilters;

        // "Changed values only" is answered from the recorded aggregates — the same source the timeline
        // and row highlights use. Passing ghost/component narrows the scope the filter is evaluated for.
        bool ShouldApplyValueChangeFilter(Dictionary<DiffAggregate, float> aggregates, int ghostId = DiffAggregate.InvalidGhostId, TypeIndex component = default)
        {
            if (!m_TracingViewFilters.ChangedValuesOnly)
                return false;
            if (aggregates == null)
                return true;
            foreach (var aggregate in aggregates.Keys)
            {
                if (!aggregate.HasValueChanged)
                    continue;
                if (ghostId != DiffAggregate.InvalidGhostId && aggregate.GhostId != ghostId)
                    continue;
                if (component != default && aggregate.Component != component)
                    continue;
                return false;
            }
            return true;
        }

        public override VisualElement Create()
        {
            m_Root.AddToClassList(UssClassName);

            if (m_SelectedTickID.Equals(default))
            {
                return m_Root;
            }

            m_TreeView = new TreeView();
            // Misprediction detail tables make rows variable-height, so fixed-height virtualization won't do.
            m_TreeView.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
            m_TreeView.makeItem = () => new TickInspectorTreeViewItem();
            m_TreeView.bindItem = (element, i) =>
            {
                var item = (TickInspectorTreeViewItem)element;
                item.SetNode(m_TreeView.GetItemDataForIndex<TickInspectorNode>(i));
            };

            BuildTreeview();
            ApplyExpansion();

            m_Root.Add(m_TreeView);

            return m_Root;
        }


        void ApplyFiltersToComponents(Dictionary<DiffAggregate, float> aggregates, int ghostKey, GhostComponentData ghostComponent)
        {
            var componentsWithoutChanges =  new List<Type>();
            foreach (var (idx, clientTracedComponent) in ghostComponent.Components)
            {
                // Managed component values are never captured or diffed, so their rows never render
                if (!idx.IsValueType)
                {
                    componentsWithoutChanges.Add(idx);
                    continue;
                }

                var typeIndex = TypeManager.GetTypeIndex(idx);
                if (m_TracingViewFilters.IsComponentHidden(typeIndex))
                {
                    componentsWithoutChanges.Add(idx);
                    continue;
                }

                if (ShouldApplyValueChangeFilter(aggregates, ghostKey, typeIndex))
                    componentsWithoutChanges.Add(idx);
            }

            foreach (var type in componentsWithoutChanges)
                ghostComponent.Components.Remove(type);
        }


        void ApplyGhostComponentFilters(ManagedSystemData clientManagedData)
        {
            var ghostsToRemove = new HashSet<int>();

            foreach (var (ghostKey, ghostComponent) in clientManagedData.GhostComponents)
            {
                if (ShouldApplyValueChangeFilter(clientManagedData.DiffAggregates, ghostKey))
                {
                    ghostsToRemove.Add(ghostKey);
                    continue;
                }

                ApplyFiltersToComponents(clientManagedData.DiffAggregates, ghostKey, ghostComponent);

                if (ghostComponent.Components.Count == 0)
                    ghostsToRemove.Add(ghostKey);

            }

            foreach (var id in ghostsToRemove)
                clientManagedData.GhostComponents.Remove(id);
        }

        // Re-evaluates the system/ghost/component highlight flags from the stored aggregates and the active filters.
        ManagedSystemData ApplyReasonMasks(ManagedSystemData systemData)
        {
            if (m_TracingViewFilters == null)
                return systemData;

            var enabledReasons = m_TracingViewFilters.EnabledDiffReasons;
            systemData.DiffReasonFlags = systemData.RowDiffReasonFlags & enabledReasons;
            systemData.HasDiff = systemData.DiffReasonFlags != DiffInfo.DiffReasons.Undefined;

            if (systemData.GhostComponents == null)
                return systemData;

            var ghostKeys = new List<int>(systemData.GhostComponents.Keys);
            foreach (var ghostKey in ghostKeys)
            {
                var ghostData = systemData.GhostComponents[ghostKey];

                if (ghostData.Components != null)
                {
                    var componentTypes = new List<Type>(ghostData.Components.Keys);
                    foreach (var componentType in componentTypes)
                    {
                        var component = ghostData.Components[componentType];
                        component.RowDiffReasonFlags = m_TracingViewFilters.ApplyFuzzyThreshold(component.RowDiffReasonFlags, component.DataDiffAmount);
                        component.DiffReasonFlags = component.RowDiffReasonFlags & enabledReasons;
                        component.HasDiff = component.DiffReasonFlags != DiffInfo.DiffReasons.Undefined;
                        ghostData.Components[componentType] = component;
                    }
                }

                ghostData.DiffReasonFlags = ghostData.RowDiffReasonFlags & enabledReasons;
                ghostData.HasDiff = ghostData.DiffReasonFlags != DiffInfo.DiffReasons.Undefined;

                systemData.GhostComponents[ghostKey] = ghostData;
            }

            return systemData;
        }

        static bool TryFindSystemByName(List<ManagedSystemData> systems, string systemName, out ManagedSystemData found)
        {
            foreach (var data in systems)
            {
                if (data.SystemName == systemName)
                {
                    found = data;
                    return true;
                }
            }

            found = default;
            return false;
        }


        List<ManagedSystemData> GetSystemDataList(out DiffInfo.DiffReasons tickRowReasons)
        {
            List<ManagedSystemData> managedSystems = new();
            List<ManagedSystemData> serverManagedSystems = new();

            if (m_Data.ServerWorldData.PerTickData.TryGetValue(m_SelectedTickID, out _))
            {
                serverManagedSystems = m_Data.ServerWorldData.GetServerTickData(m_SelectedTickID).SystemsByExecutionOrder;
            }

            var clientTickData = m_Data.ClientWorldData.GetClientTickData(m_SelectedFrameID, m_SelectedTickID);
            tickRowReasons = clientTickData.TickRowDiffReasons;

            var hasServerTick = serverManagedSystems.Count > 0;
            foreach (var clientManagedSystemData in clientTickData.SystemsByExecutionOrder)
            {
                if (m_TracingViewFilters.IsSystemHidden(clientManagedSystemData.SystemName))
                    continue;

                var hasServerCounterpart = TryFindSystemByName(serverManagedSystems, clientManagedSystemData.SystemName, out var serverManagedSystemData);

                // A system without a server counterpart is only interesting when it diffed (e.g. ExtraSystem);
                // when the whole tick has no server trace, there is nothing to compare against, keep everything.
                if (hasServerTick && !hasServerCounterpart && !clientManagedSystemData.HasDiff)
                    continue;
                if (ShouldApplyValueChangeFilter(clientManagedSystemData.DiffAggregates))
                    continue;

                if (hasServerCounterpart)
                {
                    InjectMissingServerGhosts(clientManagedSystemData, serverManagedSystemData);
                    InjectMissingServerComponents(clientManagedSystemData, serverManagedSystemData);
                }
                ApplyGhostComponentFilters(clientManagedSystemData);
                var filteredSystemData = ApplyReasonMasks(clientManagedSystemData);
                managedSystems.Add(filteredSystemData);
            }

            AppendMissingServerSystems(clientTickData.SystemsByExecutionOrder, serverManagedSystems, managedSystems);

            return managedSystems;
        }

        // A ghost the server traced under this system but the client didn't gets its own row (flagged
        // MissingGhost by the diff), carrying the server values so its tables fill the server column and
        // show "No data" on the client side — the ghost-level mirror of the missing component/system rows.
        static void InjectMissingServerGhosts(ManagedSystemData clientSystemData, in ManagedSystemData serverSystemData)
        {
            if (clientSystemData.GhostComponents == null || serverSystemData.GhostComponents == null)
                return;

            foreach (var (ghostKey, serverGhost) in serverSystemData.GhostComponents)
            {
                if (clientSystemData.GhostComponents.ContainsKey(ghostKey)
                    || !HasMissingGhostAggregate(clientSystemData.DiffAggregates, ghostKey))
                    continue;

                var missingGhost = serverGhost;
                missingGhost.FromServerWorld = true;
                missingGhost.Components = new Dictionary<Type, TracedComponent>(serverGhost.Components?.Count ?? 0);
                if (serverGhost.Components != null)
                {
                    foreach (var (componentType, serverComponent) in serverGhost.Components)
                    {
                        var missingComponent = serverComponent;
                        missingComponent.FromServerWorld = true;
                        missingGhost.Components[componentType] = missingComponent;
                    }
                }

                clientSystemData.GhostComponents[ghostKey] = missingGhost;
            }
        }

        // Only rows the diff actually flagged: presence alone is not enough on ticks the diff skipped
        // (e.g. ignored partial ticks).
        static bool HasMissingGhostAggregate(Dictionary<DiffAggregate, float> aggregates, int ghostId)
        {
            if (aggregates == null)
                return false;

            foreach (var aggregate in aggregates.Keys)
            {
                if ((aggregate.Reasons & DiffInfo.DiffReasons.MissingGhost) != 0 && aggregate.GhostId == ghostId)
                    return true;
            }

            return false;
        }

        // A component the server traced on a ghost but the client didn't gets its own row under the
        // client ghost (flagged MissingComponent by the diff), carrying the server values — the
        // component-level mirror of the missing-system rows below. Injected before the component
        // filters so hidden/managed/changed-values rules apply to it like any other row.
        static void InjectMissingServerComponents(ManagedSystemData clientSystemData, in ManagedSystemData serverSystemData)
        {
            if (clientSystemData.GhostComponents == null || serverSystemData.GhostComponents == null)
                return;

            foreach (var (ghostKey, clientGhost) in clientSystemData.GhostComponents)
            {
                if (clientGhost.Components == null
                    || !serverSystemData.GhostComponents.TryGetValue(ghostKey, out var serverGhost)
                    || serverGhost.Components == null)
                    continue;

                foreach (var (componentType, serverComponent) in serverGhost.Components)
                {
                    if (clientGhost.Components.ContainsKey(componentType)
                        || !HasMissingComponentAggregate(clientSystemData.DiffAggregates, ghostKey, componentType))
                        continue;

                    var missingComponent = serverComponent;
                    missingComponent.FromServerWorld = true;
                    clientGhost.Components[componentType] = missingComponent;
                }
            }
        }

        // Only rows the diff actually flagged: presence alone is not enough on ticks the diff skipped
        // (e.g. ignored partial ticks).
        static bool HasMissingComponentAggregate(Dictionary<DiffAggregate, float> aggregates, int ghostId, Type componentType)
        {
            if (aggregates == null)
                return false;

            var typeIndex = TypeManager.GetTypeIndex(componentType);
            foreach (var aggregate in aggregates.Keys)
            {
                if ((aggregate.Reasons & DiffInfo.DiffReasons.MissingComponent) != 0
                    && aggregate.GhostId == ghostId && aggregate.Component == typeIndex)
                    return true;
            }

            return false;
        }

        // A server system with no client trace this tick gets its own row (flagged MissingSystem by the
        // diff), mirroring how client-only systems get an ExtraSystem row — its data is server data. Ids
        // move out of the client execution-order range and the row nests under the client group matching
        // its server parent group by name (or that group's own missing row; top-level otherwise).
        void AppendMissingServerSystems(List<ManagedSystemData> clientSystems, List<ManagedSystemData> serverSystems, List<ManagedSystemData> managedSystems)
        {
            foreach (var serverSystemData in serverSystems)
            {
                // The name check guards against stale MissingSystem flags: the server tick is compared
                // against every client frame containing it, and its flags accumulate across those.
                if ((serverSystemData.DiffReasonFlags & DiffInfo.DiffReasons.MissingSystem) == 0
                    || TryFindSystemByName(clientSystems, serverSystemData.SystemName, out _))
                    continue;
                if (m_TracingViewFilters.IsSystemHidden(serverSystemData.SystemName))
                    continue;
                if (ShouldApplyValueChangeFilter(serverSystemData.DiffAggregates))
                    continue;

                var missingSystemData = serverSystemData;
                missingSystemData.FromServerWorld = true;
                missingSystemData.ExecutionOrder = TickInspectorTreeBuilder.MissingSystemIdBase + serverSystemData.ExecutionOrder;
                missingSystemData.ParentExecutionOrder = ResolveMissingSystemParent(serverSystemData, serverSystems, clientSystems);
                ApplyGhostComponentFilters(missingSystemData);
                managedSystems.Add(ApplyReasonMasks(missingSystemData));
            }
        }

        static int ResolveMissingSystemParent(in ManagedSystemData serverSystemData, List<ManagedSystemData> serverSystems, List<ManagedSystemData> clientSystems)
        {
            foreach (var serverGroup in serverSystems)
            {
                if (!serverGroup.IsSystemGroup || serverGroup.ExecutionOrder != serverSystemData.ParentExecutionOrder)
                    continue;
                if (TryFindSystemByName(clientSystems, serverGroup.SystemName, out var clientGroup))
                    return clientGroup.ExecutionOrder;
                // The parent group is missing too; nest under its own missing row.
                if ((serverGroup.DiffReasonFlags & DiffInfo.DiffReasons.MissingSystem) != 0)
                    return TickInspectorTreeBuilder.MissingSystemIdBase + serverGroup.ExecutionOrder;
                break;
            }

            return WorldDataManagedExtensions.NoParentExecutionOrder;
        }


        void BuildTreeview()
        {
            m_ItemsToExpand.Clear();
            // Guard against being called before data is set, or with a default/unknown tick/frame —
            // the indexers below would otherwise throw KeyNotFoundException.
            if (!m_Data.ClientWorldData.IsCreated || m_SelectedTickID.Equals(default))
            {
                TickReasonsComputed?.Invoke(DiffInfo.DiffReasons.Undefined);
                return;
            }
            if (!m_Data.ClientWorldData.PerFrameData.TryGetValue(m_SelectedFrameID, out var frameData)
                || !frameData.PerTickData.ContainsKey(m_SelectedTickID))
            {
                TickReasonsComputed?.Invoke(DiffInfo.DiffReasons.Undefined);
                return;
            }

            var serverValues = BuildServerLookup();
            var systems = GetSystemDataList(out var tickRowReasons);
            var enabledReasons = m_TracingViewFilters?.EnabledDiffReasons ?? DiffInfo.AllDiffReasons;
            var fuzzyDiffThreshold = m_TracingViewFilters?.FuzzyDiffThreshold ?? 0f;
            var result = TickInspectorTreeBuilder.Build(systems, serverValues, enabledReasons, fuzzyDiffThreshold);
            TickReasonsComputed?.Invoke(tickRowReasons);

            m_ItemsToExpand.AddRange(result.ItemsToExpand);
            m_TreeView.SetRootItems(result.Roots);
            m_TreeView.Rebuild();
        }

        // Server traces are keyed by tick only; flatten to a (system, ghost, component) lookup for the builder.
        Dictionary<TickInspectorTreeBuilder.ServerComponentKey, TracedComponent> BuildServerLookup()
        {
            var lookup = new Dictionary<TickInspectorTreeBuilder.ServerComponentKey, TracedComponent>();
            if (!m_Data.ServerWorldData.IsCreated || !m_Data.ServerWorldData.PerTickData.ContainsKey(m_SelectedTickID))
                return lookup;

            var serverSystems = m_Data.ServerWorldData.GetServerTickData(m_SelectedTickID).SystemsByExecutionOrder;
            if (serverSystems == null)
                return lookup;

            foreach (var system in serverSystems)
            {
                if (system.GhostComponents == null)
                    continue;
                foreach (var ghost in system.GhostComponents)
                {
                    if (ghost.Value.Components == null)
                        continue;
                    foreach (var component in ghost.Value.Components)
                        lookup[new TickInspectorTreeBuilder.ServerComponentKey(system.SystemName, ghost.Key, component.Key)] = component.Value;
                }
            }

            return lookup;
        }

        void ApplyExpansion()
        {
            if (m_TreeView == null)
                return;

            // The selection and filters define the expansion outright: highlighted subtrees unfold, the
            // rest folds. Expansion persists per item id across rebuilds, hence the collapse first.
            m_TreeView.CollapseAll();
            foreach (var id in m_ItemsToExpand)
                m_TreeView.ExpandItem(id, false, refresh: false);

            m_TreeView.RefreshItems();
        }

        public override Task OnDataAvailable(TracingData data)
        {
            m_Data = data;
            return Task.CompletedTask;
        }

        protected internal override Task OnSelectedFrameChanged(FrameID frameID)
        {
            m_SelectedFrameID = frameID;
            return Task.CompletedTask;
        }

        protected internal override Task OnSelectedTickChanged(TickID tickID)
        {
            m_SelectedTickID = tickID;
            if (m_TreeView == null)
            {
                Create();
            }
            else
            {
                BuildTreeview();
                ApplyExpansion();
            }
            return Task.CompletedTask;
        }

        public override void Clear()
        {
            m_SelectedFrameID = default(FrameID);
            m_SelectedTickID = default(TickID);
            m_Data = default(TracingData);

            m_Root.Clear();

            m_ItemsToExpand.Clear();
            // Null it so the next tick change goes through Create() and re-attaches the TreeView.
            m_TreeView = null;
        }

        public override void Dispose()
        {
            m_ItemsToExpand.Clear();

            if (m_TreeView != null)
            {
                m_TreeView.SetRootItems(new List<TreeViewItemData<TickInspectorNode>>());
                m_TreeView.Clear();
            }
        }

        public void OnSetFilters(TracingViewFilters tickInspectorFilters)
        {
            m_TracingViewFilters = tickInspectorFilters;
            if (m_SelectedTickID.Equals(default)) return;

            if (m_TreeView == null)
            {
                Create();
            }
            else
            {
                BuildTreeview();
                // The filters changed which diffs are highlighted; fold/unfold the tree to match.
                ApplyExpansion();
            }
        }
    }
}
