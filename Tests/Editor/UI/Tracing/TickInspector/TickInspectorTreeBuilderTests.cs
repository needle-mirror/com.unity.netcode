using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;
using Unity.Netcode.Editor.Tracing.UI.TickInspector;
using Unity.Netcode.Tracing;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor.Tracing.UI.Tests
{
    /// <summary>
    /// Unit tests for <see cref="TickInspectorTreeBuilder"/> — the heterogeneous tree shape
    /// (SystemGroup -> System -> Ghost -> Component), the partitioned id scheme and the
    /// diff-aware expansion set.
    /// </summary>
    class TickInspectorTreeBuilderTests
    {
        const int k_NoParent = WorldDataManagedExtensions.NoParentExecutionOrder;

        static ManagedSystemData MakeSystem(string name, int executionOrder, int parent, bool isGroup = false,
            int maxExecutionOrder = -1, Dictionary<int, GhostComponentData> ghosts = null, bool hasDiff = false)
        {
            return new ManagedSystemData
            {
                SystemName = name,
                SystemNameShort = name,
                ExecutionOrder = executionOrder,
                MaxExecutionOrder = maxExecutionOrder < 0 ? executionOrder : maxExecutionOrder,
                IsSystemGroup = isGroup,
                ParentExecutionOrder = parent,
                GhostComponents = ghosts,
                HasDiff = hasDiff,
            };
        }

        // hasDiff is the ghost row's OWN diff; component reasons stay on the component entries.
        static GhostComponentData MakeGhost(string name, bool hasDiff, params (Type type, bool diff)[] components)
        {
            var dict = new Dictionary<Type, TracedComponent>();
            foreach (var c in components)
            {
                var reason = c.diff ? DiffInfo.DiffReasons.ComponentData : DiffInfo.DiffReasons.Undefined;
                dict[c.type] = new TracedComponent { HasDiff = c.diff, DiffReasonFlags = reason, RowDiffReasonFlags = reason };
            }
            return new GhostComponentData { GhostName = name, HasDiff = hasDiff, Components = dict };
        }

        static List<TreeViewItemData<TickInspectorNode>> Children(TreeViewItemData<TickInspectorNode> item)
        {
            var list = new List<TreeViewItemData<TickInspectorNode>>();
            if (item.children != null)
                foreach (var c in item.children)
                    list.Add(c);
            return list;
        }

        [Test]
        public void Build_NestsGhostsAndComponentsUnderLeafSystem()
        {
            var ghosts = new Dictionary<int, GhostComponentData>
            {
                { 1, MakeGhost("Player", hasDiff: true, (typeof(string), false), (typeof(int), true)) },
            };
            var systems = new List<ManagedSystemData> { MakeSystem("MoveSystem", 1, k_NoParent, ghosts: ghosts, hasDiff: true) };

            var result = TickInspectorTreeBuilder.Build(systems);

            Assert.That(result.Roots, Has.Count.EqualTo(1));
            var system = result.Roots[0];
            Assert.That(system.data.NodeType, Is.EqualTo(TickInspectorNodeType.System));
            Assert.That(system.id, Is.EqualTo(1));

            var ghostItems = Children(system);
            Assert.That(ghostItems, Has.Count.EqualTo(1));
            var ghost = ghostItems[0];
            Assert.That(ghost.data.NodeType, Is.EqualTo(TickInspectorNodeType.Ghost));
            Assert.That(ghost.data.DisplayName, Is.EqualTo("Player"));

            var components = Children(ghost);
            Assert.That(components, Has.Count.EqualTo(2));
            Assert.That(components[0].data.NodeType, Is.EqualTo(TickInspectorNodeType.Component));
            // Components are sorted by type name: Int32 before String.
            Assert.That(components[0].data.DisplayName, Is.EqualTo("Int32"));
            Assert.That(components[1].data.DisplayName, Is.EqualTo("String"));
        }

        [Test]
        public void Build_PartitionsIdsByType()
        {
            var ghosts = new Dictionary<int, GhostComponentData>
            {
                { 1, MakeGhost("Player", hasDiff: false, (typeof(int), false)) },
            };
            var systems = new List<ManagedSystemData> { MakeSystem("MoveSystem", 1, k_NoParent, ghosts: ghosts) };

            var result = TickInspectorTreeBuilder.Build(systems);

            var system = result.Roots[0];
            var ghost = Children(system)[0];
            var component = Children(ghost)[0];

            Assert.That(system.id, Is.LessThan(TickInspectorTreeBuilder.GhostIdBase));
            Assert.That(ghost.id, Is.InRange(TickInspectorTreeBuilder.GhostIdBase, TickInspectorTreeBuilder.ComponentIdBase - 1));
            Assert.That(component.id, Is.GreaterThanOrEqualTo(TickInspectorTreeBuilder.ComponentIdBase));
        }

        [Test]
        public void Build_DoesNotAttachGhostsToSystemGroups()
        {
            var ghosts = new Dictionary<int, GhostComponentData>
            {
                { 1, MakeGhost("Player", hasDiff: false, (typeof(int), false)) },
            };
            // The group is given ghosts too (the data model populates them on groups), but they must
            // not be attached to the group — only to the leaf system inside it.
            var group = MakeSystem("Group", 1, k_NoParent, isGroup: true, maxExecutionOrder: 3, ghosts: ghosts);
            var child = MakeSystem("Child", 2, parent: 1, ghosts: ghosts);
            var systems = new List<ManagedSystemData> { group, child };

            var result = TickInspectorTreeBuilder.Build(systems);

            Assert.That(result.Roots, Has.Count.EqualTo(1));
            var groupItem = result.Roots[0];
            Assert.That(groupItem.data.NodeType, Is.EqualTo(TickInspectorNodeType.SystemGroup));

            var groupChildren = Children(groupItem);
            foreach (var c in groupChildren)
                Assert.That(c.data.NodeType, Is.Not.EqualTo(TickInspectorNodeType.Ghost), "Groups must not own ghost rows.");

            // The leaf system inside the group owns the ghost.
            var childItem = groupChildren.Find(c => c.id == 2);
            Assert.That(childItem.data.NodeType, Is.EqualTo(TickInspectorNodeType.System));
            Assert.That(Children(childItem).Find(c => c.data.NodeType == TickInspectorNodeType.Ghost).data, Is.Not.Null);
        }

        [Test]
        public void Build_ExpandsOnlyBranchesWithDiffs()
        {
            // The dirty ghost's diff lives on its component only — auto-expand must still reach it.
            var diffGhosts = new Dictionary<int, GhostComponentData>
            {
                { 1, MakeGhost("Dirty", hasDiff: false, (typeof(int), true)) },
            };
            var cleanGhosts = new Dictionary<int, GhostComponentData>
            {
                { 2, MakeGhost("Clean", hasDiff: false, (typeof(int), false)) },
            };
            var dirtySystem = MakeSystem("Dirty", 1, k_NoParent, ghosts: diffGhosts, hasDiff: true);
            var cleanSystem = MakeSystem("Clean", 2, k_NoParent, ghosts: cleanGhosts);
            var systems = new List<ManagedSystemData> { dirtySystem, cleanSystem };

            var result = TickInspectorTreeBuilder.Build(systems);

            // Only the system whose subtree has a diff is expanded; the clean system stays collapsed.
            Assert.That(result.ItemsToExpand, Has.Member(1));
            Assert.That(result.ItemsToExpand, Has.No.Member(2));

            var dirtyGhost = Children(result.Roots.Find(r => r.id == 1))[0];
            var cleanGhost = Children(result.Roots.Find(r => r.id == 2))[0];

            // The diffed ghost auto-expands to reveal its components; the clean one stays collapsed.
            Assert.That(result.ItemsToExpand, Has.Member(dirtyGhost.id));
            Assert.That(result.ItemsToExpand, Has.No.Member(cleanGhost.id));
        }

        [Test]
        public void Build_ExpandsAncestorGroupOfADiffedSystem()
        {
            // The group itself has no diff, but a system inside it does — the group must still expand so
            // the diffed system is reachable.
            var diffGhosts = new Dictionary<int, GhostComponentData>
            {
                { 1, MakeGhost("Dirty", hasDiff: false, (typeof(int), true)) },
            };
            var group = MakeSystem("Group", 1, k_NoParent, isGroup: true, maxExecutionOrder: 3);
            var child = MakeSystem("Child", 2, parent: 1, ghosts: diffGhosts, hasDiff: true);
            var systems = new List<ManagedSystemData> { group, child };

            var result = TickInspectorTreeBuilder.Build(systems);

            Assert.That(result.ItemsToExpand, Has.Member(1)); // group expanded via propagation
            Assert.That(result.ItemsToExpand, Has.Member(2)); // diffed child system
        }

        [Test]
        public void Build_NoDiffs_ExpandsNothing()
        {
            var cleanGhosts = new Dictionary<int, GhostComponentData>
            {
                { 1, MakeGhost("Clean", hasDiff: false, (typeof(int), false)) },
            };
            var system = MakeSystem("Clean", 1, k_NoParent, ghosts: cleanGhosts);

            var result = TickInspectorTreeBuilder.Build(new List<ManagedSystemData> { system });

            Assert.That(result.ItemsToExpand, Is.Empty);
        }

        [Test(Description = "A ghost row never reddens for its components' reasons; they reach it only via " +
                            "the collapsed-row tags, and the ghost still auto-expands to the diffing component.")]
        public void Build_KeepsComponentReasonsOnTheComponentRows()
        {
            var ghosts = new Dictionary<int, GhostComponentData>
            {
                { 1, MakeGhost("Player", hasDiff: false, (typeof(int), true), (typeof(string), false)) },
            };
            var systems = new List<ManagedSystemData> { MakeSystem("MoveSystem", 1, k_NoParent, ghosts: ghosts, hasDiff: true) };

            var result = TickInspectorTreeBuilder.Build(systems);

            var ghost = Children(result.Roots[0])[0];
            Assert.That(ghost.data.HasDiff, Is.False, "a ghost must not redden for a component's diff");
            Assert.That(ghost.data.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.Undefined));
            Assert.That(ghost.data.InclusiveDiffReasonFlags & DiffInfo.DiffReasons.ComponentData, Is.EqualTo(DiffInfo.DiffReasons.ComponentData),
                "the collapsed-row tags still surface the subtree's reasons");
            Assert.That(result.ItemsToExpand, Has.Member(ghost.id), "the ghost still auto-expands to the diffing component");

            var components = Children(ghost);
            var intComp = components.Find(c => c.data.DisplayName == "Int32");   // diffed
            var strComp = components.Find(c => c.data.DisplayName == "String");  // clean
            Assert.That(intComp.data.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.ComponentData));
            Assert.That(strComp.data.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.Undefined));
        }

        [Test(Description = "Inclusive reasons climb the whole chain, so a collapsed group summarises a " +
                            "component's reason several levels down without owning it.")]
        public void Build_BubblesSubtreeReasonsUpToAncestorRows()
        {
            var ghosts = new Dictionary<int, GhostComponentData>
            {
                { 1, MakeGhost("Player", hasDiff: false, (typeof(int), true)) },
            };
            var systems = new List<ManagedSystemData>
            {
                MakeSystem("PredictionGroup", 1, k_NoParent, isGroup: true, maxExecutionOrder: 2),
                MakeSystem("MoveSystem", 2, 1, ghosts: ghosts, hasDiff: true),
            };

            var result = TickInspectorTreeBuilder.Build(systems);

            var group = result.Roots[0];
            Assert.That(group.data.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.Undefined),
                "the group owns no reason of its own");
            Assert.That(group.data.InclusiveDiffReasonFlags & DiffInfo.DiffReasons.ComponentData, Is.EqualTo(DiffInfo.DiffReasons.ComponentData),
                "the collapsed group summarises the component's reason from two levels down");

            var system = Children(group)[0];
            Assert.That(system.data.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.Undefined));
            Assert.That(system.data.InclusiveDiffReasonFlags & DiffInfo.DiffReasons.ComponentData, Is.EqualTo(DiffInfo.DiffReasons.ComponentData));
        }

        [Test]
        public void Build_NullSystems_ReturnsEmpty()
        {
            var result = TickInspectorTreeBuilder.Build(null);
            Assert.That(result.Roots, Is.Empty);
            Assert.That(result.ItemsToExpand, Is.Empty);
        }

        struct DiffComp : IComponentData
        {
            public int Value;
        }

        struct ServerOnlyComp : IComponentData
        {
            public int Value;
        }

        [Test]
        public void Build_AddsMispredictionTableChildUnderDiffingComponent()
        {
            var clientComp = new TracedComponent
            {
                HasDiff = true,
                DiffReasonFlags = DiffInfo.DiffReasons.ComponentData,
                RowDiffReasonFlags = DiffInfo.DiffReasons.ComponentData,
                BeforeValue = new DiffComp { Value = 1 },
                AfterValue = new DiffComp { Value = 2 },
            };
            var ghost = new GhostComponentData
            {
                GhostName = "Player",
                HasDiff = true,
                DiffReasonFlags = DiffInfo.DiffReasons.ComponentData,
                RowDiffReasonFlags = DiffInfo.DiffReasons.ComponentData,
                Components = new Dictionary<Type, TracedComponent> { { typeof(DiffComp), clientComp } },
            };
            var ghosts = new Dictionary<int, GhostComponentData> { { 0, ghost } };
            var systems = new List<ManagedSystemData> { MakeSystem("MoveSystem", 1, k_NoParent, ghosts: ghosts, hasDiff: true) };

            var serverComp = new TracedComponent { BeforeValue = new DiffComp { Value = 0 }, AfterValue = new DiffComp { Value = 5 } };
            var serverValues = new Dictionary<TickInspectorTreeBuilder.ServerComponentKey, TracedComponent>
            {
                { new TickInspectorTreeBuilder.ServerComponentKey("MoveSystem", 0, typeof(DiffComp)), serverComp },
            };

            var result = TickInspectorTreeBuilder.Build(systems, serverValues);

            var componentItem = Children(Children(result.Roots[0])[0])[0];
            Assert.That(componentItem.data.NodeType, Is.EqualTo(TickInspectorNodeType.Component));

            var tableChildren = Children(componentItem);
            Assert.That(tableChildren, Has.Count.EqualTo(1));
            var table = tableChildren[0];
            Assert.That(table.data.NodeType, Is.EqualTo(TickInspectorNodeType.MispredictionTable));
            Assert.That(table.data.Detail, Is.Not.Null);
            Assert.That(table.data.Detail.ComponentType, Is.EqualTo(typeof(DiffComp)));
            Assert.That(((DiffComp)table.data.Detail.ClientAfter).Value, Is.EqualTo(2));
            Assert.That(((DiffComp)table.data.Detail.ServerAfter).Value, Is.EqualTo(5));

            // The diffing component auto-expands so its table is visible without an extra click.
            Assert.That(result.ItemsToExpand, Has.Member(componentItem.id));
        }

        [Test]
        public void Build_ServerOnlyComponent_UnderClientSystem_ShowsServerData()
        {
            // A missing (server-only) component row injected under a regular client system: it alone
            // carries server data, while its sibling stays a normal client-side row.
            var clientComp = new TracedComponent
            {
                BeforeValue = new DiffComp { Value = 1 },
                AfterValue = new DiffComp { Value = 2 },
            };
            var missingComp = new TracedComponent
            {
                HasDiff = true,
                DiffReasonFlags = DiffInfo.DiffReasons.MissingComponent,
                RowDiffReasonFlags = DiffInfo.DiffReasons.MissingComponent,
                BeforeValue = new ServerOnlyComp { Value = 3 },
                AfterValue = new ServerOnlyComp { Value = 7 },
                FromServerWorld = true,
            };
            var ghost = new GhostComponentData
            {
                GhostName = "Player",
                HasDiff = true,
                Components = new Dictionary<Type, TracedComponent>
                {
                    { typeof(DiffComp), clientComp },
                    { typeof(ServerOnlyComp), missingComp },
                },
            };
            var systems = new List<ManagedSystemData>
            {
                MakeSystem("MoveSystem", 1, k_NoParent, ghosts: new Dictionary<int, GhostComponentData> { { 0, ghost } }, hasDiff: true),
            };

            var result = TickInspectorTreeBuilder.Build(systems);

            var systemItem = result.Roots[0];
            Assert.That(systemItem.data.FromClientWorld, Is.True, "the system itself is a client row");

            var components = Children(Children(systemItem)[0]);
            var clientItem = components.Find(c => c.data.DisplayName == nameof(DiffComp));
            var missingItem = components.Find(c => c.data.DisplayName == nameof(ServerOnlyComp));

            Assert.That(clientItem.data.FromClientWorld, Is.True);
            var clientTable = Children(clientItem)[0];
            Assert.That(((DiffComp)clientTable.data.Detail.ClientAfter).Value, Is.EqualTo(2));
            Assert.That(clientTable.data.Detail.ServerAfter, Is.Null);

            Assert.That(missingItem.data.FromClientWorld, Is.False);
            Assert.That(missingItem.data.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.MissingComponent));
            var missingTable = Children(missingItem)[0];
            Assert.That(missingTable.data.FromClientWorld, Is.False);
            Assert.That(missingTable.data.Detail.ClientBefore, Is.Null, "a server-only component has no client data");
            Assert.That(missingTable.data.Detail.ClientAfter, Is.Null, "a server-only component has no client data");
            Assert.That(((ServerOnlyComp)missingTable.data.Detail.ServerBefore).Value, Is.EqualTo(3));
            Assert.That(((ServerOnlyComp)missingTable.data.Detail.ServerAfter).Value, Is.EqualTo(7));
        }

        [Test]
        public void Build_ServerOnlySystem_ShowsServerDataAndTargetsServerWorld()
        {
            // A missing (server-only) system row: its traces are server data, so the detail table fills
            // the server column, the client column stays empty, and inspector actions target the server world.
            var serverComp = new TracedComponent
            {
                HasDiff = true,
                DiffReasonFlags = DiffInfo.DiffReasons.ComponentData,
                RowDiffReasonFlags = DiffInfo.DiffReasons.ComponentData,
                BeforeValue = new DiffComp { Value = 3 },
                AfterValue = new DiffComp { Value = 7 },
            };
            var ghost = new GhostComponentData
            {
                GhostName = "Player",
                HasDiff = true,
                Components = new Dictionary<Type, TracedComponent> { { typeof(DiffComp), serverComp } },
            };
            var missing = MakeSystem("ServerOnlySystem", TickInspectorTreeBuilder.MissingSystemIdBase + 1, k_NoParent,
                ghosts: new Dictionary<int, GhostComponentData> { { 0, ghost } }, hasDiff: true);
            missing.FromServerWorld = true;
            missing.DiffReasonFlags = DiffInfo.DiffReasons.MissingSystem;
            missing.RowDiffReasonFlags = DiffInfo.DiffReasons.MissingSystem;

            var result = TickInspectorTreeBuilder.Build(new List<ManagedSystemData> { missing });

            var systemItem = result.Roots[0];
            Assert.That(systemItem.data.FromClientWorld, Is.False);
            Assert.That(systemItem.data.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.MissingSystem));

            var ghostItem = Children(systemItem)[0];
            Assert.That(ghostItem.data.FromClientWorld, Is.False);

            var componentItem = Children(ghostItem)[0];
            Assert.That(componentItem.data.FromClientWorld, Is.False);

            var table = Children(componentItem)[0];
            Assert.That(table.data.FromClientWorld, Is.False);
            Assert.That(table.data.Detail.ClientBefore, Is.Null, "a server-only system has no client data");
            Assert.That(table.data.Detail.ClientAfter, Is.Null, "a server-only system has no client data");
            Assert.That(((DiffComp)table.data.Detail.ServerBefore).Value, Is.EqualTo(3));
            Assert.That(((DiffComp)table.data.Detail.ServerAfter).Value, Is.EqualTo(7));
        }

        [Test]
        public void Build_ServerOnlyGhost_UnderClientSystem_ShowsServerDataAndTargetsServerWorld()
        {
            // A missing (server-only) ghost row injected under a regular client system: the whole subtree
            // carries server data, its tables leave the client column empty ("No data"), and inspector
            // actions target the server world. A sibling client ghost stays a normal client-side row.
            var clientGhost = MakeGhost("Player", hasDiff: false, (typeof(DiffComp), false));
            var missingComp = new TracedComponent
            {
                BeforeValue = new ServerOnlyComp { Value = 3 },
                AfterValue = new ServerOnlyComp { Value = 7 },
            };
            var missingGhost = new GhostComponentData
            {
                GhostName = "HiddenCube",
                HasDiff = true,
                DiffReasonFlags = DiffInfo.DiffReasons.MissingGhost,
                RowDiffReasonFlags = DiffInfo.DiffReasons.MissingGhost,
                FromServerWorld = true,
                Components = new Dictionary<Type, TracedComponent> { { typeof(ServerOnlyComp), missingComp } },
            };
            var ghosts = new Dictionary<int, GhostComponentData> { { 0, clientGhost }, { 1, missingGhost } };
            var systems = new List<ManagedSystemData> { MakeSystem("MoveSystem", 1, k_NoParent, ghosts: ghosts, hasDiff: true) };

            var result = TickInspectorTreeBuilder.Build(systems);

            var systemItem = result.Roots[0];
            Assert.That(systemItem.data.FromClientWorld, Is.True, "the system itself is a client row");

            var ghostItems = Children(systemItem);
            var clientItem = ghostItems.Find(g => g.data.DisplayName == "Player");
            var missingItem = ghostItems.Find(g => g.data.DisplayName == "HiddenCube");

            Assert.That(clientItem.data.FromClientWorld, Is.True);

            Assert.That(missingItem.data.FromClientWorld, Is.False);
            Assert.That(missingItem.data.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.MissingGhost));
            Assert.That(result.ItemsToExpand, Has.Member(missingItem.id), "the missing ghost row auto-expands");

            var componentItem = Children(missingItem)[0];
            Assert.That(componentItem.data.FromClientWorld, Is.False);

            var table = Children(componentItem)[0];
            Assert.That(table.data.FromClientWorld, Is.False);
            Assert.That(table.data.Detail.ClientBefore, Is.Null, "a server-only ghost has no client data");
            Assert.That(table.data.Detail.ClientAfter, Is.Null, "a server-only ghost has no client data");
            Assert.That(((ServerOnlyComp)table.data.Detail.ServerBefore).Value, Is.EqualTo(3));
            Assert.That(((ServerOnlyComp)table.data.Detail.ServerAfter).Value, Is.EqualTo(7));
        }

        [Test]
        public void Build_ClientOnlyGhost_ShowsClientDataAndNoServerData()
        {
            // An extra (client-only) ghost renders from client data; with no server value to pair with,
            // its tables leave the server column empty ("No data").
            var extraComp = new TracedComponent
            {
                BeforeValue = new DiffComp { Value = 1 },
                AfterValue = new DiffComp { Value = 2 },
            };
            var extraGhost = new GhostComponentData
            {
                GhostName = "PhantomCube",
                HasDiff = true,
                DiffReasonFlags = DiffInfo.DiffReasons.ExtraGhost,
                RowDiffReasonFlags = DiffInfo.DiffReasons.ExtraGhost,
                Components = new Dictionary<Type, TracedComponent> { { typeof(DiffComp), extraComp } },
            };
            var systems = new List<ManagedSystemData>
            {
                MakeSystem("MoveSystem", 1, k_NoParent, ghosts: new Dictionary<int, GhostComponentData> { { 0, extraGhost } }, hasDiff: true),
            };

            // The server never spawned this ghost, so the server lookup has nothing for it.
            var result = TickInspectorTreeBuilder.Build(systems, new Dictionary<TickInspectorTreeBuilder.ServerComponentKey, TracedComponent>());

            var ghostItem = Children(result.Roots[0])[0];
            Assert.That(ghostItem.data.FromClientWorld, Is.True);
            Assert.That(ghostItem.data.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.ExtraGhost));
            Assert.That(result.ItemsToExpand, Has.Member(ghostItem.id), "the extra ghost row auto-expands");

            var table = Children(Children(ghostItem)[0])[0];
            Assert.That(((DiffComp)table.data.Detail.ClientBefore).Value, Is.EqualTo(1));
            Assert.That(((DiffComp)table.data.Detail.ClientAfter).Value, Is.EqualTo(2));
            Assert.That(table.data.Detail.ServerBefore, Is.Null, "a client-only ghost has no server data");
            Assert.That(table.data.Detail.ServerAfter, Is.Null, "a client-only ghost has no server data");
        }

        [Test]
        public void Build_CleanComponent_HasCollapsedMispredictionTable()
        {
            var ghosts = new Dictionary<int, GhostComponentData>
            {
                { 0, MakeGhost("Player", hasDiff: false, (typeof(int), false)) },
            };
            var systems = new List<ManagedSystemData> { MakeSystem("MoveSystem", 1, k_NoParent, ghosts: ghosts) };

            var result = TickInspectorTreeBuilder.Build(systems);

            var componentItem = Children(Children(result.Roots[0])[0])[0];
            Assert.That(componentItem.data.NodeType, Is.EqualTo(TickInspectorNodeType.Component));

            // Every component owns a table now; a clean one just isn't auto-expanded (and shows no red).
            var tableChildren = Children(componentItem);
            Assert.That(tableChildren, Has.Count.EqualTo(1));
            Assert.That(tableChildren[0].data.NodeType, Is.EqualTo(TickInspectorNodeType.MispredictionTable));
            Assert.That(result.ItemsToExpand, Has.No.Member(componentItem.id));
        }
    }
}
