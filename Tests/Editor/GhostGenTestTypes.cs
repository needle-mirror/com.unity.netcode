using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    // This test class is coupled with GhostGenTestUtils, which holds the types used
    internal class GhostGenTestTypes
    {
        // TODO - Test fragmented unreliable sends by having two large ICommandDatas on 1 client.
        // Tests that all supported ghost values are replicated from Server->Client on IComponentData via ghost fields
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void GhostValuesAreSerialized_IComponentData()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGenTestUtils.GhostGenTestTypesConverter_IComponentData();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                var serverEntity = testWorld.TryGetSingletonEntity<GhostGenTestUtils.GhostGenTestType_IComponentData>(testWorld.ServerWorld);
                Assert.AreNotEqual(Entity.Null, serverEntity);
                var newClampValues = GhostGenTestUtils.CreateGhostValuesClamp_Values(42, serverEntity);
                var newClampStrings = GhostGenTestUtils.CreateGhostValuesClamp_Strings(42);
                var newInterpolateValues = GhostGenTestUtils.CreateGhostValuesInterpolate(42);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestUtils.GhostGenTestType_IComponentData {GhostGenTypesClamp_Values = newClampValues, GhostGenTypesClamp_Strings = newClampStrings, GhostGenTypesInterpolate = newInterpolateValues});

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                var clientEntity = testWorld.TryGetSingletonEntity<GhostGenTestUtils.GhostGenTestType_IComponentData>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEntity);

                var serverValues = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGenTestUtils.GhostGenTestType_IComponentData>(serverEntity);
                var clientValues = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostGenTestUtils.GhostGenTestType_IComponentData>(clientEntity);

                GhostGenTestUtils.VerifyGhostValuesClamp_Values(false, serverValues.GhostGenTypesClamp_Values, clientValues.GhostGenTypesClamp_Values, serverEntity, clientEntity);
                GhostGenTestUtils.VerifyGhostValuesClamp_Strings( serverValues.GhostGenTypesClamp_Strings, clientValues.GhostGenTypesClamp_Strings);
                GhostGenTestUtils.VerifyGhostValuesInterpolate(serverValues.GhostGenTypesInterpolate, clientValues.GhostGenTypesInterpolate);

                newClampValues = GhostGenTestUtils.CreateGhostValuesClamp_Values(43, serverEntity);
                newClampStrings = GhostGenTestUtils.CreateGhostValuesClamp_Strings(43);
                newInterpolateValues = GhostGenTestUtils.CreateGhostValuesInterpolate(43);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestUtils.GhostGenTestType_IComponentData {GhostGenTypesClamp_Values = newClampValues, GhostGenTypesClamp_Strings = newClampStrings, GhostGenTypesInterpolate = newInterpolateValues});

                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // Assert that replicated version is correct
                serverValues = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGenTestUtils.GhostGenTestType_IComponentData>(serverEntity);
                clientValues = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostGenTestUtils.GhostGenTestType_IComponentData>(clientEntity);

                GhostGenTestUtils.VerifyGhostValuesClamp_Values(false, serverValues.GhostGenTypesClamp_Values, clientValues.GhostGenTypesClamp_Values, serverEntity, clientEntity);
                GhostGenTestUtils.VerifyGhostValuesClamp_Strings( serverValues.GhostGenTypesClamp_Strings, clientValues.GhostGenTypesClamp_Strings);
                GhostGenTestUtils.VerifyGhostValuesInterpolate(serverValues.GhostGenTypesInterpolate, clientValues.GhostGenTypesInterpolate);
            }
        }

        // Tests that all supported values are replicated from Client=>Server on ICommandData via command target
        // This uses multiple test cases, because there is a size limit on ICommandData, so we split the struct into multiple values
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void ValuesAreSerialized_ICommandData_Values()
        {
            Func<NetworkTick, int, Entity, GhostGenTestUtils.GhostGenTestType_ICommandData_Values> creator =
                GhostGenTestUtils.CreateICommandDataValues_Values;
            Action<GhostGenTestUtils.GhostGenTestType_ICommandData_Values, GhostGenTestUtils.GhostGenTestType_ICommandData_Values, Entity, Entity>
                verifier = GhostGenTestUtils.VerifyICommandData_Values;
            ValuesAreSerialized_ICommandData(creator, verifier);
        }

        [Test]
        public void ValuesAreSerialized_ICommandData_Strings()
        {
            Func<NetworkTick, int, Entity, GhostGenTestUtils.GhostGenTestType_ICommandData_Strings> creator =
                GhostGenTestUtils.CreateICommandDataValues_Strings;
            Action<GhostGenTestUtils.GhostGenTestType_ICommandData_Strings, GhostGenTestUtils.GhostGenTestType_ICommandData_Strings, Entity, Entity>
                verifier = GhostGenTestUtils.VerifyICommandData_Strings;
            ValuesAreSerialized_ICommandData(creator, verifier);
        }

        /// <summary>
        /// Tests that ICommandData values are serialized properly. Uses generics since there are multiple ICommandData that needs to be split to avoid duplicating code between this and the IComponentData GhostValue tests above.
        /// </summary>
        /// <param name="creator">Function that generates the values of ICommandData</param>
        /// <param name="verifier">Function that verifies the values two ICommandData, intended to verify that the values are the same between client and server</param>
        public void ValuesAreSerialized_ICommandData<T>(Func<NetworkTick, int, Entity, T> creator, Action<T, T, Entity, Entity> verifier) where T : unmanaged, ICommandData
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGenTestUtils.GhostGenTestTypesConverter_IComponentData();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                // We need a ghost on the server to verify that commands can also send ghost entities
                // We don't care what is on the ghost though, that is tested in the IComponentData variant of this test
                testWorld.SpawnOnServer(ghostGameObject);

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the world run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Add and set server command target
                var serverConnection = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                Assert.AreNotEqual(Entity.Null, serverConnection);
                testWorld.ServerWorld.EntityManager.AddBuffer<T>(serverConnection);
                testWorld.ServerWorld.EntityManager.AddComponent<CommandTarget>(serverConnection);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverConnection, new CommandTarget{targetEntity = serverConnection});

                // Add and set client command target
                var clientConnection = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientConnection);
                testWorld.ClientWorlds[0].EntityManager.AddBuffer<T>(clientConnection);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandTarget>(clientConnection);
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientConnection, new CommandTarget{targetEntity = clientConnection});

                // Add a command to client
                var clientGhostEntity = testWorld.TryGetSingletonEntity<GhostGenTestUtils.GhostGenTestType_IComponentData>(testWorld.ClientWorlds[0]); // Ghost entity
                Assert.AreNotEqual(Entity.Null, clientGhostEntity);
                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<T>(clientConnection);
                var clientTick = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).InputTargetTick;
                var newValues = creator(clientTick, 42, clientGhostEntity);
                clientBuffer.AddCommandData(newValues);

                for (int i = 0; i < 4; i++)
                    testWorld.Tick();

                // Verify values
                clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<T>(clientConnection);
                clientBuffer.GetDataAtTick(clientTick, out var clientValues);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<T>(serverConnection);
                serverBuffer.GetDataAtTick(clientTick, out var serverValues);
                var serverGhostEntity = testWorld.TryGetSingletonEntity<GhostGenTestUtils.GhostGenTestType_IComponentData>(testWorld.ServerWorld); // Ghost entity
                Assert.AreNotEqual(Entity.Null, serverGhostEntity);
                verifier(serverValues, clientValues, serverGhostEntity, clientGhostEntity);
            }
        }

        // Tests that all supported values are replicated from Client=>Server on IInputComponentData
        // This uses multiple test cases, because there is a size limit on ICommandData, so we split the struct into multiple values
        [Test]
        public void ValuesAreSerialized_IInputComponentData_Values()
        {
            Func<int, Entity, GhostGenTestUtils.GhostGenTestType_IInputComponentData_Values> creator =
                GhostGenTestUtils.CreateIInputComponentDataValues_Values;
            Action<GhostGenTestUtils.GhostGenTestType_IInputComponentData_Values, GhostGenTestUtils.GhostGenTestType_IInputComponentData_Values, Entity, Entity>
                verifier = GhostGenTestUtils.VerifyIInputComponentData_Values;
            ValuesAreSerialized_IInputCommandData(creator, verifier, new GhostGenTestUtils.GhostGenTestTypesConverter_IInputComponentData_Values());
        }

        [Test]
        public void ValuesAreSerialized_IInputComponentData_Strings()
        {
            Func<int, Entity, GhostGenTestUtils.GhostGenTestType_IInputComponentData_Strings> creator =
                GhostGenTestUtils.CreateIInputComponentDataValues_Strings;
            Action<GhostGenTestUtils.GhostGenTestType_IInputComponentData_Strings, GhostGenTestUtils.GhostGenTestType_IInputComponentData_Strings, Entity, Entity>
                verifier = GhostGenTestUtils.VerifyIInputComponentData_Strings;
            ValuesAreSerialized_IInputCommandData(creator, verifier, new GhostGenTestUtils.GhostGenTestTypesConverter_IInputComponentData_Strings());
        }

        public void ValuesAreSerialized_IInputCommandData<T, U>(Func<int, Entity, T> creator, Action<T, T, Entity, Entity> verifier, U converter) where T : unmanaged, IInputComponentData where U : TestNetCodeAuthoring.IConverter
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                // Set up ghost
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = converter;
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.SupportedGhostModes = GhostModeMask.All;
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted; // Ghost must be predicted for AutoCommandTarget to work
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                // Connect and make sure the connection could be established
                testWorld.CreateWorlds(true, 2);
                testWorld.Connect();
                testWorld.GoInGame();

                // Spawn ghost and set owner
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId});


                // Let the world run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Change input on client
                var clientGhostEntity = testWorld.TryGetSingletonEntity<T>(testWorld.ClientWorlds[0]); // Ghost entity
                Assert.AreNotEqual(Entity.Null, clientGhostEntity);
                var newValues = creator(42, clientGhostEntity);
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientGhostEntity, newValues);

                // Tick to ensure data has been changed
                for (int i = 0; i < 16; i++)
                {
                    testWorld.Tick();
                    var testValues = testWorld.GetSingleton<T>(testWorld.ServerWorld);
                }

                // Verify values
                //var clientValues = testWorld.GetSingleton<T>(testWorld.ClientWorlds[0]);
                var serverGhostEntity = testWorld.TryGetSingletonEntity<T>(testWorld.ServerWorld); // Ghost entity
                Assert.AreNotEqual(Entity.Null, serverGhostEntity);
                var serverValues = testWorld.GetSingleton<T>(testWorld.ServerWorld);
                verifier(serverValues, newValues, serverGhostEntity, clientGhostEntity);
            }
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void ValuesAreSerialized_IRpc()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGenTestUtils.GhostGenTestTypesConverter_IComponentData();
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                // We need a ghost on the server to verify that commands can also send ghost entities
                // We don't care what is on the ghost though, that is tested in the IComponentData variant of this test
                testWorld.SpawnOnServer(ghostGameObject);

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the world run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Create RPC on client
                var clientGhostEntity = testWorld.TryGetSingletonEntity<GhostGenTestUtils.GhostGenTestType_IComponentData>(testWorld.ClientWorlds[0]); // Ghost entity
                Assert.AreNotEqual(Entity.Null, clientGhostEntity);
                var rpc = testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(GhostGenTestUtils.GhostGenTestType_IRpc),
                    typeof(SendRpcCommandRequest));
                var clientValues = GhostGenTestUtils.CreateIRpcValues(42, clientGhostEntity);
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(rpc, clientValues);

                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostGenTestUtils.GhostGenTestType_IRpc));
                int maxTicks = 100;
                while (query.CalculateEntityCount() < 1)
                {
                    testWorld.Tick();
                    maxTicks--;
                    if (maxTicks <= 0)
                        Debug.LogError("Max ticks reached without finding RPC on server");
                }

                // Verify server values
                var serverGhostEntity = testWorld.TryGetSingletonEntity<GhostGenTestUtils.GhostGenTestType_IComponentData>(testWorld.ServerWorld); // Ghost entity
                Assert.AreNotEqual(Entity.Null, serverGhostEntity);
                var serverValues = testWorld.GetSingleton<GhostGenTestUtils.GhostGenTestType_IRpc>(testWorld.ServerWorld);
                GhostGenTestUtils.VerifyIRpc(serverValues, clientValues, serverGhostEntity, clientGhostEntity);
                testWorld.ServerWorld.EntityManager.DestroyEntity(query);

                // Create RPC on server
                rpc = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(GhostGenTestUtils.GhostGenTestType_IRpc),
                    typeof(SendRpcCommandRequest));
                serverValues = GhostGenTestUtils.CreateIRpcValues(43, serverGhostEntity);
                testWorld.ServerWorld.EntityManager.SetComponentData(rpc, serverValues);

                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostGenTestUtils.GhostGenTestType_IRpc));
                maxTicks = 100;
                while (query.CalculateEntityCount() < 1)
                {
                    testWorld.Tick();
                    maxTicks--;
                    if (maxTicks <= 0)
                        Debug.LogError("Max ticks reached without finding RPC on server");
                }

                // Verify server values
                clientValues = testWorld.GetSingleton<GhostGenTestUtils.GhostGenTestType_IRpc>(testWorld.ClientWorlds[0]);
                GhostGenTestUtils.VerifyIRpc(serverValues, clientValues, serverGhostEntity, clientGhostEntity);
                testWorld.ClientWorlds[0].EntityManager.DestroyEntity(query);
            }
        }

        [Test]
        public void CommandTooBig()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                // Setup
                testWorld.Bootstrap(true);

                testWorld.CreateWorlds(true, 1);

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the world run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Add and set server command target
                var serverConnection = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                Assert.AreNotEqual(Entity.Null, serverConnection);
                testWorld.ServerWorld.EntityManager.AddBuffer<GhostGenTestUtils.GhostGenTestType_ICommandData_Strings>(serverConnection);
                testWorld.ServerWorld.EntityManager.AddComponent<CommandTarget>(serverConnection);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverConnection, new CommandTarget{targetEntity = serverConnection});

                // Add and set client command target
                var clientConnection = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientConnection);
                testWorld.ClientWorlds[0].EntityManager.AddBuffer<GhostGenTestUtils.GhostGenTestType_ICommandData_Strings>(clientConnection);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<CommandTarget>(clientConnection);
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientConnection, new CommandTarget{targetEntity = clientConnection});

                // Add MASSIVE command:
                var newInvalidClampValues = GhostGenTestUtils.CreateTooLargeGhostValuesStrings();
                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenTestUtils.GhostGenTestType_ICommandData_Strings>(clientConnection);
                var clientTick = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).InputTargetTick;
                clientBuffer.AddCommandData(new GhostGenTestUtils.GhostGenTestType_ICommandData_Strings()
                    { Tick = clientTick, GhostGenTypesClamp_Strings = newInvalidClampValues });


                for (int i = 0; i < 1; ++i)
                    testWorld.Tick();

                // Expect it to log an error as it's far too large.
                LogAssert.Expect(LogType.Error, new Regex("the serialized payload is too large"));
            }
        }

        internal struct GhostGenBigStruct : IComponentData
        {
            //Add 100 int fields and check they are serialized correctly
            [GhostField] public int field000;
            [GhostField] public int field001;
            [GhostField] public int field002;
            [GhostField] public int field003;
            [GhostField] public int field004;
            [GhostField] public int field005;
            [GhostField] public int field006;
            [GhostField] public int field007;
            [GhostField] public int field008;
            [GhostField] public int field009;
            [GhostField] public int field010;
            [GhostField] public int field011;
            [GhostField] public int field012;
            [GhostField] public int field013;
            [GhostField] public int field014;
            [GhostField] public int field015;
            [GhostField] public int field016;
            [GhostField] public int field017;
            [GhostField] public int field018;
            [GhostField] public int field019;
            [GhostField] public int field020;
            [GhostField] public int field021;
            [GhostField] public int field022;
            [GhostField] public int field023;
            [GhostField] public int field024;
            [GhostField] public int field025;
            [GhostField] public int field026;
            [GhostField] public int field027;
            [GhostField] public int field028;
            [GhostField] public int field029;
            [GhostField] public int field030;
            [GhostField] public int field031;
            [GhostField] public int field032;
            [GhostField] public int field033;
            [GhostField] public int field034;
            [GhostField] public int field035;
            [GhostField] public int field036;
            [GhostField] public int field037;
            [GhostField] public int field038;
            [GhostField] public int field039;
            [GhostField] public int field040;
            [GhostField] public int field041;
            [GhostField] public int field042;
            [GhostField] public int field043;
            [GhostField] public int field044;
            [GhostField] public int field045;
            [GhostField] public int field046;
            [GhostField] public int field047;
            [GhostField] public int field048;
            [GhostField] public int field049;
            [GhostField] public int field050;
            [GhostField] public int field051;
            [GhostField] public int field052;
            [GhostField] public int field053;
            [GhostField] public int field054;
            [GhostField] public int field055;
            [GhostField] public int field056;
            [GhostField] public int field057;
            [GhostField] public int field058;
            [GhostField] public int field059;
            [GhostField] public int field060;
            [GhostField] public int field061;
            [GhostField] public int field062;
            [GhostField] public int field063;
            [GhostField] public int field064;
            [GhostField] public int field065;
            [GhostField] public int field066;
            [GhostField] public int field067;
            [GhostField] public int field068;
            [GhostField] public int field069;
            [GhostField] public int field070;
            [GhostField] public int field071;
            [GhostField] public int field072;
            [GhostField] public int field073;
            [GhostField] public int field074;
            [GhostField] public int field075;
            [GhostField] public int field076;
            [GhostField] public int field077;
            [GhostField] public int field078;
            [GhostField] public int field079;
            [GhostField] public int field080;
            [GhostField] public int field081;
            [GhostField] public int field082;
            [GhostField] public int field083;
            [GhostField] public int field084;
            [GhostField] public int field085;
            [GhostField] public int field086;
            [GhostField] public int field087;
            [GhostField] public int field088;
            [GhostField] public int field089;
            [GhostField] public int field090;
            [GhostField] public int field091;
            [GhostField] public int field092;
            [GhostField] public int field093;
            [GhostField] public int field094;
            [GhostField] public int field095;
            [GhostField] public int field096;
            [GhostField] public int field097;
            [GhostField] public int field098;
            [GhostField] public int field099;
            [GhostField] public int field100;

            public void Increment()
            {
                field000 += 1;
                field001 += 1;
                field002 += 1;
                field003 += 1;
                field004 += 1;
                field005 += 1;
                field006 += 1;
                field007 += 1;
                field008 += 1;
                field009 += 1;
                field010 += 1;
                field011 += 1;
                field012 += 1;
                field013 += 1;
                field014 += 1;
                field015 += 1;
                field016 += 1;
                field017 += 1;
                field018 += 1;
                field019 += 1;
                field020 += 1;
                field021 += 1;
                field022 += 1;
                field023 += 1;
                field024 += 1;
                field025 += 1;
                field026 += 1;
                field027 += 1;
                field028 += 1;
                field029 += 1;
                field030 += 1;
                field031 += 1;
                field032 += 1;
                field033 += 1;
                field034 += 1;
                field035 += 1;
                field036 += 1;
                field037 += 1;
                field038 += 1;
                field039 += 1;
                field040 += 1;
                field041 += 1;
                field042 += 1;
                field043 += 1;
                field044 += 1;
                field045 += 1;
                field046 += 1;
                field047 += 1;
                field048 += 1;
                field049 += 1;
                field050 += 1;
                field051 += 1;
                field052 += 1;
                field053 += 1;
                field054 += 1;
                field055 += 1;
                field056 += 1;
                field057 += 1;
                field058 += 1;
                field059 += 1;
                field060 += 1;
                field061 += 1;
                field062 += 1;
                field063 += 1;
                field064 += 1;
                field065 += 1;
                field066 += 1;
                field067 += 1;
                field068 += 1;
                field069 += 1;
                field070 += 1;
                field071 += 1;
                field072 += 1;
                field073 += 1;
                field074 += 1;
                field075 += 1;
                field076 += 1;
                field077 += 1;
                field078 += 1;
                field079 += 1;
                field080 += 1;
                field081 += 1;
                field082 += 1;
                field083 += 1;
                field084 += 1;
                field085 += 1;
                field086 += 1;
                field087 += 1;
                field088 += 1;
                field089 += 1;
                field090 += 1;
                field091 += 1;
                field092 += 1;
                field093 += 1;
                field094 += 1;
                field095 += 1;
                field096 += 1;
                field097 += 1;
                field098 += 1;
                field099 += 1;
                field100 += 1;
            }
        }

        internal class GhostGenBigStructConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent(entity, new GhostGenBigStruct {});
            }
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void StructWithLargeNumberOfFields()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGenBigStructConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                //Use reflection.. just because if is faster
                var data = default(GhostGenBigStruct);
                unsafe
                {
                    var values = (int*)UnsafeUtility.AddressOf(ref data);
                    for (int i = 0; i < 100; ++i)
                    {
                        values[i] = i;
                    }
                }
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, data);

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEntity = testWorld.TryGetSingletonEntity<GhostGenBigStruct>(testWorld.ClientWorlds[0]);
                var clientData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostGenBigStruct>(clientEntity);
                var serverData = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGenBigStruct>(serverEntity);
                Assert.AreEqual(serverData, clientData);
            }
        }
    }
}
