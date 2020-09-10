using System;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    [Obsolete("ConvertToClientServerEntity has been deprecated. Please use the sub-scene conversion workflows instead. (RemovedAfter 2020-12-01).")]
    public class ConvertToClientServerEntity : ConvertToEntity
    {
        [Flags]
        public enum ConversionTargetType
        {
            None = 0x0,
            Client = 0x1,
            Server = 0x2,
            ClientAndServer = 0x3
        }

        [SerializeField] public ConversionTargetType ConversionTarget = ConversionTargetType.ClientAndServer;

        [HideInInspector] public bool canDestroy = false;

        void Awake()
        {
            var system = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<ConvertToEntitySystem>();

            bool hasValidTargetWorld = false;
            foreach (var world in World.All)
            {
                bool convertToClient = world.GetExistingSystem<ClientSimulationSystemGroup>() != null;
                bool convertToServer = world.GetExistingSystem<ServerSimulationSystemGroup>() != null;

                hasValidTargetWorld |= convertToClient || convertToServer;
                convertToClient &= (ConversionTarget & ConversionTargetType.Client) != 0;
                convertToServer &= (ConversionTarget & ConversionTargetType.Server) != 0;

                if (convertToClient || convertToServer)
                    system.AddToBeConverted(world, this);
            }

            if (!hasValidTargetWorld)
            {
                //UnityEngine.Debug.LogWarning("ConvertEntity failed because there was no Client or Server Worlds", this);
            }
        }
    }
}
