using System;
using Unity.Burst;

namespace Unity.NetCode
{
    public struct PortableFunctionPointer<T> where T : Delegate
    {
        public PortableFunctionPointer(T executeDelegate)
        {
#if !UNITY_DOTSPLAYER
            Ptr = BurstCompiler.CompileFunctionPointer(executeDelegate);
#else
            Ptr = executeDelegate;
#endif
        }

#if !UNITY_DOTSPLAYER
        internal readonly FunctionPointer<T> Ptr;
#else
        internal readonly T Ptr;
#endif
    }
}