using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine.UIElements;
using Unity.Networking.Transport;
using UnityEditor;
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
        Overhead,
        Warning
    }

    enum IconPosition
    {
        BeforeLabel,
        AfterLabel
    }

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

        internal static string GetPacketDirection(NetworkRole networkRole)
        {
            return networkRole == NetworkRole.Client ? "received" : "sent";
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

        /// <summary>
        /// Formats bytes into a human-readable string with appropriate suffixes (B, KB, MB, etc.).
        /// </summary>
        /// <param name="bytes">The number of bytes to format.</param>
        /// <returns>A formatted string representing the size in bytes with appropriate suffixes.</returns>
        internal static string FormatBytes(long bytes)
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
    }
}
