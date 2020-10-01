using System;
using Unity.Burst;

namespace Unity.NetCode
{
    public struct PortableFunctionPointer<T> where T : Delegate
    {
        public PortableFunctionPointer(T executeDelegate)
        {
            Ptr = BurstCompiler.CompileFunctionPointer(executeDelegate);
        }

        internal readonly FunctionPointer<T> Ptr;
    }
}