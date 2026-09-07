using System;
using NUnit.Framework;
using UnityEngine;

namespace Unity.Netcode.Tests
{
    internal class TestGameObjectSpawner : MonoBehaviour
    {
        internal GameObject prefab;

        void Awake()
        {
            Netcode.OnConnectionEvent += OnConnect;

            if (!Netcode.IsHostRole) return; // This logic isn't valid for DGS
            Assert.IsTrue(Netcode.LocalConnection.IsValid(), "sanity check failed."); // This is mostly for netcode side testing
            SpawnPlayer(Netcode.LocalConnection);
        }

        void SpawnPlayer(Connection connection)
        {
            var go = GameObject.Instantiate(prefab, transform.position, transform.rotation);
            void OnDisconnect(Connection disconnectedConnection, NetcodeConnectionEvent @event)
            {
                if (@event.State != ConnectionState.State.Disconnected)
                    return;
                if (disconnectedConnection.NetworkId == connection.NetworkId)
                {
                    Netcode.OnConnectionEvent -= OnDisconnect;
                    GameObject.Destroy(go);
                }
            }
            Netcode.OnConnectionEvent += OnDisconnect;

            var ghostObject = go.GetComponent<GhostObject>();
            if (ghostObject.HasOwner)
                ghostObject.OwnerNetworkId = connection.NetworkId;
        }

        void OnConnect(Connection connection, NetcodeConnectionEvent @event)
        {
            if (@event.State != ConnectionState.State.Connected)
                return;
            if (!connection.World.IsServer()) return;

            SpawnPlayer(connection);
        }

        void OnDestroy()
        {
            Netcode.OnConnectionEvent -= OnConnect;
        }
    }
}
