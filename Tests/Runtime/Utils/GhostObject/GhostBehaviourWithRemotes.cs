namespace Unity.Netcode.Tests
{
    internal partial class GhostBehaviourWithRemotes : GhostBehaviour
    {
        public int testValue = 0;

        [Remote(Directionality.ServerToClient)]
        public void ServerRemote(int arg)
        {
            testValue = arg;
        }

        [Remote(Directionality.ClientToServer)]
        public void ClientRemote(int arg)
        {
            testValue = arg;
        }
    }
}
