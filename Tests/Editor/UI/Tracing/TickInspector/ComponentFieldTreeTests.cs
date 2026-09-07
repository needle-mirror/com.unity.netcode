using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode.Editor.Tracing.UI.TickInspector;
using Unity.Netcode.Tracing;

namespace Unity.Netcode.Editor.Tracing.UI.Tests
{
    /// <summary>
    /// Tests for <see cref="ComponentFieldTree"/> — the shared model behind the misprediction table.
    /// It decides how deeply a component's fields are shown and, from the same tree, which leaves differ.
    /// </summary>
    class ComponentFieldTreeTests
    {
        struct Inner
        {
            public int A;
            public int B;
        }

        struct Nested : IComponentData
        {
            public Inner Data;
        }

        struct DeeplyNested : IComponentData
        {
            public Level1 L;
        }

        struct Level1
        {
            public Level2 L;
            public int Tail;
        }

        struct Level2
        {
            public float X;
            public float Y;
        }

        // One field per level, so the nth nesting level is reached by taking Children[0] n times.
        struct D9 { public int Value; }
        struct D8 { public D9 Next; }
        struct D7 { public D8 Next; }
        struct D6 { public D7 Next; }
        struct D5 { public D6 Next; }
        struct D4 { public D5 Next; }
        struct D3 { public D4 Next; }
        struct D2 { public D3 Next; }
        struct D1 { public D2 Next; }

        struct TooDeep : IComponentData
        {
            public D1 Next;
        }

        struct ThreeNumbers
        {
            public float X;
            public int Y;
            public byte Z;
        }

        struct FourNumbers
        {
            public float X;
            public float Y;
            public float Z;
            public float W;
        }

        struct NumberWidths : IComponentData
        {
            public ThreeNumbers Three;
            public FourNumbers Four;
        }

        struct VectorComponent : IComponentData
        {
            public float3 V;
        }

        struct MatrixComponent : IComponentData
        {
            public float4x4 M;
        }

        struct TransformLike : IComponentData
        {
            public float3 Position;
            public quaternion Rotation;
            public float Scale;
        }

        unsafe struct FixedFloatBuffer : IComponentData
        {
            public fixed float Values[4];
        }

        unsafe struct LongFixedBuffer : IComponentData
        {
            public fixed int Values[20];
        }

        struct EntityRef : IComponentData
        {
            public Entity Target;
            public int Value;
        }

        struct BlobHolder : IComponentData
        {
            public BlobAssetReference<int> Blob;
            public int Value;
        }

        struct StringHolder : IComponentData
        {
            public FixedString64Bytes Text;
        }

        struct BlobAndValue
        {
            public BlobAssetReference<int> Blob;
            public int Value;
        }

        struct NestedBlobHolder : IComponentData
        {
            public BlobAndValue Data;
        }

        struct UnsignedHolder : IComponentData
        {
            public uint Value;
        }

        unsafe struct BoolFixedBuffer : IComponentData
        {
            public fixed bool Values[4];
            public int Tail;
        }

        const float k_Epsilon = 1e-4f;

        static FieldNode Child(System.Type componentType, string name)
        {
            foreach (var child in ComponentFieldTree.GetRoot(componentType).Children)
            {
                if (child.Name == name)
                    return child;
            }
            Assert.Fail($"{componentType.Name} has no field '{name}'");
            return null;
        }

        static FieldDiffResult Compare(System.Type componentType, object client, object server) =>
            ComponentFieldTree.Compare(ComponentFieldTree.GetRoot(componentType), client, server, k_Epsilon);

        [Test]
        public void NestedStruct_ExposesEveryLeaf()
        {
            var data = Child(typeof(Nested), nameof(Nested.Data));
            Assert.That(data.Kind, Is.EqualTo(FieldDisplayKind.Expandable), "a struct is a foldout, however small");
            Assert.That(data.LeafCount, Is.EqualTo(2), "both ints of the nested struct are addressable");
        }

        [Test]
        public void NestedStruct_FlagsOnlyTheChangedLeaf()
        {
            // The whole point of the field tree: a nested struct is no longer one opaque diff unit.
            var diff = Compare(typeof(Nested),
                new Nested { Data = new Inner { A = 3, B = 4 } },
                new Nested { Data = new Inner { A = 3, B = 9 } });

            Assert.That(diff.ChangedPaths, Is.EquivalentTo(new[] { "Data/B" }));
        }

