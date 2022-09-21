using System;
using System.Diagnostics;
using Unity.Entities;
using Unity.Collections;

namespace Unity.NetCode
{
    /// <summary>
    /// A simple struct used to represent a network tick. This is using a uint internally, but it has special
    /// logic to deal with invalid ticks, and it handles wrap around correctly.
    /// </summary>
    public struct NetworkTick : IEquatable<NetworkTick>
    {
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckValid()
        {
            if(!IsValid)
                throw new InvalidOperationException("Cannot perform calculations with invalid ticks");
        }
        /// <summary>
        /// A value representing an invalid tick, this is the same as 'default' but provide more context in the code.
        /// </summary>
        public static NetworkTick Invalid => default;
        /// <summary>
        /// Compare two ticks, also works for invalid ticks.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static bool operator ==(in NetworkTick a, in NetworkTick b)
        {
            return a.m_Value == b.m_Value;
        }
        /// <summary>
        /// Compare two ticks, also works for invalid ticks.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static bool operator !=(in NetworkTick a, in NetworkTick b)
        {
            return a.m_Value != b.m_Value;
        }
        /// <summary>
        /// Compare two ticks, also works for invalid ticks.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public override bool Equals(object obj) => obj is NetworkTick && Equals((NetworkTick) obj);
        /// <summary>
        /// Compare two ticks, also works for invalid ticks.
        /// </summary>
        /// <param name="compare"></param>
        /// <returns></returns>
        public bool Equals(NetworkTick compare)
        {
            return m_Value == compare.m_Value;
        }
        /// <summary>
        /// Get a hash for the tick.
        /// </summary>
        /// <returns></returns>
        public override int GetHashCode()
        {
            return (int)m_Value;
        }

        /// <summary>
        /// Constructor, the start tick can be 0. Use this instead of the default constructor since that will
        /// generate an invalid tick.
        /// </summary>
        /// <param name="start">The tick index to initialize the NetworkTick with.</param>
        public NetworkTick(uint start)
        {
            m_Value = (start<<1) | 1u;
        }
        /// <summary>
        /// Check if the tick is valid. Not all operations will work on invalid ticks.
        /// </summary>
        public bool IsValid => (m_Value&1)!=0;
        /// <summary>
        /// Get the tick index assuming the tick is valid. Should be used with care since ticks will wrap around.
        /// </summary>
        public uint TickIndexForValidTick
        {
            get
            {
                CheckValid();
                return m_Value>>1;
            }
        }
        /// <summary>
        /// The serialized data for a tick. Includes both validity and tick index.
        /// </summary>
        public uint SerializedData
        {
            get
            {
                return m_Value;
            }
            set
            {
                m_Value = value;
            }
        }
        /// <summary>
        /// Add a delta to the tick, assumes the tick is valid.
        /// </summary>
        /// <param name="delta">The value to add to the tick</param>
        public void Add(uint delta)
        {
            CheckValid();
            m_Value += delta<<1;
        }
        /// <summary>
        /// Subtract a delta from the tick, assumes the tick is valid.
        /// </summary>
        /// <param name="delta">The value to subtract from the tick</param>
        public void Subtract(uint delta)
        {
            CheckValid();
            m_Value -= delta<<1;
        }
        /// <summary>
        /// Increment the tick, assumes the tick is valid.
        /// </summary>
        public void Increment()
        {
            CheckValid();
            m_Value += 2;
        }
        /// <summary>
        /// Decrement the tick, assumes the tick is valid.
        /// </summary>
        public void Decrement()
        {
            CheckValid();
            m_Value -= 2;
        }
        /// <summary>
        /// Compute the number of ticks which passed since an older tick. Assumes both ticks are valid.
        /// If the passed in tick is newer this will return a negative value.
        /// </summary>
        /// <param name="older">The tick to compute passed ticks from</param>
        /// <returns></returns>
        public int TicksSince(NetworkTick older)
        {
            CheckValid();
            older.CheckValid();
            // Convert to int first to make sure negative values stay negative after shift
            int delta = (int)(m_Value-older.m_Value);
            return delta>>1;
        }
        /// <summary>
        /// Check if this tick is newer than another tick. Assumes both ticks are valid.
        /// </summary>
        /// <remarks>
        /// The ticks wraps around, so if either tick is stored for too long (several days assuming 60hz)
        /// the result might not be correct.
        /// </remarks>
        /// <param name="old">The tick to compare with</param>
        /// <returns></returns>
        public bool IsNewerThan(NetworkTick old)
        {
            CheckValid();
            old.CheckValid();
            // Invert the check so same does not count as newer
            return !(old.m_Value - m_Value < (1u << 31));
        }
        /// <summary>
        /// Convert the tick to a fixed string. Also handles invalid ticks.
        /// </summary>
        /// <returns>The tick index as a fixed string, or "Invalid" for invalid ticks.</returns>
        public FixedString32Bytes ToFixedString()
        {
            if (IsValid)
            {
                FixedString32Bytes val = default;
                val.Append(m_Value>>1);
                return val;
            }
            return "Invalid";
        }
        private uint m_Value;
    }

