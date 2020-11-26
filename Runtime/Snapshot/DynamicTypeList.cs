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

        public static unsafe void PopulateList<T>(SystemBase system, DynamicBuffer<GhostCollectionComponentType> ghostComponentCollection, bool readOnly, ref T list)
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
                var compType = ghostComponentCollection[i].Type;
                if (readOnly)
                    compType.AccessModeType = ComponentType.AccessMode.ReadOnly;
                GhostChunkComponentTypesPtr[i] = system.GetDynamicComponentTypeHandle(compType);
            }
        }

        public static unsafe void PopulateListFromArray<T>(SystemBase system, NativeArray<ComponentType> componentTypes,  bool readOnly, ref T list)
            where T: struct, IDynamicTypeList
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (UnsafeUtility.SizeOf<ArchetypeChunkComponentTypeDynamic8>() != UnsafeUtility.SizeOf<DynamicComponentTypeHandle>()*8)
                throw new System.Exception("Invalid type size, this will cause undefined behavior");
#endif

            DynamicComponentTypeHandle* componentTypesPtr = list.GetData();
            list.Length = componentTypes.Length;
            for (int i = 0; i < list.Length; ++i)
            {
                var compType = componentTypes[i];
                if (readOnly)
                    compType.AccessModeType = ComponentType.AccessMode.ReadOnly;
                componentTypesPtr[i] = system.GetDynamicComponentTypeHandle(compType);
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
        public DynamicComponentTypeHandle dynamicType00;
        public DynamicComponentTypeHandle dynamicType01;
        public DynamicComponentTypeHandle dynamicType02;
        public DynamicComponentTypeHandle dynamicType03;
        public DynamicComponentTypeHandle dynamicType04;
        public DynamicComponentTypeHandle dynamicType05;
        public DynamicComponentTypeHandle dynamicType06;
        public DynamicComponentTypeHandle dynamicType07;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct ArchetypeChunkComponentTypeDynamic32
    {
        public ArchetypeChunkComponentTypeDynamic8 dynamicType00_07;
        public ArchetypeChunkComponentTypeDynamic8 dynamicType07_15;
        public ArchetypeChunkComponentTypeDynamic8 dynamicType16_23;
        public ArchetypeChunkComponentTypeDynamic8 dynamicType24_31;
    }

    public struct DynamicTypeList8 : IDynamicTypeList
    {
        public int Length { get; set; }

        public unsafe DynamicComponentTypeHandle* GetData()
        {
            fixed (DynamicComponentTypeHandle* ptr = &dynamicTypes.dynamicType00)
            {
                return ptr;
            }
        }

        private ArchetypeChunkComponentTypeDynamic8 dynamicTypes;
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
#pragma warning disable 0169
        private ArchetypeChunkComponentTypeDynamic32 dynamicTypes32_63;
#pragma warning restore 0169
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
#pragma warning disable 0169
        private ArchetypeChunkComponentTypeDynamic32 dynamicType031_063;
        private ArchetypeChunkComponentTypeDynamic32 dynamicType064_095;
        private ArchetypeChunkComponentTypeDynamic32 dynamicType096_127;
#pragma warning restore 0169
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