        [Test]
        public void NestingPastTheDepthLimit_StopsAndSaysSo()
        {
            var node = Child(typeof(TooDeep), nameof(TooDeep.Next));
            for (var depth = 0; depth < ComponentFieldTree.MaxDepth; depth++)
            {
                Assert.That(node.Kind, Is.EqualTo(FieldDisplayKind.Expandable), $"level {depth} is still expanded");
                Assert.That(node.DepthTruncated, Is.False, $"level {depth} is within the limit");
                node = node.Children[0];
            }

            Assert.That(node.Kind, Is.EqualTo(FieldDisplayKind.Opaque), "the guardrail stops descending here");
            Assert.That(node.DepthTruncated, Is.True, "and the view has to show that it was truncated");
        }

        [Test]
        public void PlainStruct_IsAlwaysFoldedOut_HoweverFewNumbersItHolds()
        {
            Assert.That(Child(typeof(NumberWidths), nameof(NumberWidths.Three)).Kind, Is.EqualTo(FieldDisplayKind.Expandable));
            Assert.That(Child(typeof(NumberWidths), nameof(NumberWidths.Four)).Kind, Is.EqualTo(FieldDisplayKind.Expandable));
        }

        [Test]
        public void FixedBuffer_IsAlwaysFoldedOut_HoweverShort()
        {
            var shortBuffer = Child(typeof(FixedFloatBuffer), nameof(FixedFloatBuffer.Values));
            Assert.That(shortBuffer.Kind, Is.EqualTo(FieldDisplayKind.Expandable));
        }

        [Test]
        public void NestedStruct_HoldingAnotherStruct_IsAFoldoutNotOneLine()
        {
            var level1 = Child(typeof(DeeplyNested), nameof(DeeplyNested.L));
            Assert.That(level1.Kind, Is.EqualTo(FieldDisplayKind.Expandable));

            var level2 = level1.Children[0];
            Assert.That(level2.Name, Is.EqualTo(nameof(Level1.L)));
            Assert.That(level2.Kind, Is.EqualTo(FieldDisplayKind.Expandable), "and so is the level below it");
        }

        [Test]
        public void DeepNesting_ResolvesToTheInnermostLeaf()
        {
            var diff = Compare(typeof(DeeplyNested),
                new DeeplyNested { L = new Level1 { L = new Level2 { X = 1f, Y = 2f }, Tail = 7 } },
                new DeeplyNested { L = new Level1 { L = new Level2 { X = 1f, Y = 5f }, Tail = 7 } });

            Assert.That(diff.ChangedPaths, Is.EquivalentTo(new[] { "L/L/Y" }));
        }

        [Test]
        public void FloatLeaf_RespectsTheFuzzyFactor()
        {
            var within = Compare(typeof(VectorComponent),
                new VectorComponent { V = new float3(1f, 2f, 3f) },
                new VectorComponent { V = new float3(1f, 2f, 3f + k_Epsilon * 0.5f) });
            Assert.That(within.TotalChanged, Is.Zero, "inside the fuzzy factor, as the trace would also decide");

            var beyond = Compare(typeof(VectorComponent),
                new VectorComponent { V = new float3(1f, 2f, 3f) },
                new VectorComponent { V = new float3(1f, 2f, 3f + k_Epsilon * 10f) });
            Assert.That(beyond.ChangedPaths, Is.EquivalentTo(new[] { "V/z" }));
        }

        [Test]
        public void TransformShape_StaysInline()
        {
            // Math vectors are read as tuples everywhere else in the Editor, so they keep their single line
            // whatever their width: the quaternion is four values but is not broken into x/y/z/w rows.
            var root = ComponentFieldTree.GetRoot(typeof(TransformLike));
            foreach (var child in root.Children)
                Assert.That(child.Kind, Is.EqualTo(FieldDisplayKind.Inline).Or.EqualTo(FieldDisplayKind.Scalar));
        }

        [Test]
        public void Matrix_CollapsesToExpandable()
        {
            var matrix = Child(typeof(MatrixComponent), nameof(MatrixComponent.M));
            Assert.That(matrix.Kind, Is.EqualTo(FieldDisplayKind.Expandable), "16 scalars is past the inline budget");
            Assert.That(matrix.LeafCount, Is.EqualTo(16));
            Assert.That(matrix.LeafCount, Is.GreaterThan(ComponentFieldTree.MaxInlineMathLeaves),
                "a matrix is a math type, so only the budget keeps it out of a single line");
        }

