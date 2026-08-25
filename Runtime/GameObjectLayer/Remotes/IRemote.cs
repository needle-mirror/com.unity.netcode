using Unity.Entities;

namespace Unity.NetCode
{
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#else
    internal
#endif // NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    readonly struct RemoteInvokeID
    {
        readonly private ulong Value;

        public RemoteInvokeID(ulong value)
        {
            Value = value;
        }

        public bool IsValid() { return Value != 0; }

        public override string ToString() { return Value.ToString(); }
    }

    // Used to indentify an RPC as AutoInvoke so we can pick it up when we receive it and call the linked method
    internal struct RemoteAutoInvoke : IComponentData
    {
        public RemoteInvokeID invokeType;
    }

    /// <summary>
    /// An interface that can be used to declare a 'Remote' struct.
    /// </summary>
    /// <remarks>
    /// Remotes are backed by <see cref="IRpcCommand"/> and have all the same restrictions
    /// </remarks>\
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#else
    internal
#endif // NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    interface IRemote : IRpcCommand
    {
    }
}
