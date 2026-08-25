<!-- ADD BACK IN ONCE GAMEOBJECTLAYER is Enabled
# Remotes

Remotes provide an [RPC](../rpcs.md) backed mechanism for sending state between connections.

You can declare remotes as an attribute using [Remote], or as a struct by extending [IRemote](../../api/Unity.NetCode.IRemote.html).

# Using the [Remote] attribute

You can declare the [Remote] attribute on either a struct, a static method, or a GhostBehaviour method.

```c#
[Remote]
public partial struct ExampleRemote
{
    public int intData;
    public short shortData;
}

class SomeClass
{
    [Remote(Directionality.ServerToClient)]
    static void ExampleRemoteMethod( int dummy )
    {
        // code to execute on the remote instance
    }
}

partial class SomeBehaviour : GhostBehaviour
{
    [Remote(Directionality.ClientToServer)]
    void ExampleGhostRemote( int dummy )
    {
        // code to execute on the remote instance
    }
}
```

When using [Remote] on a struct, it adds the IRemote interface for you and operates in the same way as described in the [IRemote section](#using-iremote).

When using the [Remote] attribute on methods, it can be specified on either static or GhostBehaviour methods. Static methods will be invoked and actioned remotely inside of the Early update. For GhostBehaviour methods, the remote will be actioned inside the Early update but will target the matching GhostBehaviour instance on the remote instance.

You need to specify a direction for the remote, either `ClientToServer` or `ServerToClient`. Currently there is no way to filter which clients you would like to send a remote method to.

# Using IRemote

```c#
public struct ExampleRemote : IRemote
{
    public int intData;
    public short shortData;
}
```

Once declared, remotes can be invoked by calling [Invoke](../../api/Unity.NetCode.Remote.html#Unity_NetCode_Remote_Invoke__1___0_), passing in the instance you wish to transmit.

```c#
Netcode.Remote.Invoke<ExampleRemote>(new ExampleRemote{intDatra=5,shortData=6});
```

Remotes will be invoked on all connections in all worlds by default, or you can specify a connection or list of connections to target, as in the following code example:

```c#
Netcode.Remote.Invoke<ExampleRemote>(new ExampleRemote{intDatra=5,shortData=6}, Netcode.m_Instance.m_Client.Connection);
```

Remotes can be queried by calling [Query](../../api/Unity.NetCode.Remote.html#Unity_NetCode_Remote_Query__1) and providing the type you wish to query. The function returns all remotes of that type received since a query for that type was last called.

```c#
var allRemotes = Netcode.Remote.Query<ExampleRemote>();

foreach ( var r in allRemotes )
{
    Debug.Log( $"ExampleRemote received with values intData={r.intData} shortData={r.shortData}" );
}
```

Query will query all connections in all worlds unless you specify a connection to limit the scope of the query. For example, a query on a host will contain all the received remotes in both the client and server world without any scope limits.

```c#
var allRemotesFromAllClients = Netcode.Remote.Query<ExampleRemote>(Netcode.m_Instance.m_Server.Connections);

foreach ( var r in allRemotesFromAllClients )
{
    Debug.Log( $"ExampleRemote received with values intData={r.intData} shortData={r.shortData}" );
}
```

If you want to associate data to a specific client, then you should iterate and call per client.

```c#
foreach ( var c in Netcode.m_Instance.m_Server.Connections )
{
    var allRemotesFromAClient = Netcode.Remote.Query<ExampleRemote>(c);

    Debug.Log( $"ExampleRemote received from {c} with values intData={r.intData} shortData={r.shortData}" );
}
```

You can also specify a `Handle` method on the struct that will be automatically invoked on the receiving instance so that the remote will be consumed and not available to the Query method.

```c#
public struct ExampleRemote : IRemote
{
    public int intData;
    public short shortData;

    void Handle()
    {
        // I will be invoked when this remote is received
    }
}
```
-->