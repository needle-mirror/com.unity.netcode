using Unity.Netcode.Tracing;

namespace Unity.Netcode.Editor.Tracing.UI
{

    // The display name and one-line explanation of each <see cref="DiffInfo.DiffReasons"/>.
    static class DiffReasonCatalog
    {
        internal readonly struct Entry
        {
            public readonly DiffInfo.DiffReasons Reason;
            public readonly string Label;
            public readonly string Tooltip;

            public Entry(DiffInfo.DiffReasons reason, string label, string tooltip)
            {
                Reason = reason;
                Label = label;
                Tooltip = tooltip;
            }
        }

        // Declaration order is the order reasons render in
        internal static readonly Entry[] All =
        {
            new(DiffInfo.DiffReasons.ComponentData, "Component data", "Value differs between client and server."),
            new(DiffInfo.DiffReasons.MissingComponent, "Missing component", "Component on the server but not the client."),
            new(DiffInfo.DiffReasons.ExtraComponent, "Extra component", "Component on the client but not the server."),
            new(DiffInfo.DiffReasons.MissingGhost, "Missing ghost", "Ghost on the server but not the client."),
            new(DiffInfo.DiffReasons.ExtraGhost, "Extra ghost", "Ghost on the client but not the server."),
            new(DiffInfo.DiffReasons.MissingSystem, "Missing system", "System ran on the server but not the client."),
            new(DiffInfo.DiffReasons.ExtraSystem, "Extra system", "System ran on the client but not the server."),
            new(DiffInfo.DiffReasons.SystemOrder, "System order", "Systems ran in a different order on each side."),
            new(DiffInfo.DiffReasons.BatchedTick, "Batched tick", "Server simulated this tick as part of a batch."),
            new(DiffInfo.DiffReasons.DeltaTime, "Delta time", "Client and server tick delta times differ."),
            new(DiffInfo.DiffReasons.PartialTick, "Partial tick", "Diff on a partial tick, where mismatches are expected."),
        };

        // Every catalog reason OR'd together — the "all diff tags selected" mask
        internal static readonly DiffInfo.DiffReasons SelectableMask = ComputeSelectableMask();

        static DiffInfo.DiffReasons ComputeSelectableMask()
        {
            var mask = DiffInfo.DiffReasons.Undefined;
            foreach (var entry in All)
                mask |= entry.Reason;
            return mask;
        }

        internal static string SelectionTooltip(Entry entry, bool selected)
            => entry.Tooltip + (selected ? "\nClick to deselect." : "\nClick to select.");
    }
}
