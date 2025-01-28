using System.Runtime.InteropServices;
using Unity.Entities;
using Unity.Physics;

namespace Unity.NetCode
{
    /// <summary>
    /// Singleton entity that allow to configure the NetCode LagCompensation system.
    /// If the singleton does not exist the PhysicsWorldHistory system will not run.
    /// If you want to use PhysicsWorldHistory in a prediction system the config must
    /// exist in both client and server worlds, but in the client world HistorySize can
    /// be different from the server - usually 1 is enough on the client.
    /// </summary>
    public struct LagCompensationConfig : IComponentData
    {
        /// <summary>
        /// The number of physics world states that are backed up on the server.
        /// This cannot be more than the maximum capacity (of <see cref="PhysicsWorldHistory.RawHistoryBufferMaxCapacity"/>).
        /// Leaving the value at zero will give you the default value (16).
        /// </summary>
        /// <remarks>
        /// Must be a power of 2 for the ring-buffer to return correct values when
        /// <see cref="NetworkTime.ServerTick"/> wraps uint max value.
        /// </remarks>
        public int ServerHistorySize;
        /// <summary>
        /// The number of physics world states that are backed up on the client.
        /// This cannot be more than the maximum capacity (of <see cref="PhysicsWorldHistory.RawHistoryBufferMaxCapacity"/>),
        /// but typically only needs to be around ~4 (to allow the client to check its own shots against the historic entries).
        /// Setting the value to 0 will disable recording the physics history on the client.
        /// By default, the history size on the client is 1.
        /// </summary>
        /// <remarks>
        /// Must be 0 (OFF/DISABLED), otherwise a power of 2 (for the ring-buffer to return correct values when
        /// <see cref="NetworkTime.ServerTick"/> wraps uint max value).
        /// </remarks>
        public int ClientHistorySize;
        /// <summary>
        /// Determines whether or not netcode's call to <see cref="CollisionWorld.Clone()"/> deep copies dynamic colliders.
        /// Set this to true if you want <see cref="PhysicsWorldHistory"/> to return accurate query information for
        /// historic queries against dynamic entities.
        /// </summary>
        /// <remarks>
        /// Also note: From netcode's POV, querying entities which have not been deep copied is considered
        /// "undefined behaviour". The only requirement we make here is that the Physics query itself will not throw an
        /// exception (as safely handling this flow is a Physics requirement).
        /// </remarks>
        [MarshalAs(UnmanagedType.U1)]
        public bool DeepCopyDynamicColliders;
        /// <summary>
        /// Determines whether or not netcode's call to <see cref="CollisionWorld.Clone()"/> deep copies static colliders.
        /// Set this to true if you want <see cref="PhysicsWorldHistory"/> to return accurate query information for
        /// historic queries against static entities. Only needed if (presumably rare) changes to static collider
        /// information (including geometry changes) causes invalid collision detection, which should be an exceptional case.
        /// </summary>
        /// <remarks>
        /// For large worlds, copying static geometry is best avoided. Instead: Run two queries: One against the current
        /// static geometry (using layers), then use that collision hit result to set the max cast distance for the
        /// dynamic colliders query.
        /// <br/><br/>If your games static geometry occasionally changes (e.g. chopping down a tree), manually copy these
        /// bodies colliders via <see cref="PhysicsWorldHistorySingleton.DeepCopyRigidBodyCollidersWhitelist"/>.
        /// <br/><br/>Also note: From netcode's POV, querying entities which have not been deep copied is considered
        /// "undefined behaviour". The only requirement we make here is that the Physics query itself will not throw an
        /// exception (as safely handling this flow is a Physics requirement).
        /// </remarks>
        [MarshalAs(UnmanagedType.U1)]
        public bool DeepCopyStaticColliders;
    }
}