    /// <summary>
    /// Flags used by <see cref="NetworkTime"/> singleton to add some properties to the current simulated tick.
    /// See the individual flags documentation for further information.
    /// </summary>
    [Flags]
    public enum NetworkTimeFlags : byte
    {
        /// <summary>
        /// Indicate that the current server tick is a predicted one and the simulation is running inside the prediction group.
        /// </summary>
        IsInPredictionLoop = 1 << 0,
        /// <summary>
        /// Only valid inside the prediction loop, the server tick the prediction is starting from.
        /// </summary>
        IsFirstPredictionTick = 1 << 2,
        /// <summary>
        /// Only valid inside the prediction loop, the current server tick which will be the last tick to predict.
        /// </summary>
        IsFinalPredictionTick = 1 << 3,
        /// <summary>
        /// Only valid inside the prediction loop, the current server tick is the last full tick we are predicting. If IsFinalPredictionTick is set
        /// the IsPartial flag must be false. The IsFinalPredictionTick can be also set if the current server tick we are predicting is a full tick.
        /// </summary>
        IsFinalFullPredictionTick = 1 << 4,
        /// <summary>
        /// Only valid on server. True when the current simulated tick is running with a variabled delta time to recover from
        /// a previous long running frame.
        /// </summary>
        IsCatchUpTick = 1 << 5,
        /// <summary>
        /// Only valid inside the prediction loop, the current server tick is a full tick and this is the first time it is being predicting as a non-partial tick.
        /// The IsPartial flag must be false.
        /// This is frequently used to make sure effects which cannot easily be rolled back, such as spawning objects / particles / vfx or playing sounds, only happens once and are not repeated.
        /// </summary>
        IsFirstTimeFullyPredictingTick = 1 << 6,
    }
    /// <summary>
    /// Present on both client and server world, singleton component that contains all the timing characterist of the client/server simulation loop.
    /// </summary>
    public struct NetworkTime : IComponentData
    {
        /// <summary>
        /// The current simulated server tick the server will run this frame. Always start from 1. 0 is consider an invalid value.
        /// The ServerTick value behave differently on client and server.
        /// On the server:
        ///  - it is always a "full" tick
        ///  - strict monontone and continue (up to the wrap around)
        ///  - the same inside or outside the prediction loop
        /// On the client:
        ///  - it is the tick the client predict the server should simulate this frame. Depends on current lag and command slack
        ///  - can be either a full or partial.
        ///  - if the tick is partial, the client would run the simulation for it multiple time, each time with a different delta time proportion
        ///  - it is not monotone:
        ///      - in some rare/recovery situation may rollback or having jump forward (due to time/lag adjustments).
        ///      - during the prediction loop the ServerTick value is changed to match either the last full simulated tick or
        ///        , in case of a rollback because a snapshot has been received, to the oldest received tick among all entities. In both case, and the end of
        ///        of the prediction loop the server tick will be reset to current predicted server tick.
        /// </summary>
        public NetworkTick ServerTick;
        /// <summary>
        /// Only meaningful on the client that run at variable step rate. On the server is always 1.0. Always in range is (0.0 and 1.0].
        /// </summary>
        public float ServerTickFraction;
        /// <summary>
        /// The current interpolated tick (integral part). Always less then the ServerTick on the Client (and equals to ServerTick on the server).
        /// </summary>
        public NetworkTick InterpolationTick;
        /// <summary>
        /// The fractional part of the tick (XXX.fraction). Always in between (0.0, 1.0]
        /// </summary>
        public float InterpolationTickFraction;
        /// <summary>
        /// The number of simulation steps this tick is scaled with. This is used to make one update which covers
        /// N ticks in order to reduce CPU cost. This is always 1 for partial ticks in the prediction loop, but can be more than 1 for partial ticks outside the prediction loop.
        /// </summary>
        public int SimulationStepBatchSize;
        /// <summary>
        ///  For internal use only, special flags that add context and properties to the current server tick value.
        /// </summary>
        internal NetworkTimeFlags Flags;
        /// <summary>
        ///  For internal use only, the total elapsed network time since the world has been created. Different for server and client:
        /// - On the server is advanced at fixed time step interval (depending on ClientServerTickRate)
        /// - On the client use the computed network delta time based on the predicted server tick. The time time is not monotone.
        /// </summary>
        internal double ElapsedNetworkTime;
        /// <summary>
        /// True if the current tick is running with delta time that is a fraction of the ServerTickDeltaTime. Only true on the client when
        /// running at variable frame rate.
        /// </summary>
        public bool IsPartialTick => ServerTickFraction < 1f;
        /// <summary>
        /// Indicate that the current server tick is a predicted one and the simulation is running inside the prediction group.
        /// </summary>
        public bool IsInPredictionLoop => (Flags & NetworkTimeFlags.IsInPredictionLoop) != 0;
        /// <summary>
        /// Only valid inside the prediction loop. The server tick the prediction is starting from.
        /// </summary>
        public bool IsFirstPredictionTick => (Flags & NetworkTimeFlags.IsFirstPredictionTick) != 0;
        /// <summary>
        ///  Only valid inside the prediction loop. The current server tick which will be the last tick to predict
        /// </summary>
        public bool IsFinalPredictionTick => (Flags & NetworkTimeFlags.IsFinalPredictionTick) != 0;
        /// <summary>
        /// Only valid inside the prediction loop. The current server tick which will be the last full tick we are predicting
        /// </summary>
        public bool IsFinalFullPredictionTick => (Flags & NetworkTimeFlags.IsFinalFullPredictionTick) != 0;
        /// <summary>
        /// Only valid inside the prediction loop. True when this `ServerTick` is being predicted in full for the first time.
        /// "In full" meaning the first non-partial simulation tick. I.e. Partial ticks don't count.
        /// </summary>
        public bool IsFirstTimeFullyPredictingTick => (Flags & NetworkTimeFlags.IsFirstTimeFullyPredictingTick) != 0;

