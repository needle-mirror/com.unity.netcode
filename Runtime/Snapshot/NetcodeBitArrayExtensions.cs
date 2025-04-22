using System;
using System.Diagnostics;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    ///     For <see cref="UnsafeBitArray" />.
    ///     Only needed until those changes land in those packages.
    /// </summary>
    public static class NetcodeBitArrayExtensions
    {
        /// <summary>
        /// Shifts the entire bit array left (in other words: upwards away from 0, towards <see cref="UnsafeBitArray.Capacity"/>).
        /// Discards all bits shifted off the top, and all new bits shifted into existence from the bottom are 0.
        /// </summary>
        /// <param name="bitArray">Instance to apply the operation on.</param>
        /// <param name="shiftBits">How far should all the bits be shifted (in number of bits i.e. bit indexes)?</param>
        public static unsafe void ShiftLeftExt(ref this UnsafeBitArray bitArray, int shiftBits)
        {
            if (shiftBits >= bitArray.Capacity)
            {
                bitArray.Clear();
                return;
            }
            CheckShiftArgs(shiftBits);

            var ptrLength = bitArray.Capacity >> 6;

            // Shift entire 64bit blocks first:
            {
                var num64BitHops = shiftBits >> 6;
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
                UnityEngine.Debug.Assert(num64BitHops < ptrLength);
#endif
                for (int i = ptrLength - num64BitHops - 1; i >= 0; i--)
                    bitArray.Ptr[i + num64BitHops] = bitArray.Ptr[i];
                // Zero out bottom indexes.
                for (int i = 0; i < num64BitHops; i++)
                    bitArray.Ptr[i] = 0;
                shiftBits -= num64BitHops * 64;
            }

            // Shift any remaining bits, running backwards (downwards) so we don't clobber previous values.
            if (shiftBits > 0)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
                UnityEngine.Debug.Assert(shiftBits < 64);
#endif
                for (int i = ptrLength - 1; i >= 1; i--)
                {
                    bitArray.Ptr[i] <<= shiftBits;
                    bitArray.Ptr[i] |= bitArray.Ptr[i - 1] >> (64 - shiftBits);
                }

                bitArray.Ptr[0] <<= shiftBits;
            }
        }

        /// <summary>
        /// Shifts the entire bit array right (in other words: downwards towards 0, away from <see cref="UnsafeBitArray.Capacity"/>).
        /// Discards all bits shifted off the bottom, and all new bits shifted into existence at the top are 0.
        /// </summary>
        /// <param name="bitArray">Instance to apply the operation on.</param>
        /// <param name="shiftBits">How far should all the bits be shifted (in number of bits i.e. bit indexes)?</param>
        public static unsafe void ShiftRightExt(ref this UnsafeBitArray bitArray, int shiftBits)
        {
            if (shiftBits >= bitArray.Capacity)
            {
                bitArray.Clear();
                return;
            }

            CheckShiftArgs(shiftBits);
            var ptrLength = bitArray.Capacity >> 6;

            // Shift entire 64bit blocks first:
            {
                var num64BitHops = shiftBits >> 6;
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
                UnityEngine.Debug.Assert(num64BitHops < ptrLength);
#endif
                for (int i = 0; i < ptrLength - num64BitHops; i++)
                    bitArray.Ptr[i] = bitArray.Ptr[i + num64BitHops];
                // Zero out top indexes.
                for (int i = ptrLength - num64BitHops; i < ptrLength; i++)
                    bitArray.Ptr[i] = 0;
                shiftBits -= num64BitHops * 64;
            }

            // Shift any remaining bits.
            if (shiftBits > 0)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS || UNITY_DOTS_DEBUG
                UnityEngine.Debug.Assert(shiftBits < 64);
#endif
                for (int i = 0; i < ptrLength - 1; i++)
                {
                    bitArray.Ptr[i] >>= shiftBits;
                    bitArray.Ptr[i] |= bitArray.Ptr[i + 1] << (64 - shiftBits);
                }

                bitArray.Ptr[ptrLength - 1] >>= shiftBits;
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS"), Conditional("UNITY_DOTS_DEBUG")]
        static void CheckShiftArgs(int shiftBits)
        {
            if (shiftBits < 0)
                throw new ArgumentOutOfRangeException($"Shift called with negative bits value {shiftBits}!");
        }

        /// <summary>Logs a human-readable format for this bit array.</summary>
        /// <param name="bitArray">Instance to apply the operation on.</param>
        /// <param name="maxFixedStringLength">Denotes the max fixed string length, if you want a concatenated string.</param>
        /// <returns>Example: <c>BitArray[num_bits,length,numTrueBits,indexOfLastTrueBit][10011100-00000000-00100000-00000000-00000000-00000000-00000000-0000000...]</c></returns>
        public static unsafe FixedString4096Bytes ToDecimalFixedStringExt(ref this UnsafeBitArray bitArray, int maxFixedStringLength = 4093)
        {
            var ptrLength = bitArray.Capacity >> 6;
            var lastTrueBitIndex = bitArray.FindLastSetBitExt();
            var numTrueBits = lastTrueBitIndex >= 0 ? bitArray.CountBits(0, lastTrueBitIndex + 1) : 0;
            FixedString32Bytes end = default;
            if (numTrueBits == 0)
                end = ",ZEROS";
            else if (numTrueBits == bitArray.Length)
                end = ",ONES";

            FixedString4096Bytes sb = $"BitArray[bits:{bitArray.Length},len:{ptrLength}ul,num1s:{numTrueBits},last1:{lastTrueBitIndex}{end}][";
            var exitCap = math.min(maxFixedStringLength, sb.Capacity);
            for (var i = 0; i < ptrLength; i++)
            {
                var maxBit = i == ptrLength - 1 && bitArray.Length != bitArray.Capacity ? bitArray.Length % 64 : 64;
                for (int b = 0; b < maxBit; b++)
                {
                    sb.Append((1ul << b & bitArray.Ptr[i]) != 0 ? '1' : '0');
                    if (exitCap - sb.Length <= 5)
                    {
                        sb.Append((FixedString32Bytes) "...");
                        goto doubleBreak;
                    }

                    if (b % 8 == 7) sb.Append(b != 63 ? '_' : '|');
                }
            }

            doubleBreak:
            sb.Append(']');
            return sb;
        }

        /// <summary>Finds the index of the last true bit in the BitArray, and returns said bit index.</summary>
        /// <param name="bitArray">The bitArray to query.</param>
        /// <returns>-1 if no true bits found.</returns>
        public static unsafe int FindLastSetBitExt(ref this UnsafeBitArray bitArray)
        {
            var ptrLength = bitArray.Capacity >> 6;
            var ptrIndex = ptrLength - 1;
            // Special case for first index because of length:
            if (bitArray.Length != bitArray.Capacity)
            {
                var maxIndex = bitArray.Length % 64;
                var leastSignificantMask = (1ul << maxIndex) - 1;
                var mask = bitArray.Ptr[ptrIndex] & leastSignificantMask;
                if (mask != default) return (ptrIndex * 64) + (63 - math.lzcnt(mask));
                ptrIndex--;
            }

            for (; ptrIndex >= 0; ptrIndex--)
            {
                var mask = bitArray.Ptr[ptrIndex];
                if (mask != default) return (ptrIndex * 64) + (63 - math.lzcnt(mask));
            }

            return -1;
        }
    }
}