        [Test]
        public void Matrix_CountsChangedLeavesForTheSummary()
        {
            var client = float4x4.identity;
            var server = float4x4.identity;
            server.c2.z = 42f;

            var diff = Compare(typeof(MatrixComponent),
                new MatrixComponent { M = client },
                new MatrixComponent { M = server });

            Assert.That(diff.TotalChanged, Is.EqualTo(1));
            Assert.That(diff.ChangedLeafCounts["M"], Is.EqualTo(1), "the collapsed summary reports one changed value");
        }

        [Test]
        public unsafe void FixedBuffer_ExposesEveryElement()
        {
            var values = Child(typeof(FixedFloatBuffer), nameof(FixedFloatBuffer.Values));
            Assert.That(values.LeafCount, Is.EqualTo(4), "all four elements are addressable, not just element 0");

            var client = new FixedFloatBuffer();
            var server = new FixedFloatBuffer();
            for (var i = 0; i < 4; i++)
                client.Values[i] = server.Values[i] = i;
            server.Values[2] = 99f;

            var diff = Compare(typeof(FixedFloatBuffer), client, server);
            Assert.That(diff.ChangedPaths, Is.EquivalentTo(new[] { "Values[2]" }));
        }

        [Test]
        public void LongFixedBuffer_CollapsesToExpandable()
        {
            var values = Child(typeof(LongFixedBuffer), nameof(LongFixedBuffer.Values));
            Assert.That(values.Kind, Is.EqualTo(FieldDisplayKind.Expandable));
            Assert.That(values.LeafCount, Is.EqualTo(20));
        }

        [Test]
        public void EntityField_IsOneToken_AndDiffsLikeAnyOtherField()
        {
            var target = Child(typeof(EntityRef), nameof(EntityRef.Target));
            Assert.That(target.Kind, Is.EqualTo(FieldDisplayKind.Opaque), "an entity reference reads as one token");

            // A client relying on an entity reference matching the server's is a real cause of
            // misprediction, so a diverging reference has to be reported like any other value.
            var diff = Compare(typeof(EntityRef),
                new EntityRef { Target = new Entity { Index = 3, Version = 1 }, Value = 7 },
                new EntityRef { Target = new Entity { Index = 88, Version = 2 }, Value = 7 });

            Assert.That(diff.ChangedPaths, Is.EquivalentTo(new[] { "Target" }));
            Assert.That(diff.LeafValues.ContainsKey("Target"), Is.True, "the tooltip needs both values");
        }

        // A 2-step stays under the fuzzy threshold; the full-range distance clears it. Both orders agree.
        [TestCase(5u, 3u, false)]
        [TestCase(3u, 5u, false)]
        [TestCase(0u, uint.MaxValue, true)]
        [TestCase(uint.MaxValue, 0u, true)]
        public void UnsignedInt_FollowsTheDiffersMagnitude(uint clientValue, uint serverValue, bool expectDiff)
        {
            // TypeDiffer.Distance subtracts in the value's own width and converts to float last, so it
            // reports the true |b - a| and the two orders are symmetric. The table has to reproduce that
            // verdict: a masked field it decides is equal gets no highlight, and the component reddens
            // wholesale as unattributed instead.
            const float epsilon = 10f;
            var client = new UnsignedHolder { Value = clientValue };
            var server = new UnsignedHolder { Value = serverValue };

            // The differ reports an amount and leaves the threshold to the caller, so apply it here the
            // way the table does before comparing the two verdicts.
            var amounts = new float[TypeDiffer.MaxFieldBits];
            var mask = TypeDiffer.ComputeUnitDiffs(typeof(UnsignedHolder), client, server, amounts);
            Assert.That(mask != 0 && amounts[0] > epsilon, Is.EqualTo(expectDiff), "the trace's own arithmetic");

            var diff = ComponentFieldTree.Compare(ComponentFieldTree.GetRoot(typeof(UnsignedHolder)), client, server, epsilon);
            Assert.That(diff.ChangedPaths.Count != 0, Is.EqualTo(expectDiff), "the table must explain the mask, not contradict it");
        }