        /// <summary>
        /// Only valid on server. True when the current simulated tick is running with a variabled delta time to recover from
        /// a previous long running frame.
        /// </summary>
        public bool IsCatchUpTick => (Flags & NetworkTimeFlags.IsCatchUpTick) != 0;
    }

    /// <summary>
    /// Component added to the NetworkTime singleton entity when it is created in a client world. Contains the unscaled application
    /// ElapsedTime and DeltaTime.
    /// </summary>
    public struct UnscaledClientTime : IComponentData
    {
        /// <summary>
        /// The current unscaled elapsed time since the World has been created. Reliably traking the real elapsed time and
        /// it is always consistent in all the client states (connected/disconnected/ingame).
        /// </summary>
        public double UnscaleElapsedTime;
        /// <summary>
        /// The current unscaled delta time since since last frame.
        /// </summary>
        public float UnscaleDeltaTime;
    }

    static class NetworkTimeHelper
    {
        /// <summary>
        /// Return the current ServerTick value if is a fulltick, otherwise the previous one. The returned
        /// server tick value is correctly wrap around (server tick never equal 0)
        /// </summary>
        /// <param name="networkTime"></param>
        /// <returns></returns>
        static public NetworkTick LastFullServerTick(in NetworkTime networkTime)
        {
            var targetTick = networkTime.ServerTick;
            if (targetTick.IsValid && networkTime.IsPartialTick)
            {
                targetTick.Decrement();
            }
            return targetTick;
        }
    }
}
