using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.Tracing;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tests.Editor
{
    // Unit tests for TypeDiffer construction and diff processing: real field offsets (padding),
    // diff amounts (thresholded by the caller), MemCmp collapsing, fixed buffers and managed type guards.
    internal unsafe class TypeDifferTests
    {
        struct PaddedStruct // 3 padding bytes between A and B
        {
            public byte A;
            public int B;
        }

        struct IntPair // packed, collapses to a single MemCmp
        {
            public int A;
            public int B;
        }

        struct FloatPair // floats must keep epsilon comparison, never MemCmp
        {
            public float X;
            public float Y;
        }

        struct NestedMixed // outer struct mixing a padded struct and a float
        {
            public PaddedStruct Inner;
            public float F;
        }

        enum ByteEnum : byte { First, Second }

        struct EnumHolder
        {
            public ByteEnum Value;
        }

        struct FixedByteBuffer
        {
            public fixed byte Buffer[16];
        }

        struct FixedFloatBuffer
        {
            public fixed float Values[4];
        }

        struct FixedStringHolder
        {
            public FixedString64Bytes Text;
        }

        struct EmptyTag
        {
        }

        struct UIntHolder
        {
            public uint V;
        }

        struct ULongHolder
        {
            public ulong V;
        }

        struct WithReferenceField
        {
            public int Value;
            public string Text;
        }

        class ManagedComponent
        {
            public int Value;
        }

        static float RunDiffAmount<T>(T left, T right) where T : unmanaged
        {
            var differ = TypeDiffer.ConstructDifferRecursive(typeof(T), Allocator.Temp);
            try
            {
                return differ.ProcessDiffAmount((byte*)UnsafeUtility.AddressOf(ref left), (byte*)UnsafeUtility.AddressOf(ref right));
            }
            finally
            {
                differ.Dispose();
            }
        }

        // The differ reports amounts; thresholding is the caller's business (the view-side fuzzy filter).
        static bool RunDiff<T>(T left, T right, float threshold = 1e-4f) where T : unmanaged
            => RunDiffAmount(left, right) > threshold;

        [Test]
        public void PaddedStruct_UsesRealFieldOffsets()
        {
            Assert.IsFalse(RunDiff(new PaddedStruct { A = 1, B = 100 }, new PaddedStruct { A = 1, B = 100 }));
            Assert.IsTrue(RunDiff(new PaddedStruct { A = 1, B = 100 }, new PaddedStruct { A = 2, B = 100 }), "change in A not detected");
            Assert.IsTrue(RunDiff(new PaddedStruct { A = 1, B = 100 }, new PaddedStruct { A = 1, B = 200 }), "change in B not detected");
            // Only B's high byte changes: with size-accumulated offsets the differ read B at offset
            // 1 instead of 4 and missed its last bytes entirely.
            Assert.IsTrue(RunDiff(new PaddedStruct { A = 1, B = 0 }, new PaddedStruct { A = 1, B = 1 << 24 }), "change in B's high byte not detected");
        }

        [Test]
        public void PackedIntStruct_DetectsDiffs()
        {
            Assert.IsFalse(RunDiff(new IntPair { A = 1, B = 2 }, new IntPair { A = 1, B = 2 }));
            Assert.IsTrue(RunDiff(new IntPair { A = 1, B = 2 }, new IntPair { A = 1, B = 3 }));
        }

        [Test]
        public void Floats_KeepThresholdSemantics()
        {
            Assert.IsFalse(RunDiff(new FloatPair { X = 1f, Y = 1f }, new FloatPair { X = 1f + 1e-6f, Y = 1f }), "sub-threshold change reported as diff");
            Assert.IsTrue(RunDiff(new FloatPair { X = 1f, Y = 1f }, new FloatPair { X = 2f, Y = 1f }), "above-threshold change not detected");
        }

        [Test]
        public void Amounts_ReportAbsoluteDelta_AndInfinityForNonNumeric()
        {
            Assert.AreEqual(0f, RunDiffAmount(new FloatPair { X = 1f, Y = 1f }, new FloatPair { X = 1f, Y = 1f }));
            Assert.AreEqual(1f, RunDiffAmount(new FloatPair { X = 1f, Y = 1f }, new FloatPair { X = 2f, Y = 1f }));
            // Enums collapse to MemCmp: any change is beyond every threshold.
            Assert.AreEqual(float.PositiveInfinity, RunDiffAmount(new EnumHolder { Value = ByteEnum.First }, new EnumHolder { Value = ByteEnum.Second }));
            // Wide integers must not wrap before the magnitude is taken (|0 - MaxValue| is huge, not 1)...
            Assert.AreEqual((float)uint.MaxValue, RunDiffAmount(new UIntHolder { V = uint.MaxValue }, new UIntHolder { V = 0 }));
            // ...and a small delta on a huge value must stay exact, not round away.
            Assert.AreEqual(3f, RunDiffAmount(new ULongHolder { V = (1UL << 62) + 3 }, new ULongHolder { V = 1UL << 62 }));
        }

        [Test]
        public void NestedStruct_DiffsInnerAndOuterFields()
        {
            var baseline = new NestedMixed { Inner = new PaddedStruct { A = 1, B = 7 }, F = 1f };
            Assert.IsFalse(RunDiff(baseline, new NestedMixed { Inner = new PaddedStruct { A = 1, B = 7 }, F = 1f }));
            Assert.IsTrue(RunDiff(baseline, new NestedMixed { Inner = new PaddedStruct { A = 1, B = 8 }, F = 1f }));
            Assert.IsTrue(RunDiff(baseline, new NestedMixed { Inner = new PaddedStruct { A = 1, B = 7 }, F = 2f }));
        }

        [Test]
        public void Enums_DetectValueChanges()
        {
            Assert.IsFalse(RunDiff(new EnumHolder { Value = ByteEnum.First }, new EnumHolder { Value = ByteEnum.First }));
            Assert.IsTrue(RunDiff(new EnumHolder { Value = ByteEnum.First }, new EnumHolder { Value = ByteEnum.Second }));
        }

        [Test]
        public void FixedByteBuffer_DiffsAllElements()
        {
            var left = new FixedByteBuffer();
            var right = new FixedByteBuffer();
            Assert.IsFalse(RunDiff(left, right));
            // Change the LAST element: the previous implementation only diffed the first one.
            right.Buffer[15] = 42;
            Assert.IsTrue(RunDiff(left, right), "change in last fixed buffer element not detected");
        }

        [Test]
        public void FixedFloatBuffer_KeepsAmountPerElement()
        {
            var left = new FixedFloatBuffer();
            var right = new FixedFloatBuffer();
            for (int i = 0; i < 4; i++)
            {
                left.Values[i] = 1f;
                right.Values[i] = 1f;
            }

            right.Values[3] = 1f + 1e-6f;
            Assert.IsFalse(RunDiff(left, right), "sub-threshold fixed buffer change reported as diff");
            right.Values[3] = 2f;
            Assert.IsTrue(RunDiff(left, right), "above-threshold fixed buffer change not detected");
        }

        [Test]
        public void FixedString_DetectsContentChanges()
        {
            var left = new FixedStringHolder { Text = new FixedString64Bytes("hello") };
            Assert.IsFalse(RunDiff(left, new FixedStringHolder { Text = new FixedString64Bytes("hello") }));
            Assert.IsTrue(RunDiff(left, new FixedStringHolder { Text = new FixedString64Bytes("hellp") }));
            Assert.IsTrue(RunDiff(left, new FixedStringHolder { Text = new FixedString64Bytes("hello!") }));
        }

        [Test]
        public void EmptyTag_NeverDiffs()
        {
            Assert.IsFalse(RunDiff(new EmptyTag(), new EmptyTag()));
        }

        [Test]
        public void ManagedType_IsSkippedSilently()
        {
            var differ = TypeDiffer.ConstructDifferRecursive(typeof(ManagedComponent), Allocator.Temp);
            differ.Dispose();
            // Managed types are skipped without logging (the tracing UI reports them as not captured instead).
            // Any unexpected error (e.g. the previous mono_class_is_valuetype native assert) fails the test.
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ReferenceField_IsSkippedSilently_OtherFieldsStillDiff()
        {
            var differ = TypeDiffer.ConstructDifferRecursive(typeof(WithReferenceField), Allocator.Temp);
            try
            {
                // Diff two stack buffers directly; the string field's differ is a no-op so only
                // Value participates. Buffer is large enough for the struct's unmanaged view.
                var left = stackalloc byte[64];
                var right = stackalloc byte[64];
                var valueOffset = UnsafeUtility.GetFieldOffset(typeof(WithReferenceField).GetField(nameof(WithReferenceField.Value)));
                Assert.IsFalse(TypeDiffer.IsDiffAmount(differ.ProcessDiffAmount(left, right)));
                *(int*)(right + valueOffset) = 123;
                Assert.IsTrue(TypeDiffer.IsDiffAmount(differ.ProcessDiffAmount(left, right)), "change in unmanaged field next to a reference field not detected");
            }
            finally
            {
                differ.Dispose();
            }
        }
    }
}
