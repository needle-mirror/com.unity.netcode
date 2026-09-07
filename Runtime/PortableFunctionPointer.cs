using System;
using Unity.Burst;
using Unity.Profiling;
using UnityEngine.Scripting.APIUpdating;

namespace Unity.Netcode
{
    ///<summary>
    ///Simple RAII-like wrapper that simplify making C# function delegate burst compatible.
    ///</summary>
    ///<typeparam name="T">the function delegate type</typeparam>
    [MovedFrom(true, "Unity.NetCode")]
    public struct PortableFunctionPointer<T> where T : Delegate
    {
        static readonly ProfilerMarker s_marker = new("Netcode PortableFunctionPointer compile function");
        /// <summary>
        /// Convert the delegate to a burst-compatible function pointer.
        /// </summary>
        /// <param name="executeDelegate">the function delegate</param>
        public PortableFunctionPointer(T executeDelegate)
        {
            using var a = s_marker.Auto();
            Ptr = BurstCompiler.CompileFunctionPointer(executeDelegate);
        }

        internal readonly FunctionPointer<T> Ptr;
    }
}
