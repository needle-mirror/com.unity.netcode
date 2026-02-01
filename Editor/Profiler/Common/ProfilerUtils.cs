using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine.UIElements;
using Unity.Networking.Transport;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    enum NetworkRole
    {
        Client,
        Server
    }

    enum OverheadType
    {
        None,
        SnapshotOverhead,
        GhostTypeOverhead,
    }

    enum IconType
    {
        None,
        Overhead,
        Warning,
        GhostPrefab,
        GhostComponent
    }

    enum IconPosition
    {
        BeforeLabel,
        AfterLabel
    }

    /// <summary>
    /// A collection of static utility methods for the netcode profiler editor window.
    /// </summary>
    static class ProfilerUtils
    {
        internal static string GetWorldName(NetworkRole role)
        {
            return role switch
            {
                NetworkRole.Server => ClientServerBootstrap.ServerWorld?.Name ?? "Server World",
                NetworkRole.Client => ClientServerBootstrap.ClientWorld?.Name ?? "Client World",
                _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Invalid network role")
            };
        }

        internal static int GetMaxMessageSize()
        {
            NetCodeConfig.RuntimeTryFindSettings();
            var config = NetCodeConfig.Global;
            if (config && !Application.isPlaying)
                return config.MaxMessageSize;

            var maxMessageSize = NetworkParameterConstants.MaxMessageSize;
            foreach (var world in World.All)
            {
                if (world.IsServer())
                {
                    using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamSnapshotTargetSize>());
                    if (query.TryGetSingleton(out NetworkStreamSnapshotTargetSize snapshotTargetSize))
                        return snapshotTargetSize.Value;
                }
            }

            return maxMessageSize;
        }

        internal static string GetOverheadTooltip(OverheadType overheadType)
        {
            switch (overheadType)
            {
                case OverheadType.None:
                    return string.Empty;
                case OverheadType.SnapshotOverhead:
                    return NetcodeProfilerConstants.s_SnapshotOverheadTooltip;
                case OverheadType.GhostTypeOverhead:
                    return NetcodeProfilerConstants.s_GhostPrefabOverheadTooltip;
                default:
                    throw new ArgumentOutOfRangeException(nameof(overheadType), overheadType, null);
            }
        }

        internal static string GetPacketDirection(NetworkRole networkRole, bool uppercase = false)
        {
            var direction = networkRole == NetworkRole.Client ? "received" : "sent";
            return uppercase ? char.ToUpper(direction[0]) + direction[1..] : direction;
        }

        internal static uint BitsToBytes(uint bits)
        {
            return (uint)Math.Round((float)bits / 8, 0);
        }

        internal static uint BitsToBytes(float bits)
        {
            return (uint)Math.Round(bits / 8, 0);
        }

        internal static string FormatBitsToBytes(uint bits)
        {
            return FormatBytes(BitsToBytes(bits));
        }

        internal static string FormatFractionalBytes(uint bits)
        {
            // Convert into format xB.yb (i.e. 26 bits --> 3B.2b)
            var bytes = bits / 8;
            var remainingBits = bits % 8;
            return $"{bytes}B.{remainingBits}b";
        }

        /// <summary>
        /// Formats bytes into a human-readable string with appropriate suffixes (B, KB, MB, etc.).
        /// </summary>
        /// <param name="bytes">The number of bytes to format.</param>
        /// <returns>A formatted string representing the size in bytes with appropriate suffixes.</returns>
        static string FormatBytes(long bytes)
        {
            switch (bytes)
            {
                case < 0:
                    return "N/A";
                case 0:
                    return "0 B";
            }

            string[] suffix = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            var i = 0;
            double dblSByte = bytes;
            while (dblSByte >= 1024 && i < suffix.Length - 1)
            {
                dblSByte /= 1024.0;
                i++;
            }
            return $"{dblSByte:0.##} {suffix[i]}";
        }

        internal static List<int> GetExpandedIds(this MultiColumnTreeView treeView)
        {
            var expandedIds = new List<int>();
            for (var i = 0; i < 1000; i++)
            {
                if (!treeView.viewController.Exists(i))
                    break;

                if (treeView.IsExpanded(i))
                {
                    expandedIds.Add(i);
                }
            }
            return expandedIds;
        }

        internal static void ExpandItemsById(this MultiColumnTreeView treeView, List<int> expandedIds)
        {
            foreach (var expandedId in expandedIds)
            {
                if(!treeView.viewController.Exists(expandedId))
                    continue;

                treeView.ExpandItem(expandedId, false);
            }
        }

        internal static void SelectGhostPrefab(TypeIndex typeIndex, string name)
        {
            // Ping the ghost prefab in the project window
            var guids = AssetDatabase.FindAssets(name);
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset != null && asset.name == name)
                {
                    EditorGUIUtility.PingObject(asset);
                    Selection.activeObject = asset;
                    break;
                }
            }
        }

        internal static void SelectGhostComponent(TypeIndex typeIndex, string name = null)
        {
            var type = TypeManager.GetType(typeIndex);
            NetcodeEditorUtility.ShowGhostComponentInspectorContent(type);
        }

        internal static void SelectAdjacentTick(int direction, NetworkRole networkRole)
        {
            var profilerWindow = EditorWindow.GetWindow<ProfilerWindow>();
            var selectedFrameIndex = (int)profilerWindow.selectedFrameIndex;
            if (selectedFrameIndex == -1) return;

            var frameForNextTick = SnapshotTickMappingSingleton.instance.GetFrameIndexForAdjacentTick(networkRole, selectedFrameIndex, direction);
            if (frameForNextTick >= ProfilerDriver.firstFrameIndex && frameForNextTick <= ProfilerDriver.lastFrameIndex)
                profilerWindow.selectedFrameIndex = frameForNextTick;
        }

        internal static void SelectCorrespondingTick(NetworkRole networkRole)
        {
            var profilerWindow = EditorWindow.GetWindow<ProfilerWindow>();
            var selectedFrameIndex = profilerWindow.selectedFrameIndex;
            if (selectedFrameIndex == -1) return;

            if (networkRole == NetworkRole.Client)
            {
                var serverTickFrame = SnapshotTickMappingSingleton.instance.GetServerTickFrameIndexFromClientTickFrameIndex((int)selectedFrameIndex);
                if (serverTickFrame >= 0) profilerWindow.selectedFrameIndex = serverTickFrame;
            }

            if (networkRole == NetworkRole.Server)
            {
                var clientTickFrame = SnapshotTickMappingSingleton.instance.GetClientTickFrameIndexFromServerTickFrameIndex((int)selectedFrameIndex);
                if (clientTickFrame >= 0) profilerWindow.selectedFrameIndex = clientTickFrame;
            }
        }

        internal static ProfilerGhostTypeData GetGhostTypeDataAtIndex(MultiColumnTreeView multiColumnTreeView, int index)
        {
            var data = multiColumnTreeView.GetItemDataForIndex<ProfilerGhostTypeData>(index);
            return data;
        }

        internal static int GetPercentageOfSnapshot(uint totalSnapshotSizeInBits, ProfilerGhostTypeData ghostTypeData)
        {
            if (ghostTypeData.sizeInBits == 0 || totalSnapshotSizeInBits == 0)
                return 0;
            return Mathf.RoundToInt(ghostTypeData.sizeInBits / (float)totalSnapshotSizeInBits * 100);
        }

        [MenuItem("Window/Multiplayer/Network Profiler", priority = 3007)]
        static void OpenNetworkProfilerWindow()
        {
            EditorWindow.GetWindow<ProfilerWindow>(false, null, true);
        }
    }
}