        [Test]
        public unsafe void BoolFixedBuffer_DiffsPastItsFirstElement()
        {
            // bool buffers cannot be pinned, so they stay one opaque token. That token still has to
            // compare by its bytes: the compiler-generated wrapper declares only element 0, so
            // ValueType.Equals calls a change at any other index equal and the buffer's mask bit — which
            // the runtime does set, memcmping the whole buffer — would land on a token showing no diff.
            var client = new BoolFixedBuffer();
            var server = new BoolFixedBuffer();
            server.Values[1] = true;

            var values = Child(typeof(BoolFixedBuffer), nameof(BoolFixedBuffer.Values));
            Assert.That(values.Kind, Is.EqualTo(FieldDisplayKind.Opaque));

            var mask = TypeDiffer.ComputeUnitDiffs(typeof(BoolFixedBuffer), client, server, new float[TypeDiffer.MaxFieldBits]);
            Assert.That(mask, Is.EqualTo(1UL << 0), "the runtime memcmps the buffer as one unit");

            Assert.That(Compare(typeof(BoolFixedBuffer), client, server).ChangedPaths,
                Is.EquivalentTo(new[] { "Values" }));
            Assert.That(Compare(typeof(BoolFixedBuffer), client, new BoolFixedBuffer()).ChangedPaths, Is.Empty,
                "identical buffers must stay quiet");
        }

        [Test]
        public void BlobReference_IsUnsupported_AndNeverDiffs()
        {
            var blob = Child(typeof(BlobHolder), nameof(BlobHolder.Blob));
            Assert.That(blob.Kind, Is.EqualTo(FieldDisplayKind.Unsupported),
                "its pointer overlaps a long, so comparing it would compare addresses");

            var diff = Compare(typeof(BlobHolder), new BlobHolder { Value = 1 }, new BlobHolder { Value = 1 });
            Assert.That(diff.TotalChanged, Is.Zero);
        }

        [Test]
        public void NestedBlobReference_DoesNotTakeItsSiblingsDownWithIt()
        {
            // The differ skips the pointer and keeps walking the struct, so a change to Value still reaches
            // the mask. Collapsing the whole struct into one unsupported token would leave that bit with
            // nothing to highlight, and the table would show a mispredicted component with no diff on it.
            var data = Child(typeof(NestedBlobHolder), nameof(NestedBlobHolder.Data));
            Assert.That(data.Kind, Is.Not.EqualTo(FieldDisplayKind.Unsupported));
            Assert.That(data.Children, Has.Length.EqualTo(2));
            foreach (var child in data.Children)
            {
                var expected = child.Name == nameof(BlobAndValue.Blob)
                    ? FieldDisplayKind.Unsupported
                    : FieldDisplayKind.Scalar;
                Assert.That(child.Kind, Is.EqualTo(expected), child.Name);
            }

            var diff = Compare(typeof(NestedBlobHolder),
                new NestedBlobHolder { Data = new BlobAndValue { Value = 1 } },
                new NestedBlobHolder { Data = new BlobAndValue { Value = 2 } });

            Assert.That(diff.ChangedPaths, Is.EquivalentTo(new[] { "Data/Value" }));
        }

        [Test]
        public void FixedString_IsOneOpaqueToken()
        {
            var text = Child(typeof(StringHolder), nameof(StringHolder.Text));
            Assert.That(text.Kind, Is.EqualTo(FieldDisplayKind.Opaque), "it prints itself; exposing its bytes would be noise");

            var diff = Compare(typeof(StringHolder),
                new StringHolder { Text = "hello" },
                new StringHolder { Text = "world" });
            Assert.That(diff.ChangedPaths, Is.EquivalentTo(new[] { "Text" }));
        }

        [Test]
        public void ManyLeaves_AreAllAddressable_PastTheOldSixtyFourBitCeiling()
        {
            // Five matrices is 80 scalars. The diff mask folded everything past bit 63 onto one bit, so
            // leaf 79 and leaf 63 shared a highlight; the tree addresses each one by path.
            var client = new FiveMatrices();
            var server = new FiveMatrices();
            server.E.c3.w = 123f;

            var diff = Compare(typeof(FiveMatrices), client, server);
            Assert.That(diff.ChangedPaths, Is.EquivalentTo(new[] { "E/c3/w" }));
        }

        struct FiveMatrices : IComponentData
        {
            public float4x4 A;
            public float4x4 B;
            public float4x4 C;
            public float4x4 D;
            public float4x4 E;
        }

        struct BigBlock
        {
            public float4x4 A;
            public float4x4 B;
            public float4x4 C;
            public float4x4 D;
            public float4x4 E;
            public float4x4 F;
            public float4x4 G;
            public float4x4 H;
        }

        struct BigComponent : IComponentData
        {
            public BigBlock Block;
            public int Tail;
        }

        enum Zone : byte
        {
            Idle,
            Running,
        }

        struct MixedInner
        {
            public bool Flag;
            public float Value;
            public FixedString32Bytes Tag;
        }

