using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    /// <summary>
    ///     Contains a single, discrete 'NetworkConnection' connect / disconnect event.
    ///     For more details, refer to <see cref="NetworkStreamDriver.ConnectionEventsForTick"/>.
    /// </summary>
    public struct NetCodeConnectionEvent
    {
        /// <summary>
        ///     The <see cref="NetworkId" /> of the client whom this event was raised on the behalf of.
        /// </summary>
        public NetworkId Id;

        /// <summary>
        ///     The <see cref="NetworkStreamConnection.Value"/> value of this connection entity.
        /// </summary>
        public NetworkConnection ConnectionId;

        /// <summary>
        ///     The current value of the <see cref="ConnectionState.State" />.
        /// </summary>
        /// <remarks>
        ///     This event is raised any time this state changes. A single connection may therefore have multiple state
        ///     changes per frame.
        /// </remarks>
        public ConnectionState.State State;

        /// <summary>
        ///     Only valid when <see cref="State" /> is <see cref="ConnectionState.State.Disconnected" />.
        /// </summary>
        public NetworkStreamDisconnectReason DisconnectReason;

        /// <summary>
        ///     The entity containing the <see cref="NetworkStreamConnection"/> component.
        /// </summary>
        public Entity ConnectionEntity;

        /// <summary>
        /// Returns a human-readable print of values.
        /// </summary>
        /// <returns>Returns a human-readable print of values.</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString128Bytes ToFixedString()
        {
            FixedString128Bytes s = "NetCodeConnEvt[";
            s.Append(Id.ToFixedString());
            s.Append(',');
            s.Append(ConnectionId.ToFixedString());
            s.Append(',');
            s.Append(State.ToFixedString());
            if (DisconnectReason >= 0)
            {
                s.Append(',');
                s.Append(DisconnectReason.ToFixedString());
            }
            s.Append(']');
            return s;
        }

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }
}
