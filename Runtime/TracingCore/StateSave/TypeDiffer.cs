using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.NetCode.Tracing
{

    [BurstCompile]
    internal struct TypeDiffer : IDisposable
    {
        NativeArray<TypeDiffer> m_PerFieldDiffers;
        PortableFunctionPointer<DiffDelegate> m_DiffMethod;
        int m_FieldOffset;
        int m_MemCmpSize;
        // Math vectors expand into one diff unit per scalar; every other struct is a single unit.
        bool m_ExpandChildren;

        private TypeDiffer(PortableFunctionPointer<DiffDelegate> diffMethod) : this()
        {
            m_DiffMethod = diffMethod;
        }

        public TypeDiffer(ComponentType typeToDiff, Allocator allocator)
        {
            this = ConstructDifferRecursive(typeToDiff.GetManagedType(), allocator);
        }

        public void Dispose()
        {
            if (m_PerFieldDiffers.IsCreated)
            {
                foreach (var typeDiffer in m_PerFieldDiffers)
                {
                    typeDiffer.Dispose();
                }
            }

            m_PerFieldDiffers.Dispose();
        }

        // Cache the reflection result: GetAllFields is called per field per capture (recursively for math
        // vectors), and Type.GetFields allocates + reflects on every call.
        static readonly Dictionary<Type, FieldInfo[]> s_FieldsCache = new();

        public static FieldInfo[] GetAllFields(Type managedType)
        {
            if (!s_FieldsCache.TryGetValue(managedType, out var fields))
            {
                fields = managedType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                s_FieldsCache[managedType] = fields;
            }
            return fields;
        }

        public static TypeDiffer ConstructDifferRecursive(Type managedType, Allocator allocator)
            => ConstructDifferRecursive(managedType, allocator, isRoot: true);

        static TypeDiffer ConstructDifferRecursive(Type managedType, Allocator allocator, bool isRoot)
        {
            if (managedType.IsPrimitive)
                return CreatePrimitiveDiffer(managedType);

            // Managed (and pointer) types are never diffed
            if (!managedType.IsValueType)
                return default;

            // ProcessDiffPerField needs the root to keep one differ per field and math vectors one per
            // scalar (m_ExpandChildren); everything below that is a single diff unit, so a float-free
            // struct can collapse into one MemCmp.
            var expandChildren = managedType.Namespace == "Unity.Mathematics";
            if (!isRoot && !expandChildren && TryGetMemCmpSize(managedType, out var memCmpSize))
                return new TypeDiffer { m_MemCmpSize = memCmpSize };

            var allFields = GetAllFields(managedType);
            TypeDiffer toReturn = default;
            if (allFields.Length == 0)
                return toReturn;

            toReturn.m_ExpandChildren = expandChildren;
            toReturn.m_PerFieldDiffers = new NativeArray<TypeDiffer>(allFields.Length, allocator);
            for (int i = 0; i < allFields.Length; i++)
            {
                var fieldInfo = allFields[i];
                TypeDiffer differ;
                if (TryCreateFixedBufferDiffer(fieldInfo, allocator, out var fixedBufferDiffer))
                    differ = fixedBufferDiffer;
                else if (fieldInfo.FieldType.IsPrimitive)
                    differ = CreatePrimitiveDiffer(fieldInfo.FieldType);
                else
                    differ = ConstructDifferRecursive(fieldInfo.FieldType, allocator, isRoot: false);

                // Query the runtime for the real offset: summing field sizes ignores alignment
                // padding (e.g. the int in struct{byte,int} lives at offset 4, not 1).
                differ.m_FieldOffset = UnsafeUtility.GetFieldOffset(fieldInfo);
                toReturn.m_PerFieldDiffers[i] = differ;
            }

            return toReturn;
        }

        /// <summary>
        /// True when <paramref name="type"/> can be compared with a single MemCmp: it contains no floating point fields
        /// </summary>
        static bool TryGetMemCmpSize(Type type, out int size)
        {
            size = 0;
            if (!type.IsValueType || type == typeof(float) || type == typeof(double))
                return false;

            if (type.IsPrimitive || type.IsEnum)
            {
                size = UnsafeUtility.SizeOf(type);
                return true;
            }

            var fields = GetAllFields(type);
            if (fields.Length == 0)
                return false;

            var ranges = new List<(int offset, int size)>(fields.Length);
            foreach (var field in fields)
            {
                int fieldSize;
                if (TryGetFixedBufferInfo(field, out var elementType, out var bufferSize))
                {
                    if (elementType == typeof(float) || elementType == typeof(double))
                        return false;
                    fieldSize = bufferSize;
                }
                else if (!TryGetMemCmpSize(field.FieldType, out fieldSize))
                {
                    return false;
                }

                ranges.Add((UnsafeUtility.GetFieldOffset(field), fieldSize));
            }

            ranges.Sort(static (a, b) => a.offset.CompareTo(b.offset));
            var covered = 0;
            foreach (var range in ranges)
            {
                if (range.offset != covered)
                    return false;
                covered += range.size;
            }

            if (covered != UnsafeUtility.SizeOf(type))
                return false;

            size = covered;
            return true;
        }

        /// <summary>
        /// Fixed buffers (<c>fixed byte b[N]</c>) hide behind a compiler-generated struct exposing
        /// a single element field, so the generic recursion would only diff the first element.
        /// Non-float buffers become one MemCmp range; float buffers get one differ per element to
        /// keep epsilon semantics.
        /// </summary>
        static bool TryCreateFixedBufferDiffer(FieldInfo fieldInfo, Allocator allocator, out TypeDiffer differ)
        {
            differ = default;
            if (!TryGetFixedBufferInfo(fieldInfo, out var elementType, out var bufferSize))
                return false;

            if (elementType != typeof(float) && elementType != typeof(double))
            {
                differ.m_MemCmpSize = bufferSize;
                return true;
            }

            var elementSize = UnsafeUtility.SizeOf(elementType);
            var length = bufferSize / elementSize;
            differ.m_PerFieldDiffers = new NativeArray<TypeDiffer>(length, allocator);
            for (int i = 0; i < length; i++)
            {
                var elementDiffer = CreatePrimitiveDiffer(elementType);
                elementDiffer.m_FieldOffset = i * elementSize;
                differ.m_PerFieldDiffers[i] = elementDiffer;
            }

            return true;
        }

        static bool TryGetFixedBufferInfo(FieldInfo fieldInfo, out Type elementType, out int bufferSize)
        {
            elementType = null;
            bufferSize = 0;
            var fixedBuffer = fieldInfo.GetCustomAttribute<System.Runtime.CompilerServices.FixedBufferAttribute>();
            if (fixedBuffer == null)
                return false;
            elementType = fixedBuffer.ElementType;
            bufferSize = UnsafeUtility.SizeOf(elementType) * fixedBuffer.Length;
            return true;
        }

        [BurstCompile]
        public readonly unsafe float ProcessDiffAmount(byte* left, byte* right)
        {
            if (m_MemCmpSize > 0)
            {
                return UnsafeUtility.MemCmp(left, right, m_MemCmpSize) != 0 ? float.PositiveInfinity : 0f;
            }

            if (m_DiffMethod.Ptr.IsCreated)
                return m_DiffMethod.Ptr.Invoke(left, right);

            var maxAmount = 0f;
            for (int i = 0; i < this.m_PerFieldDiffers.Length; i++)
            {
                var differ = m_PerFieldDiffers[i];
                maxAmount = math.max(maxAmount, differ.ProcessDiffAmount(left + differ.m_FieldOffset, right + differ.m_FieldOffset));
            }

            return maxAmount;
        }

        public static bool IsDiffAmount(float amount) => amount > 0f;

        // Diff bitmask: bit i = unit i (a math-vector scalar or a whole non-math field), depth-first.
        // >64 units fold into the last bit.
        public const int MaxFieldBits = 64;

        public readonly unsafe ulong ProcessDiffPerField(byte* left, byte* right, float* diffAmountsByUnit)
        {
            if (m_DiffMethod.Ptr.IsCreated || m_MemCmpSize > 0)
            {
                var amount = ProcessDiffAmount(left, right);
                if (!IsDiffAmount(amount))
                    return 0UL;
                diffAmountsByUnit[0] = math.max(diffAmountsByUnit[0], amount);
                return 1UL;
            }

            var unitIndex = 0;
            ulong mask = 0;
            for (var i = 0; i < m_PerFieldDiffers.Length; i++)
            {
                var differ = m_PerFieldDiffers[i];
                mask |= differ.ProcessUnitDiffs(left + differ.m_FieldOffset, right + differ.m_FieldOffset, diffAmountsByUnit, ref unitIndex);
            }

            return mask;
        }

        readonly unsafe ulong ProcessUnitDiffs(byte* left, byte* right, float* diffAmountsByUnit, ref int unitIndex)
        {
            // Math container: recurse so each scalar is its own unit.
            if (m_ExpandChildren)
            {
                ulong mask = 0;
                for (var i = 0; i < m_PerFieldDiffers.Length; i++)
                {
                    var differ = m_PerFieldDiffers[i];
                    mask |= differ.ProcessUnitDiffs(left + differ.m_FieldOffset, right + differ.m_FieldOffset, diffAmountsByUnit, ref unitIndex);
                }
                return mask;
            }

            // Primitive or non-math struct: a single unit compared as a whole.
            var bit = unitIndex < MaxFieldBits ? unitIndex : MaxFieldBits - 1;
            unitIndex++;
            var unitAmount = ProcessDiffAmount(left, right);
            if (!IsDiffAmount(unitAmount))
                return 0UL;
            diffAmountsByUnit[bit] = math.max(diffAmountsByUnit[bit], unitAmount);
            return 1UL << bit;
        }


        /// <summary>
        /// Managed entry point used by the UI, diffs two component values with the same differ the processing pass uses
        /// Returns the unit diff mask (0 when either value is missing).
        /// </summary>
        public static unsafe ulong ComputeUnitDiffs(Type componentType, object left, object right, float[] diffAmountsByUnit)
        {
            Array.Clear(diffAmountsByUnit, 0, diffAmountsByUnit.Length);
            if (componentType == null || left == null || right == null || !componentType.IsValueType)
                return 0UL;
            var size = UnsafeUtility.SizeOf(componentType);
            if (size == 0)
                return 0UL;

            var type = new ComponentType(componentType);
            AllTypeDiffers.Instance.TryAddTypeDiffer(type);
            var differ = AllTypeDiffers.Instance.AllDiffers[type];

            var copier = GetBoxedCopier(componentType);
            var leftBytes = new byte[size];
            var rightBytes = new byte[size];
            copier(left, leftBytes);
            copier(right, rightBytes);

            fixed (byte* leftPtr = leftBytes)
            fixed (byte* rightPtr = rightBytes)
            fixed (float* amounts = diffAmountsByUnit)
                return differ.ProcessDiffPerField(leftPtr, rightPtr, amounts);
        }

        static readonly Dictionary<Type, Action<object, byte[]>> s_BoxedCopiers = new();

        static Action<object, byte[]> GetBoxedCopier(Type type)
        {
            if (!s_BoxedCopiers.TryGetValue(type, out var copier))
            {
                copier = (Action<object, byte[]>)typeof(TypeDiffer)
                    .GetMethod(nameof(CopyBoxedTo), BindingFlags.Static | BindingFlags.NonPublic)
                    .MakeGenericMethod(type)
                    .CreateDelegate(typeof(Action<object, byte[]>));
                s_BoxedCopiers[type] = copier;
            }
            return copier;
        }

        // Unboxing copies the exact bits; a Marshal-style conversion (e.g. 4-byte bools) never runs.
        static unsafe void CopyBoxedTo<T>(object boxed, byte[] destination) where T : unmanaged
        {
            fixed (byte* ptr = destination)
                *(T*)ptr = (T)boxed;
        }

        // Compiling a Burst function pointer is expensive. Compile all primitives only once.
        static Dictionary<Type, PortableFunctionPointer<DiffDelegate>> s_PrimitiveDiffMethods;

        static unsafe TypeDiffer CreatePrimitiveDiffer(Type fieldType)
        {
            // primitive types taken from https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/built-in-types
            s_PrimitiveDiffMethods ??= new Dictionary<Type, PortableFunctionPointer<DiffDelegate>>
            {
                { typeof(bool), new PortableFunctionPointer<DiffDelegate>(DiffBool) },
                { typeof(byte), new PortableFunctionPointer<DiffDelegate>(DiffByte) },
                { typeof(sbyte), new PortableFunctionPointer<DiffDelegate>(DiffSbyte) },
                { typeof(double), new PortableFunctionPointer<DiffDelegate>(DiffDouble) },
                { typeof(float), new PortableFunctionPointer<DiffDelegate>(DiffFloat) },
                { typeof(int), new PortableFunctionPointer<DiffDelegate>(DiffInt) },
                { typeof(uint), new PortableFunctionPointer<DiffDelegate>(DiffUint) },
                { typeof(nint), new PortableFunctionPointer<DiffDelegate>(DiffNint) },
                { typeof(nuint), new PortableFunctionPointer<DiffDelegate>(DiffNuint) },
                { typeof(long), new PortableFunctionPointer<DiffDelegate>(DiffLong) },
                { typeof(ulong), new PortableFunctionPointer<DiffDelegate>(DiffUlong) },
                { typeof(short), new PortableFunctionPointer<DiffDelegate>(DiffShort) },
                { typeof(ushort), new PortableFunctionPointer<DiffDelegate>(DiffUshort) },
            };
            if (s_PrimitiveDiffMethods.TryGetValue(fieldType, out var diffMethod))
                return new TypeDiffer(diffMethod);
            Debug.LogError($"field type {fieldType} not implemented");
            return default;
        }

        // Exact |b - a| for wide integers: subtract in the value's own width and convert to float last.
        static float Distance(long a, long b) => b >= a ? (ulong)(b - a) : (ulong)(a - b);
        static float Distance(ulong a, ulong b) => b >= a ? b - a : a - b;

         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffBool(byte* a, byte* b) => *(bool*)a != *(bool*)b ? float.PositiveInfinity : 0f;
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffByte(byte* a, byte* b) => math.abs(*(byte*)b - *(byte*)a);
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffSbyte(byte* a, byte* b) => math.abs(*(sbyte*)b - *(sbyte*)a);
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffDouble(byte* a, byte* b) => (float)math.abs(*(double*)b - *(double*)a);
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffFloat(byte* a, byte* b) => math.abs(*(float*)b - *(float*)a);
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffInt(byte* a, byte* b) => Distance(*(int*)a, *(int*)b);
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffUint(byte* a, byte* b) => Distance(*(uint*)a, *(uint*)b);
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffNint(byte* a, byte* b) => Distance(*(nint*)a, *(nint*)b);
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffNuint(byte* a, byte* b) => Distance(*(nuint*)a, *(nuint*)b);
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffLong(byte* a, byte* b) => Distance(*(long*)a, *(long*)b);
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffUlong(byte* a, byte* b) => Distance(*(ulong*)a, *(ulong*)b);

         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffShort(byte* a, byte* b) => math.abs(*(short*)b - *(short*)a);
         [BurstCompile][AOT.MonoPInvokeCallback(typeof(DiffDelegate))] public static unsafe float DiffUshort(byte* a, byte* b) => math.abs(*(ushort*)b - *(ushort*)a);
         public unsafe delegate float DiffDelegate(byte* a, byte* b);

    }

    internal struct AllTypeDiffers : IDisposable
    {
        public struct StaticKey { }

        static readonly SharedStatic<AllTypeDiffers> s_Instance = SharedStatic<AllTypeDiffers>.GetOrCreate<AllTypeDiffers, StaticKey>();

        public NativeHashMap<ComponentType, TypeDiffer> AllDiffers;
        bool m_Initialized;
        public static ref AllTypeDiffers Instance {
            get
            {
                if (!s_Instance.Data.m_Initialized)
                {
                    s_Instance.Data.Initialize();
                }
                return ref s_Instance.Data;
            }
        }

        void Initialize()
        {
            AllDiffers = new(10, Allocator.Persistent);
            m_Initialized = true;
        }

        public void Dispose()
        {
            if(!m_Initialized)
                return;
            foreach (var kvPair in AllDiffers)
            {
                kvPair.Value.Dispose();
            }
            AllDiffers.Dispose();
            m_Initialized = false;
        }

        public void TryAddTypeDiffer(ComponentType type)
        {
            if (!AllDiffers.ContainsKey(type))
                AllDiffers.Add(type, new TypeDiffer(type, Allocator.Persistent));
        }
    }
}
