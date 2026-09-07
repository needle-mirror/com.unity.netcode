using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode.Tracing;

namespace Unity.Netcode.Editor.Tracing.UI.TickInspector
{
    enum FieldDisplayKind
    {
        Scalar,
        Inline,
        Opaque,
        Expandable,
        Unsupported,
    }

    sealed class FieldNode
    {
        /// <summary>Stable across rebuilds; keys the expansion state and the diff result.</summary>
        public string Path;
        public string Name;
        public Type Type;
        public FieldDisplayKind Kind;
        public FieldNode[] Children = Array.Empty<FieldNode>();
        public int LeafCount;

        /// <summary>
        /// Shown as one token because the tree stopped descending at <see cref="ComponentFieldTree.MaxDepth"/>,
        /// not because the type reads well on its own. The view marks it so the truncation is visible.
        /// </summary>
        public bool DepthTruncated;

        /// <summary>
        /// The differ compares this node's bytes with a single MemCmp, so the fuzzy factor never reaches
        /// the values underneath it — a float-free nested struct, an enum, or a non-floating fixed buffer.
        /// Leaves below such a node are compared exactly, or the table would find no changed leaf where
        /// the trace found a change and fall back to reddening the whole component.
        /// </summary>
        public bool ExactCompare;

        /// <summary>
        /// True when the children read naturally as an ordered tuple — math vector components and buffer
        /// elements. Everything else labels them, because "(True, 1.5, hello)" says nothing about which
        /// field is which.
        /// </summary>
        public bool PositionalChildren;

        /// <summary>
        /// A <c>FixedList*Bytes</c>: unlike every other node its entry count is a property of the value, not
        /// of the type, so the client and server can disagree about how many children there even are.
        /// </summary>
        public bool IsList;

        /// <summary>
        /// A fixed buffer shown as one token because its elements cannot be read back. It has to compare by
        /// its bytes: see <see cref="ComponentFieldTree.BufferBytesDiffer{T}"/> for why Equals will not do.
        /// </summary>
        public bool IsOpaqueFixedBuffer;

        internal FieldInfo Field;
        internal Type FixedBufferElementType;
        internal ElementAccess Access;
        internal int ElementIndex = -1;

        public bool IsLeaf => Kind is FieldDisplayKind.Scalar or FieldDisplayKind.Opaque or FieldDisplayKind.Unsupported;

        /// <summary>Reads this node's value out of its parent's value.</summary>
        public object ReadValue(object owner)
        {
            if (owner == null)
                return null;
            return Access switch
            {
                ElementAccess.FixedBuffer => ComponentFieldTree.ReadFixedBufferElement(owner, FixedBufferElementType, ElementIndex),
                ElementAccess.ListIndexer => ComponentFieldTree.ReadListElement(owner, ElementIndex),
                _ => Field != null ? Field.GetValue(owner) : owner,
            };
        }
    }

    enum ElementAccess
    {
        Field,
        FixedBuffer,
        ListIndexer,
    }

    sealed class FieldDiffResult
    {
        public readonly HashSet<string> ChangedPaths = new();
        public readonly Dictionary<string, int> ChangedLeafCounts = new();
        public readonly Dictionary<string, int> ListRowCounts = new();
        public readonly HashSet<string> ChangedLengthPaths = new();
        public readonly Dictionary<string, (object Client, object Server)> LeafValues = new();

        public int TotalChanged => ChangedPaths.Count;
        public bool HasLengthChanges => ChangedLengthPaths.Count > 0;
    }

    static class ComponentFieldTree
    {

        internal const int MaxInlineMathLeaves = 4;

        internal const int MaxDepth = 8;
        internal const string MathNamespace = "Unity.Mathematics";

        static readonly Dictionary<Type, FieldNode> s_Roots = new();

        static readonly HashSet<Type> s_PinnableElementTypes = new()
        {
            typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong),
            typeof(float), typeof(double),
        };

