using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Collections;
using System.Runtime.InteropServices;

namespace Unity.NetCode
{
    public struct DynamicTypeList
    {
        public const int MaxCapacity = 128;

        public static unsafe void PopulateList<T>(SystemBase system, NativeArray<GhostComponentSerializer.State> ghostComponentCollection, bool readOnly, ref T list)
            where T: struct, IDynamicTypeList
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<ArchetypeChunkComponentTypeDynamic32>() != UnsafeUtility.SizeOf<DynamicComponentTypeHandle>()*32)
                throw new System.Exception("Invalid type size, this will cause undefined behavior");
#endif
            var listLength = ghostComponentCollection.Length;
            DynamicComponentTypeHandle* GhostChunkComponentTypesPtr = list.GetData();
            list.Length = listLength;
            for (int i = 0; i < list.Length; ++i)
            {
                var compType = ghostComponentCollection[i].ComponentType;
                if (readOnly)
                    compType.AccessModeType = ComponentType.AccessMode.ReadOnly;
                GhostChunkComponentTypesPtr[i] = system.GetDynamicComponentTypeHandle(compType);
            }
        }
    }
    public unsafe interface IDynamicTypeList
    {
        int Length { set; get; }
        DynamicComponentTypeHandle* GetData();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ArchetypeChunkComponentTypeDynamic8
    {
        [NativeDisableContainerSafetyRestriction] public DynamicComponentTypeHandle dynamicType00;
        [NativeDisableContainerSafetyRestriction] public DynamicComponentTypeHandle dynamicType01;
        [NativeDisableContainerSafetyRestriction] public DynamicComponentTypeHandle dynamicType02;
        [NativeDisableContainerSafetyRestriction] public DynamicComponentTypeHandle dynamicType03;
        [NativeDisableContainerSafetyRestriction] public DynamicComponentTypeHandle dynamicType04;
        [NativeDisableContainerSafetyRestriction] public DynamicComponentTypeHandle dynamicType05;
        [NativeDisableContainerSafetyRestriction] public DynamicComponentTypeHandle dynamicType06;
        [NativeDisableContainerSafetyRestriction] public DynamicComponentTypeHandle dynamicType07;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct ArchetypeChunkComponentTypeDynamic32
    {
        public ArchetypeChunkComponentTypeDynamic8 dynamicType00_07;
        public ArchetypeChunkComponentTypeDynamic8 dynamicType07_15;
        public ArchetypeChunkComponentTypeDynamic8 dynamicType16_23;
        public ArchetypeChunkComponentTypeDynamic8 dynamicType24_31;
    }

    public struct DynamicTypeList32 : IDynamicTypeList
    {
        public int Length { get; set; }

        public unsafe DynamicComponentTypeHandle* GetData()
        {
            fixed (DynamicComponentTypeHandle* ptr = &dynamicTypes.dynamicType00_07.dynamicType00)
            {
                return ptr;
            }
        }

        private ArchetypeChunkComponentTypeDynamic32 dynamicTypes;
    }
    public struct DynamicTypeList64 : IDynamicTypeList
    {
        public int Length { get; set; }

        public unsafe DynamicComponentTypeHandle* GetData()
        {
            fixed (DynamicComponentTypeHandle* ptr = &dynamicTypes00_31.dynamicType00_07.dynamicType00)
            {
                return ptr;
            }
        }

        private ArchetypeChunkComponentTypeDynamic32 dynamicTypes00_31;
        private ArchetypeChunkComponentTypeDynamic32 dynamicTypes32_63;
    }

    public struct DynamicTypeList128 : IDynamicTypeList
    {
        public int Length { get; set; }

        public unsafe DynamicComponentTypeHandle* GetData()
        {
            fixed (DynamicComponentTypeHandle* ptr = &dynamicType000_031.dynamicType00_07.dynamicType00)
            {
                return ptr;
            }
        }

        private ArchetypeChunkComponentTypeDynamic32 dynamicType000_031;
        private ArchetypeChunkComponentTypeDynamic32 dynamicType031_063;
        private ArchetypeChunkComponentTypeDynamic32 dynamicType064_095;
        private ArchetypeChunkComponentTypeDynamic32 dynamicType096_127;
    }
    /*public struct DynamicTypeList160 : IDynamicTypeList
    {
        public int Length { get; set; }

        public unsafe ArchetypeChunkComponentTypeDynamic* GetData()
        {
            fixed (ArchetypeChunkComponentTypeDynamic* ptr = &dynamicTypes000_127.dynamicType00_07.dynamicType00)
            {
                return ptr;
            }
        }

        private ArchetypeChunkComponentTypeDynamic32 dynamicTypes000_031;
        private ArchetypeChunkComponentTypeDynamic32 dynamicTypes031_063;
        private ArchetypeChunkComponentTypeDynamic32 dynamicTypes064_095;
        private ArchetypeChunkComponentTypeDynamic32 dynamicTypes096_127;
        private ArchetypeChunkComponentTypeDynamic32 dynamicTypes128_159;
    }*/
}