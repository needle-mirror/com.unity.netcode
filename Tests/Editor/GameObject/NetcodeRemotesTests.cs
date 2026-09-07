using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Netcode.Tests;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;
using System.Threading;
using System.Threading.Tasks;

namespace Unity.Netcode.Tests
{
    internal struct TestRemoteInterface : IRemote
    {
        public int dummyData;
    }

    // We need to declare the type outside of the class since nesting doesn't work at the moment
    [Remote]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#else
    internal
#endif // NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    partial struct TestAttributeRemote
    {
        public int dummyData;
    }

    [Remote]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#else
    internal
#endif // NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    partial struct TestHandleRemote
    {
        public int dummyData;

        void Handle()
        {
            NetCodeRemotesTests.TestHandleRemoteCalled = true;
        }
    }

    internal partial class RemoteStaticMethods
    {
        public enum RemoteStaticMethodsTests
        {
            NotRun,
            UndefinedDirectionalityTest,
            ClientToServerDirectionality,
            ServerToClientDirectionality,
        }

        [Remote(Directionality.ClientToServer)]
        public static void ClientToServerDirectionality(int dummy)
        {
            NetCodeRemotesTests.TestStaticMethodCalled = RemoteStaticMethodsTests.ClientToServerDirectionality;
        }

        [Remote(Directionality.ServerToClient)]
        public static void ServerToClientDirectionality(int dummy)
        {
            NetCodeRemotesTests.TestStaticMethodCalled = RemoteStaticMethodsTests.ServerToClientDirectionality;
        }
    }

    [Category(NetcodeTestCategories.Foundational)]
    internal class NetCodeRemotesTests
    {
        public static bool TestHandleRemoteCalled = false;
        public static RemoteStaticMethods.RemoteStaticMethodsTests TestStaticMethodCalled = RemoteStaticMethods.RemoteStaticMethodsTests.NotRun;

        public const ushort TestPort = 6666;
        static NetworkEndpoint TestListenEndpoint = NetworkEndpoint.LoopbackIpv4.WithPort(TestPort);

        private void TestRemoteInvokeAndQuery<TRemote>() where TRemote : unmanaged, IRemote
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            int numExpectedClientToServerRemotes = ClientServerBootstrap.ClientWorlds.Count;
            int numExpectedServerToClientRemotes = ClientServerBootstrap.ServerWorlds.Count* ClientServerBootstrap.ClientWorlds.Count;

            // Invoke a remote to the server only
            Netcode.Remote.Invoke<TRemote>(new TRemote(), ClientServerBootstrap.ClientWorlds, Directionality.ClientToServer);
            testWorld.TickMultiple(3);

            // we should receive only one on ther server
            Assert.That(Netcode.Remote.Query<TRemote>(ClientServerBootstrap.ServerWorlds).Count, Is.EqualTo(numExpectedClientToServerRemotes), "After invoking a remote on the client we should have received one on the server.");
            // and none on the client
            Assert.That(Netcode.Remote.Query<TRemote>(ClientServerBootstrap.ClientWorlds).Count, Is.EqualTo(0), "After invoking a remote on the client we should have received none on the client.");

            // Invoke a remote to all clients
            Netcode.Remote.Invoke<TRemote>(new TRemote(), ClientServerBootstrap.ServerWorlds, Directionality.ServerToClient);
            testWorld.TickMultiple(3);

            // we should receive only one on ther client
            Assert.That(Netcode.Remote.Query<TRemote>(ClientServerBootstrap.ClientWorlds).Count, Is.EqualTo(numExpectedServerToClientRemotes), "After invoking a remote on the server we should have received one on the client.");

            // and none on the server
            Assert.That(Netcode.Remote.Query<TRemote>(ClientServerBootstrap.ServerWorlds).Count, Is.EqualTo(0), "After invoking a remote on the server we should have received none on the server.");
        }

        [Test]
        public void TestInheritedRemoteInvokeAndQuery()
        {
            TestRemoteInvokeAndQuery<TestRemoteInterface>();
        }

        [Test]
        [Category(NetcodeTestCategories.Smoke)]
        public void TestAttributeRemoteInvokeAndQuery()
        {
            TestRemoteInvokeAndQuery<TestAttributeRemote>();
        }

        [Test]
        public void TestAutoInvokeHandleCalled()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            // We reset the state here before we invoke
            TestHandleRemoteCalled = false;

            // Invoke the remote
            Netcode.Remote.Invoke<TestHandleRemote>(new TestHandleRemote(), ClientServerBootstrap.ClientWorlds, Directionality.ClientToServer);
            testWorld.TickMultiple(6);
            // so the handle function should have been called which is the only thing which would set this to true
            Assert.That(TestHandleRemoteCalled, Is.EqualTo(true));
        }

        [Test]
        public void TestRemoteMethodDirectionality_ClientToServer()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            // We reset the state here before we invoke
            TestStaticMethodCalled = RemoteStaticMethods.RemoteStaticMethodsTests.NotRun;

            // Invoke the remote
            RemoteStaticMethods.ClientToServerDirectionality(23);
            Assert.That(TestStaticMethodCalled, Is.EqualTo(RemoteStaticMethods.RemoteStaticMethodsTests.NotRun), "sanity check failed");
            testWorld.TickMultiple(6);
            // so the handle function should have been called which is the only thing which would set this to true
            Assert.That(TestStaticMethodCalled, Is.EqualTo(RemoteStaticMethods.RemoteStaticMethodsTests.ClientToServerDirectionality));
        }

        [Test]
        public void TestRemoteMethodDirectionality_ServerToClient()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            // We reset the state here before we invoke
            TestStaticMethodCalled = RemoteStaticMethods.RemoteStaticMethodsTests.NotRun;

            // Invoke the remote
            RemoteStaticMethods.ServerToClientDirectionality(23);
            Assert.That(TestStaticMethodCalled, Is.EqualTo(RemoteStaticMethods.RemoteStaticMethodsTests.NotRun), "sanity check failed");
            testWorld.TickMultiple(6);
            // so the handle function should have been called which is the only thing which would set this to true
            Assert.That(TestStaticMethodCalled, Is.EqualTo(RemoteStaticMethods.RemoteStaticMethodsTests.ServerToClientDirectionality));
        }
    }
}
