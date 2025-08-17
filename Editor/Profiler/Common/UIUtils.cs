using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    enum NetworkRole
    {
        Client,
        Server
    }

    static class UIUtils
    {
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
    }
}
