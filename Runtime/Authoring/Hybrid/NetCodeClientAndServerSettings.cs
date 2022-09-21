#if UNITY_EDITOR
using System;
using Unity.Entities.Build;
using UnityEngine;

namespace Authoring.Hybrid
{
    public class NetCodeClientAndServerSettings: DotsPlayerSettings
    {
        [SerializeField]
        public NetcodeConversionTarget NetcodeTarget = NetcodeConversionTarget.ClientAndServer;
        [SerializeField]
        public string[] AdditionalScriptingDefines = Array.Empty<string>();

        public override BakingSystemFilterSettings GetFilterSettings()
        {
            return null;
        }

        public override string[] GetAdditionalScriptingDefines()
        {
            return AdditionalScriptingDefines;
        }
    }
}
#endif