        public static FieldNode GetRoot(Type componentType)
        {
            if (componentType == null)
                return null;
            if (s_Roots.TryGetValue(componentType, out var cached))
                return cached;

            var root = new FieldNode
            {
                Path = string.Empty,
                Name = componentType.Name,
                Type = componentType,
                Kind = FieldDisplayKind.Inline,
            };
            root.Children = BuildChildren(componentType, string.Empty, depth: 0);
            root.LeafCount = SumLeaves(root.Children);
            s_Roots[componentType] = root;
            return root;
        }

        static FieldNode[] BuildChildren(Type ownerType, string ownerPath, int depth)
        {
            var fields = TypeDiffer.GetAllFields(ownerType);
            if (fields.Length == 0)
                return Array.Empty<FieldNode>();

            var children = new FieldNode[fields.Length];
            for (var i = 0; i < fields.Length; i++)
                children[i] = BuildFieldNode(fields[i], ownerPath, depth);
            MarkPointerAliasedFields(fields, children);
            return children;
        }

        static void MarkPointerAliasedFields(FieldInfo[] fields, FieldNode[] children)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                if (!fields[i].FieldType.IsPointer)
                    continue;

                var start = UnsafeUtility.GetFieldOffset(fields[i]);
                var end = start + IntPtr.Size;
                for (var j = 0; j < fields.Length; j++)
                {
                    if (j == i)
                        continue;
                    var offset = UnsafeUtility.GetFieldOffset(fields[j]);
                    if (offset < start || offset >= end)
                        continue;

                    children[j].Kind = FieldDisplayKind.Unsupported;
                    children[j].Children = Array.Empty<FieldNode>();
                    children[j].LeafCount = 1;
                }
            }
        }

        static FieldNode BuildFieldNode(FieldInfo field, string ownerPath, int depth)
        {
            var path = ownerPath.Length == 0 ? field.Name : $"{ownerPath}/{field.Name}";
            var node = new FieldNode { Path = path, Name = field.Name, Type = field.FieldType, Field = field };

            if (TryGetFixedBufferInfo(field, out var elementType, out var length))
            {
                node.ExactCompare = elementType != typeof(float) && elementType != typeof(double);
                if (s_PinnableElementTypes.Contains(elementType) && length > 0)
                {
                    node.Kind = FieldDisplayKind.Expandable;
                    node.Children = BuildElementNodes(path, elementType, length);
                    node.LeafCount = length;
                    node.PositionalChildren = true;
                    return node;
                }

                node.Kind = FieldDisplayKind.Opaque;
                node.IsOpaqueFixedBuffer = true;
                node.LeafCount = 1;
                return node;
            }

            Classify(node, depth);
            return node;
        }

        static bool TryClassifyFixedList(FieldNode node, int depth)
        {
            var type = node.Type;
            if (!type.IsGenericType || type.Namespace != "Unity.Collections"
                || !type.Name.StartsWith("FixedList", StringComparison.Ordinal))
                return false;

            var elementType = type.GetGenericArguments()[0];
            var capacity = GetListCapacity(type);
            if (capacity <= 0)
                return false;

            node.Kind = FieldDisplayKind.Expandable;
            node.IsList = true;
            node.PositionalChildren = true;
            node.LeafCount = capacity;
            node.Children = BuildListElementNodes(node.Path, elementType, capacity, depth + 1);
            return true;
        }

        static int GetListCapacity(Type listType)
        {
            var property = listType.GetProperty("Capacity");
            if (property == null)
                return 0;
            return (int)property.GetValue(Activator.CreateInstance(listType));
        }

        static FieldNode[] BuildListElementNodes(string ownerPath, Type elementType, int capacity, int depth)
        {
            var nodes = new FieldNode[capacity];
            for (var i = 0; i < capacity; i++)
            {
                var node = new FieldNode
                {
                    Path = $"{ownerPath}[{i}]",
                    Name = $"[{i}]",
                    Type = elementType,
                    Access = ElementAccess.ListIndexer,
                    ElementIndex = i,
                };
                Classify(node, depth);
                nodes[i] = node;
            }
            return nodes;
        }

        internal static int ReadListLength(object list)
        {
            if (list == null)
                return 0;
            var property = list.GetType().GetProperty("Length");
            return property != null ? (int)property.GetValue(list) : 0;
        }

        internal static object ReadListElement(object list, int index)
        {
            if (list == null)
                return null;
            var indexer = list.GetType().GetProperty("Item");
            return indexer?.GetValue(list, new object[] { index });
        }

        static FieldNode[] BuildElementNodes(string ownerPath, Type elementType, int length)
        {
            var elements = new FieldNode[length];
            for (var i = 0; i < length; i++)
            {
                elements[i] = new FieldNode
                {
                    Path = $"{ownerPath}[{i}]",
                    Name = $"[{i}]",
                    Type = elementType,
                    Kind = FieldDisplayKind.Scalar,
                    LeafCount = 1,
                    FixedBufferElementType = elementType,
                    Access = ElementAccess.FixedBuffer,
                    ElementIndex = i,
                };
            }
            return elements;
        }

        static void Classify(FieldNode node, int depth)
        {
            var type = node.Type;

            if (type == null || !type.IsValueType || type.IsPointer)
            {
                node.Kind = FieldDisplayKind.Unsupported;
                node.LeafCount = 1;
                return;
            }

            node.ExactCompare = !type.IsPrimitive
                && type.Namespace != MathNamespace
                && TypeDiffer.TryGetMemCmpSize(type, out _);

            // An entity reference reads as one token: its ToString is the readable form, and a client
            // relying on a server-allocated index is a real cause of misprediction, so it diffs normally.
            if (type == typeof(Entity))
            {
                node.Kind = FieldDisplayKind.Opaque;
                node.LeafCount = 1;
                return;
            }

            if (type.IsPrimitive || type.IsEnum)
            {
                node.Kind = FieldDisplayKind.Scalar;
                node.LeafCount = 1;
                return;
            }


            if (TryClassifyFixedList(node, depth))
                return;

            var isMathVector = type.Namespace == MathNamespace;
            if (!isMathVector)
            {
                var hasOwnToString = HasOwnToString(type);
                if (hasOwnToString || depth >= MaxDepth)
                {
                    node.Kind = FieldDisplayKind.Opaque;
                    // A type with its own ToString says what it is; one we stopped at does not.
                    node.DepthTruncated = !hasOwnToString;
                    node.LeafCount = 1;
                    return;
                }
            }

            node.PositionalChildren = isMathVector;
            node.Children = BuildChildren(type, node.Path, depth + 1);
            if (node.Children.Length == 0)
            {
                node.Kind = FieldDisplayKind.Opaque;
                node.LeafCount = 1;
                return;
            }

            if (AllUnsupported(node.Children))
            {
                node.Kind = FieldDisplayKind.Unsupported;
                node.Children = Array.Empty<FieldNode>();
                node.LeafCount = 1;
                return;
            }

            node.LeafCount = SumLeaves(node.Children);
            node.Kind = CanInline(node) ? FieldDisplayKind.Inline : FieldDisplayKind.Expandable;
        }

        static bool CanInline(FieldNode node)
        {
            if (node.Type?.Namespace == MathNamespace)
                return node.LeafCount <= MaxInlineMathLeaves && AllLeavesAreNumbers(node.Children);
            return false;
        }

        static bool AllLeavesAreNumbers(FieldNode[] children)
        {
            foreach (var child in children)
            {
                var ok = child.IsLeaf
                    ? child.Kind == FieldDisplayKind.Scalar && IsNumberType(child.Type)
                    : AllLeavesAreNumbers(child.Children);
                if (!ok)
                    return false;
            }
            return children.Length > 0;
        }

        // Every numeric primitive and nothing else: bool, char and enums are spelled out, not tupled.
        static bool IsNumberType(Type type) => type != null && s_PinnableElementTypes.Contains(type);

        static bool AllUnsupported(FieldNode[] children)
        {
            foreach (var child in children)
            {
                if (child.Kind != FieldDisplayKind.Unsupported)
                    return false;
            }
            return true;
        }

        static int SumLeaves(FieldNode[] children)
        {
            var total = 0;
            foreach (var child in children)
                total += Math.Max(1, child.LeafCount);
            return total;
        }

        static bool TryGetFixedBufferInfo(FieldInfo field, out Type elementType, out int length)
        {
            elementType = null;
            length = 0;
            var attribute = field.GetCustomAttribute<System.Runtime.CompilerServices.FixedBufferAttribute>();
            if (attribute == null)
                return false;
            elementType = attribute.ElementType;
            length = attribute.Length;
            return true;
        }

        static bool HasOwnToString(Type type)
        {
            var method = type.GetMethod("ToString", Type.EmptyTypes);
            return method != null && method.DeclaringType != typeof(object) && method.DeclaringType != typeof(ValueType);
        }

        internal static object ReadFixedBufferElement(object bufferWrapper, Type elementType, int index)
        {
            if (bufferWrapper == null || elementType == null)
                return null;

            var handle = GCHandle.Alloc(bufferWrapper, GCHandleType.Pinned);
            try
            {
                var address = handle.AddrOfPinnedObject() + index * UnsafeUtility.SizeOf(elementType);
                return Marshal.PtrToStructure(address, elementType);
            }
            finally
            {
                handle.Free();
            }
        }

        const float k_ExactEpsilon = 0f;

        /// <summary>
        /// Compares two instances of the component leaf by leaf. Both sides must be non-null; the
        /// present/missing cases are handled by the caller.
        /// </summary>
        public static FieldDiffResult Compare(FieldNode root, object clientValue, object serverValue, float epsilon)
        {
            var result = new FieldDiffResult();
            if (root == null || clientValue == null || serverValue == null)
                return result;

            foreach (var child in root.Children)
                CompareNode(child, clientValue, serverValue, epsilon, result);
            return result;
        }

        public static FieldDiffResult MarkAllLeavesChanged(FieldNode root)
        {
            var result = new FieldDiffResult();
            if (root == null)
                return result;

            foreach (var child in root.Children)
                MarkLeavesChanged(child, result);
            return result;
        }

        static int CompareNode(FieldNode node, object clientOwner, object serverOwner, float epsilon, FieldDiffResult result)
        {
            var clientValue = node.ReadValue(clientOwner);
            var serverValue = node.ReadValue(serverOwner);

            if (node.ExactCompare)
                epsilon = k_ExactEpsilon;

            if (node.IsLeaf)
            {
                if (node.Kind == FieldDisplayKind.Unsupported || !LeafDiffers(node, clientValue, serverValue, epsilon))
                    return 0;

                result.ChangedPaths.Add(node.Path);
                result.LeafValues[node.Path] = (clientValue, serverValue);
                return 1;
            }

            if (node.IsList)
                return CompareList(node, clientValue, serverValue, epsilon, result);

            var changed = 0;
            foreach (var child in node.Children)
                changed += CompareNode(child, clientValue, serverValue, epsilon, result);

            result.ChangedLeafCounts[node.Path] = changed;
            return changed;
        }

        static int CompareList(FieldNode node, object clientList, object serverList, float epsilon, FieldDiffResult result)
        {
            var clientLength = ReadListLength(clientList);
            var serverLength = ReadListLength(serverList);
            var shared = Math.Min(clientLength, serverLength);
            var rows = Math.Max(clientLength, serverLength);
            result.ListRowCounts[node.Path] = rows;

            var changed = 0;
            for (var i = 0; i < shared && i < node.Children.Length; i++)
                changed += CompareNode(node.Children[i], clientList, serverList, epsilon, result);

            // Entries past the shorter side exist on one side only, so they count as changed wholesale.
            if (clientLength != serverLength)
            {
                result.ChangedLengthPaths.Add(node.Path);
                for (var i = shared; i < rows && i < node.Children.Length; i++)
                    changed += MarkLeavesChanged(node.Children[i], result);
            }

            result.ChangedLeafCounts[node.Path] = changed;
            return changed;
        }

        static int MarkLeavesChanged(FieldNode node, FieldDiffResult result)
        {
            if (node.IsLeaf)
            {
                // Unsupported leaves have no comparable value, so they are left alone here and read the
                // same as they do in an ordinary comparison.
                if (node.Kind == FieldDisplayKind.Unsupported)
                    return 0;

                result.ChangedPaths.Add(node.Path);
                return 1;
            }

            var changed = 0;
            foreach (var child in node.Children)
                changed += MarkLeavesChanged(child, result);

            result.ChangedLeafCounts[node.Path] = changed;
            return changed;
        }

        static readonly HashSet<Type> s_EpsilonIntegralTypes = new()
        {
            typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
        };

        static bool LeafDiffers(FieldNode node, object client, object server, float epsilon)
        {
            if (node.IsOpaqueFixedBuffer && client != null && server != null)
                return GetBufferComparer(node.Type)(client, server);

            return ValuesDiffer(client, server, node.Type, epsilon);
        }

        static readonly Dictionary<Type, Func<object, object, bool>> s_BufferComparers = new();

        static Func<object, object, bool> GetBufferComparer(Type wrapperType)
        {
            if (s_BufferComparers.TryGetValue(wrapperType, out var cached))
                return cached;

            var comparer = (Func<object, object, bool>)Delegate.CreateDelegate(
                typeof(Func<object, object, bool>),
                typeof(ComponentFieldTree)
                    .GetMethod(nameof(BufferBytesDiffer), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(wrapperType));
            s_BufferComparers[wrapperType] = comparer;
            return comparer;
        }

        static unsafe bool BufferBytesDiffer<T>(object client, object server) where T : struct
        {
            var clientBuffer = (T)client;
            var serverBuffer = (T)server;
            return UnsafeUtility.MemCmp(UnsafeUtility.AddressOf(ref clientBuffer), UnsafeUtility.AddressOf(ref serverBuffer),
                UnsafeUtility.SizeOf<T>()) != 0;
        }

        // Exact |b - a| for wide integers, mirroring TypeDiffer.Distance: subtract in the value's own
        // width and convert to float last, so neither the wraparound nor a widening rounding step
        // can shrink the magnitude.
        static float Distance(long a, long b) => b >= a ? (ulong)(b - a) : (ulong)(a - b);
        static float Distance(ulong a, ulong b) => b >= a ? b - a : a - b;

        /// <summary>
        /// Mirrors <see cref="TypeDiffer"/>'s per-primitive comparisons — epsilon, and the true magnitude
        /// measured in the subtraction's own width — so the table and the trace agree on what counts as
        /// a change. The table explains the trace rather than second-guessing it. Types the differ compares
        /// exactly (char, bool, enums and anything opaque) fall through to Equals rather than going through
        /// a conversion that can throw.
        /// </summary>
        static bool ValuesDiffer(object client, object server, Type type, float epsilon)
        {
            if (client == null || server == null)
                return !ReferenceEquals(client, server);

            if (type == typeof(float))
                return math.abs((float)server - (float)client) > epsilon;
            if (type == typeof(double))
                return math.abs((double)server - (double)client) > epsilon;

            if (type == typeof(int))
                return Distance((int)client, (int)server) > epsilon;
            if (type == typeof(uint))
                return Distance((uint)client, (uint)server) > epsilon;
            if (type == typeof(long))
                return Distance((long)client, (long)server) > epsilon;

            if (type == typeof(ulong))
                return Distance((ulong)client, (ulong)server) > epsilon;
            if (type == typeof(IntPtr))
                return Distance(((IntPtr)client).ToInt64(), ((IntPtr)server).ToInt64()) > epsilon;
            if (type == typeof(UIntPtr))
                return Distance(((UIntPtr)client).ToUInt64(), ((UIntPtr)server).ToUInt64()) > epsilon;

            if (s_EpsilonIntegralTypes.Contains(type))
                return math.abs(Convert.ToDouble(server) - Convert.ToDouble(client)) > epsilon;

            return !client.Equals(server);
        }
    }
}