        unsafe struct MixedComponent : IComponentData
        {
            public MixedInner Inner;
            public float3 Pos;
            public float4x4 Matrix;
            public Entity Owner;
            public Zone State;
            public bool Enabled;
            public FixedString64Bytes Name;
            public fixed float Samples[4];
            public BlobAssetReference<int> Blob;
        }

        [Test]
        public void MixedComponent_ClassifiesEachFieldIndependently()
        {
            Assert.That(Child(typeof(MixedComponent), "Inner").Kind, Is.EqualTo(FieldDisplayKind.Expandable),
                "a struct gets a row per field rather than one tuple");
            Assert.That(Child(typeof(MixedComponent), "Inner").PositionalChildren, Is.False, "a mixed struct labels its fields");
            Assert.That(Child(typeof(MixedComponent), "Pos").Kind, Is.EqualTo(FieldDisplayKind.Inline));
            Assert.That(Child(typeof(MixedComponent), "Pos").PositionalChildren, Is.True, "a vector stays a bare tuple");
            Assert.That(Child(typeof(MixedComponent), "Matrix").Kind, Is.EqualTo(FieldDisplayKind.Expandable));
            Assert.That(Child(typeof(MixedComponent), "Owner").Kind, Is.EqualTo(FieldDisplayKind.Opaque));
            Assert.That(Child(typeof(MixedComponent), "State").Kind, Is.EqualTo(FieldDisplayKind.Scalar));
            Assert.That(Child(typeof(MixedComponent), "Enabled").Kind, Is.EqualTo(FieldDisplayKind.Scalar));
            Assert.That(Child(typeof(MixedComponent), "Name").Kind, Is.EqualTo(FieldDisplayKind.Opaque));
            Assert.That(Child(typeof(MixedComponent), "Samples").Kind, Is.EqualTo(FieldDisplayKind.Expandable),
                "a buffer folds out however short it is");
            Assert.That(Child(typeof(MixedComponent), "Samples").PositionalChildren, Is.True, "buffer elements are positional");
            Assert.That(Child(typeof(MixedComponent), "Blob").Kind, Is.EqualTo(FieldDisplayKind.Unsupported));
        }

        [Test]
        public void MixedComponent_FindsTheChangedLeafAmongUnlikeNeighbours()
        {
            var client = new MixedComponent
            {
                Inner = new MixedInner { Flag = true, Value = 1.5f, Tag = "abc" },
                Pos = new float3(1f, 2f, 3f),
                Matrix = float4x4.identity,
                Owner = new Entity { Index = 3, Version = 1 },
                State = Zone.Idle,
                Enabled = true,
                Name = "cube",
            };

            // One leaf per kind, changed one at a time, must be isolated every time.
            var boolChange = client; boolChange.Enabled = false;
            Assert.That(Compare(typeof(MixedComponent), client, boolChange).ChangedPaths, Is.EquivalentTo(new[] { "Enabled" }));

            var enumChange = client; enumChange.State = Zone.Running;
            Assert.That(Compare(typeof(MixedComponent), client, enumChange).ChangedPaths, Is.EquivalentTo(new[] { "State" }));

            var stringChange = client; stringChange.Name = "sphere";
            Assert.That(Compare(typeof(MixedComponent), client, stringChange).ChangedPaths, Is.EquivalentTo(new[] { "Name" }));

            var nestedStringChange = client; nestedStringChange.Inner.Tag = "xyz";
            Assert.That(Compare(typeof(MixedComponent), client, nestedStringChange).ChangedPaths, Is.EquivalentTo(new[] { "Inner/Tag" }));

            var nestedBoolChange = client; nestedBoolChange.Inner.Flag = false;
            Assert.That(Compare(typeof(MixedComponent), client, nestedBoolChange).ChangedPaths, Is.EquivalentTo(new[] { "Inner/Flag" }));

            var deepFloatChange = client; deepFloatChange.Matrix.c2.z = 42f;
            Assert.That(Compare(typeof(MixedComponent), client, deepFloatChange).ChangedPaths, Is.EquivalentTo(new[] { "Matrix/c2/z" }));
        }

        [Test]
        public unsafe void MixedComponent_IsolatesABufferElementFromItsNeighbours()
        {
            var client = new MixedComponent { Enabled = true, Name = "cube" };
            var server = client;
            server.Samples[2] = 5f;

            Assert.That(Compare(typeof(MixedComponent), client, server).ChangedPaths,
                Is.EquivalentTo(new[] { "Samples[2]" }));
        }

