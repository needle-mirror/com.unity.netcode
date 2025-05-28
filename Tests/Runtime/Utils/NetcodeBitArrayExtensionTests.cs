using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Assert = Unity.Assertions.Assert;

namespace Unity.NetCode.Tests
{
    internal class NetcodeBitArrayExtensionTests
    {
        [Test]
        public unsafe void UnsafeBitArray_ShiftLeftRightExt([Values] bool up)
        {
            const int numBits = 512;
            using var testBitArray = new UnsafeBitArray(numBits, Allocator.Persistent);
            ref var test = ref UnsafeUtility.AsRef<UnsafeBitArray>(&testBitArray);
            Assert.IsTrue(test.TestNone(0, numBits));
            test.Set(up ? 0 : numBits - 1, true);
            for (int i = 0; i < numBits; i++)
            {
                try
                {
                    Assert.IsTrue(test.IsSet(up ? i : (numBits - 1) - i), i.ToString());
                    Assert.AreEqual(1, test.CountBits(0, numBits), i.ToString());
                    if (up) test.ShiftLeftExt(1);
                    else test.ShiftRightExt(1);
                }
                catch (Exception)
                {
                    UnityEngine.Debug.LogError($"Exception at idx {i}, {test.ToDecimalFixedStringExt()}\"");
                    throw;
                }
            }

            Assert.AreEqual(0, test.CountBits(0, numBits), "Should be no more true bits as they should have been shifted off the ends!");
        }

        [Test]
        public unsafe void UnsafeBitArray_FindLastSetBitExt()
        {
            TestOnLength(129);
            TestOnLength(128);
            TestOnLength(127);
            TestOnLength(1);

            // Test zero:
            var testBitArray = new UnsafeBitArray(0, Allocator.Temp);
            ref var test = ref UnsafeUtility.AsRef<UnsafeBitArray>(&testBitArray);
            Assert.AreEqual(-1, test.FindLastSetBitExt(), "BitArray of size ZERO should return -1 for FindLastSetBit!");

            static void TestOnLength(int bitArrayLength)
            {
                var testBitArray = new UnsafeBitArray(bitArrayLength, Allocator.Temp);
                ref var test = ref UnsafeUtility.AsRef<UnsafeBitArray>(&testBitArray);
                Assert.IsTrue(test.TestNone(0, bitArrayLength), $"TestOnLength[{bitArrayLength}] All bits should START as zero! {test.ToDecimalFixedStringExt()}");
                Assert.AreEqual(-1, test.FindLastSetBitExt(), $"TestOnLength[{bitArrayLength}] FindLastSetBit should be -1 as there should be ZERO true bits! {test.ToDecimalFixedStringExt()}");
                // Set the lowest bit true, so that we can get a false positive.
                test.Set(0, true);

                // Set and test every other index (individually).
                for (int indexToSet = 1; indexToSet < test.Length; indexToSet++)
                {
                    test.Set(indexToSet, true);
                    Assert.AreEqual(2, test.CountBits(0, bitArrayLength), $"TestOnLength[{bitArrayLength},{indexToSet}] UnsafeBitArray.CountBits {test.ToDecimalFixedStringExt()}");
                    var indexOfLastTrueBit = test.FindLastSetBitExt();
                    Assert.AreEqual(indexToSet, indexOfLastTrueBit, $"TestOnLength[{bitArrayLength},{indexToSet}] UnsafeBitArray.FindLastSetBit {test.ToDecimalFixedStringExt()}");
                    test.Set(indexToSet, false);
                }
            }
        }

        [Test]
        public unsafe void UnsafeBitArray_ToDecimalFixedStringExt()
        {
            const int numBits = 128;
            var testBitArray = new UnsafeBitArray(numBits, Allocator.Temp);
            ref var test = ref UnsafeUtility.AsRef<UnsafeBitArray>(&testBitArray);
            Assert.IsTrue(test.TestNone(0, numBits));
            const ulong constant = 15950305135099; // 11101000000110111000010001011000100111111011
            const int trueBits = 22;
            const int idxOfLastTrue = 43;
            FixedString4096Bytes zero = "BitArray[bits:128,len:2ul,num1s:0,last1:-1,ZEROS][00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000000|00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000000|]";
            TestAndAssertShift(ref test, -100, zero);
            TestAndAssertShift(ref test, -44, zero);
            TestAndAssertShift(ref test, -1, "BitArray[bits:128,len:2ul,num1s:21,last1:42][10111111_00100011_01000100_00111011_00000010_11100000_00000000_00000000|00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000000|]");
            TestAndAssertShift(ref test, 0, "BitArray[bits:128,len:2ul,num1s:22,last1:43][11011111_10010001_10100010_00011101_10000001_01110000_00000000_00000000|00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000000|]");
            TestAndAssertShift(ref test, 1, "BitArray[bits:128,len:2ul,num1s:22,last1:44][01101111_11001000_11010001_00001110_11000000_10111000_00000000_00000000|00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000000|]");
            TestAndAssertShift(ref test, 63, "BitArray[bits:128,len:2ul,num1s:22,last1:106][00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000001|10111111_00100011_01000100_00111011_00000010_11100000_00000000_00000000|]");
            TestAndAssertShift(ref test, 64, "BitArray[bits:128,len:2ul,num1s:22,last1:107][00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000000|11011111_10010001_10100010_00011101_10000001_01110000_00000000_00000000|]");
            TestAndAssertShift(ref test, 65, "BitArray[bits:128,len:2ul,num1s:22,last1:108][00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000000|01101111_11001000_11010001_00001110_11000000_10111000_00000000_00000000|]");
            TestAndAssertShift(ref test, 66, "BitArray[bits:128,len:2ul,num1s:22,last1:109][00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000000|00110111_11100100_01101000_10000111_01100000_01011100_00000000_00000000|]");
            TestAndAssertShift(ref test, 127, "BitArray[bits:128,len:2ul,num1s:1,last1:127][00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000000|00000000_00000000_00000000_00000000_00000000_00000000_00000000_00000001|]");
            TestAndAssertShift(ref test, 128, zero);
            TestAndAssertShift(ref test, 129, zero);
            TestAndAssertShift(ref test, int.MaxValue, zero);

            // Test all bits being "ONES":
            test.SetBits(0, true, test.Length);
            Assert.AreEqual("BitArray[bits:128,len:2ul,num1s:128,last1:127,ONES][11111111_11111111_11111111_11111111_11111111_11111111_11111111_11111111|11111111_11111111_11111111_11111111_11111111_11111111_11111111_11111111|]", test.ToDecimalFixedStringExt().ToString());

            void TestAndAssertShift(ref UnsafeBitArray test, int shiftDistance, FixedString4096Bytes expectedResult)
            {
                // Reset:
                test.Clear();
                test.SetBits(0, constant, 64);
                Assert.AreEqual(trueBits, test.CountBits(0, numBits));
                Assert.AreEqual(constant, test.GetBits(0, 64));
                Assert.AreEqual(idxOfLastTrue, test.FindLastSetBitExt());

                // Test:
                if(shiftDistance < 0) test.ShiftRightExt(math.abs(shiftDistance));
                else test.ShiftLeftExt(shiftDistance);
                try
                {
                    Assert.AreEqual(expectedResult.ToString(), test.ToDecimalFixedStringExt().ToString());
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError($"expect:{expectedResult}\nactual:{test.ToDecimalFixedStringExt()} ShiftUp({shiftDistance})");
                    UnityEngine.Debug.LogException(e);
                }
            }
        }
    }
}
