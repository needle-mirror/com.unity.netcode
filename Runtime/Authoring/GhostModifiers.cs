// IMPORTANT NOTE: This file is shared with NetCode source generators
// NO UnityEngine, UnityEditore or other packages dll references are allowed here.
// IF YOU CHANGE THIS FILE, REMEMBER TO RECOMPILE THE SOURCE GENERATORS

using System;

namespace Unity.NetCode
{
    [Flags]
    public enum GhostPrefabType
    {
        None = 0,
        InterpolatedClient = 1,
        PredictedClient = 2,
        Client = 3,
        Server = 4,
        AllPredicted = 6,
        All = 7
    }

    [Flags]
    public enum GhostSendType
    {
        None = 0,
        Interpolated = 1,
        Predicted = 2,
        All = 3
    }

    [Flags]
    public enum SendToOwnerType
    {
        None = 0,
        SendToOwner = 1,
        SendToNonOwner = 2,
        All = 3,
    }

    /// <summary>Denotes how <see cref="GhostFieldAttribute"/> values are deserialized when received from snapshots.</summary>
    public enum SmoothingAction
    {
        /// <summary>The GhostField value will clamp to the latest snapshot value as it's available.</summary>
        Clamp       = 0,
        
        /// <summary>Interpolate the GhostField value between the two snapshot values, and if no data is available for the next tick, clamp at the latest snapshot value.</summary>
        Interpolate = 1 << 0,
        
        /// <summary>
        /// Interpolate the GhostField value between snapshot values, and if no data is available for the next tick, the next value is linearly extrapolated using the previous two snapshot values. 
        /// Extrapolation is limited (i.e. clamped) via <see cref="ClientTickRate.MaxExtrapolationTimeSimTicks"/>.
        /// </summary>
        InterpolateAndExtrapolate = 3
    }
}
