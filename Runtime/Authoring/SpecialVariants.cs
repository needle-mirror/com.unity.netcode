namespace Unity.NetCode
{
    /// <summary>
    /// Special universal component variant that can be assigned to any component and/or buffer when configuring
    /// the GhostComponentSerializerCollectionSystemGroup. Mostly used for stripping components from the server-side ghost prefabs
    /// </summary>
    sealed public class ClientOnlyVariant
    {
    }

    /// <summary>
    /// Special universal component variant that can be assigned to any component and/or buffer. When a component
    /// serializer is set to DontSerializeVariant, the component itself is not stripped from the client or server version of
    /// the prefab and at runtime it is not serialized and sent to the clients.
    /// </summary>
    sealed public class DontSerializeVariant
    {
    }
}
