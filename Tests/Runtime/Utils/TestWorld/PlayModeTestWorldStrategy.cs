#if UNITY_6000_0_OR_NEWER
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.LowLevel;

namespace Unity.NetCode.Tests
{
    internal partial class PlayModeTestWorldStrategy : NetCodeTestWorld.ITestWorldStrategy
    {
        NetCodeTestWorld m_TestWorld;
        PlayerLoopSystem m_OldLoop;
        int m_oldThinClientRequestCount;
        public float DeltaTime { private get; set; } = 1 / 60f;
        public World DefaultWorld => World.DefaultGameObjectInjectionWorld;

        public void Dispose()
        {
            PlayerLoop.SetPlayerLoop(m_OldLoop);
#if UNITY_EDITOR
            MultiplayerPlayModePreferences.RequestedNumThinClients = m_oldThinClientRequestCount;
#endif
        }

        internal void ApplyDT()
        {

        }

        public void Bootstrap(NetCodeTestWorld testWorld)
        {
            this.m_TestWorld = testWorld;

            var mainLoop = PlayerLoop.GetCurrentPlayerLoop();
            var oldLoop = mainLoop;
            m_OldLoop = oldLoop;
#if UNITY_EDITOR
            m_oldThinClientRequestCount = MultiplayerPlayModePreferences.RequestedNumThinClients;
#endif
            List<PlayerLoopSystem> systemList = new();
            systemList.AddRange(mainLoop.subSystemList);
            systemList.Insert(1, new PlayerLoopSystem()
            {
                type = typeof(PlayModeTestWorldStrategy),
                updateDelegate = UpdateTimeFromUpdateLoop
            });

            mainLoop.subSystemList = systemList.ToArray();

            Assert.AreNotEqual(mainLoop.subSystemList, m_OldLoop);
            PlayerLoop.SetPlayerLoop(mainLoop);
        }

        #region world management
        public World CreateClientWorld(string name, bool thinClient, World world = null)
        {
            if (world == null)
            {
                if (thinClient)
                {
                    TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ThinClientSystems); // Ensure CreationOrder is respected.
                    world = ClientServerBootstrap.CreateThinClientWorld(ListToNativeList(NetCodeTestWorld.m_ThinClientSystems));
                }
                else
                {
                    TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ClientSystems); // Ensure CreationOrder is respected.
                    world = ClientServerBootstrap.CreateClientWorld(name, ListToNativeList(NetCodeTestWorld.m_ClientSystems));
                }
            }
            world.GetExistingSystemManaged<UpdateWorldTimeSystem>().Enabled = false;
#if UNITY_EDITOR
            if (thinClient)
                MultiplayerPlayModePreferences.RequestedNumThinClients += 1; // this way any code calls don't conflict with editor side settings and we won't randomly get our code side thin client worlds destroyed by the editor
#endif
            return world;
        }

        public World CreateServerWorld(string name, World world = null)
        {
            if (world == null)
            {
                TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ServerSystems); // Ensure CreationOrder is respected.
                world = ClientServerBootstrap.CreateServerWorld(name, ListToNativeList(NetCodeTestWorld.m_ServerSystems));
            }
            world.GetExistingSystemManaged<UpdateWorldTimeSystem>().Enabled = false;
            return world;
        }

        public World CreateHostWorld(string name, World world = null)
        {
            if (world == null)
            {
                TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_HostSystems); // Ensure CreationOrder is respected.
                world = ClientServerBootstrap.CreateSingleWorldHost(name, ListToNativeList(NetCodeTestWorld.m_HostSystems));
            }
            world.GetExistingSystemManaged<UpdateWorldTimeSystem>().Enabled = false;
            return world;
        }
        NativeList<SystemTypeIndex> ListToNativeList(List<Type> list)
        {

            var nativeList = new NativeList<SystemTypeIndex>(list.Count, Allocator.Temp);
            foreach (var type in list)
            {
                nativeList.Add(TypeManager.GetSystemTypeIndex(type));
            }
            return nativeList;
        }

        public void DisposeClientWorld(World world)
        {
            if (m_TestWorld.AlwaysDispose || world.IsCreated)
                world.Dispose();
        }

        public void DisposeServerWorld(World world)
        {
            if (m_TestWorld.AlwaysDispose || world.IsCreated)
                world.Dispose();
        }
        #endregion

        #region ticking
        public void TickNoAwait(float dt)
        {
            throw new NotSupportedException("Must yield in playmode");
        }

        public async Task TickAsync(float dt, NetcodeAwaitable awaitInstruction = null)
        {
            DeltaTime = dt;
            if (awaitInstruction == null)
            {
                await Awaitable.NextFrameAsync();
                // await Awaitable.EndOfFrameAsync(); // TODO this hangs forever when in batchmode, so hacking this for now with yield in tests that need it
            }
            else
                await awaitInstruction;
        }

        public void TickClientWorld(float dt)
        {
            throw new NotImplementedException();
        }

        public void TickServerWorld(float dt)
        {
            throw new NotImplementedException();
        }

        #endregion

        public void RemoveWorldFromUpdateList(World world)
        {
            throw new NotImplementedException();
        }

        public void DisposeDefaultWorld()
        {
            throw new NotImplementedException();
        }

        void UpdateTimeFromUpdateLoop()
        {
            m_TestWorld.ApplyDT(DeltaTime);
        }
    }
}
#endif
