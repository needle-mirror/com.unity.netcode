using System;

namespace Unity.NetCode.Editor
{
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
    }
}