        struct SlotEntry
        {
            public int Key;
            public float Value;
        }

        struct ListComponent : IComponentData
        {
            public FixedList128Bytes<SlotEntry> Lookup;
        }

        static ListComponent MakeList(params (int key, float value)[] entries)
        {
            var component = new ListComponent();
            foreach (var (key, value) in entries)
                component.Lookup.Add(new SlotEntry { Key = key, Value = value });
            return component;
        }

        [Test]
        public void FixedList_IsAnExpandableListOfEntries_NotItsBackingBytes()
        {
            var lookup = Child(typeof(ListComponent), nameof(ListComponent.Lookup));
            Assert.That(lookup.Kind, Is.EqualTo(FieldDisplayKind.Expandable));
            Assert.That(lookup.IsList, Is.True);
            // Entries, each of which is a struct with its own fields — not the 128-byte blob underneath.
            Assert.That(lookup.Children[0].Type, Is.EqualTo(typeof(SlotEntry)));
            Assert.That(lookup.Children[0].Kind, Is.EqualTo(FieldDisplayKind.Expandable), "an entry is a struct, so it folds out too");
        }

        [Test]
        public void FixedList_ResolvesAChangeToOneEntryField()
        {
            var diff = Compare(typeof(ListComponent),
                MakeList((0, 1f), (1, 2f), (2, 3f)),
                MakeList((0, 1f), (1, 99f), (2, 3f)));

            Assert.That(diff.ChangedPaths, Is.EquivalentTo(new[] { "Lookup[1]/Value" }));
            Assert.That(diff.ChangedLeafCounts["Lookup"], Is.EqualTo(1));
            Assert.That(diff.ChangedLengthPaths, Is.Empty, "same number of entries");
            Assert.That(diff.ListRowCounts["Lookup"], Is.EqualTo(3));
        }

        [Test]
        public void FixedList_ReportsALengthDifference()
        {
            var diff = Compare(typeof(ListComponent),
                MakeList((0, 1f), (1, 2f)),
                MakeList((0, 1f), (1, 2f), (2, 3f)));

            Assert.That(diff.ChangedLengthPaths, Is.EquivalentTo(new[] { "Lookup" }));
            // Both cells draw three rows so the shared entries stay aligned and the extra one stands out.
            Assert.That(diff.ListRowCounts["Lookup"], Is.EqualTo(3));
            // The entry the other side lacks is changed leaf by leaf, not as a single path: the table draws
            // an entry's members, so marking only "Lookup[2]" would leave the side that has it unhighlighted.
            Assert.That(diff.ChangedPaths, Is.SupersetOf(new[] { "Lookup[2]/Key", "Lookup[2]/Value" }));
            Assert.That(diff.ChangedLeafCounts["Lookup[2]"], Is.EqualTo(2), "both members of the extra entry");
            Assert.That(diff.ChangedLeafCounts["Lookup"], Is.EqualTo(2));
        }

        [Test]
        public void FixedList_ComparesOnlyEntriesBothSidesHave()
        {
            // Entries past Length hold stale bytes; reading them would invent differences.
            var client = MakeList((0, 1f), (1, 2f), (2, 3f));
            client.Lookup.RemoveAt(2);
            var server = MakeList((0, 1f), (1, 2f));

            var diff = Compare(typeof(ListComponent), client, server);
            Assert.That(diff.TotalChanged, Is.Zero);
            Assert.That(diff.ChangedLengthPaths, Is.Empty);
        }

        [Test]
        public void LargeComponent_ResolvesOneLeafOutOfHundreds()
        {
            var block = Child(typeof(BigComponent), nameof(BigComponent.Block));
            Assert.That(block.Kind, Is.EqualTo(FieldDisplayKind.Expandable));
            Assert.That(block.LeafCount, Is.EqualTo(128));

            var server = new BigBlock();
            server.H.c3.w = 42f;
            var diff = Compare(typeof(BigComponent), new BigComponent(), new BigComponent { Block = server });

            Assert.That(diff.ChangedPaths, Is.EquivalentTo(new[] { "Block/H/c3/w" }));
            // Each level knows how many changed leaves it owns, which is what drives the drill-down.
            Assert.That(diff.ChangedLeafCounts["Block"], Is.EqualTo(1));
            Assert.That(diff.ChangedLeafCounts["Block/H"], Is.EqualTo(1));
            Assert.That(diff.ChangedLeafCounts["Block/A"], Is.Zero);
        }
    }
}
