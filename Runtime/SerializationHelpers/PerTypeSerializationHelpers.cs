using System;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine.Assertions;

namespace SerializationHelpers
{
    /// <summary>
    /// Various serialization helpers for non-primitive types. Can be used in templates to serialize those specific types.
    /// </summary>
    public static class PerTypeSerializationHelpers
    {
        #region NetworkEndpoint
        /// <summary>
        /// Serializes a NetworkEndpoint using packed DataStreamWriter methods (bit by bit).
        /// Allows writing to an already packed stream like snapshot serialization or packed commands
        /// </summary>
        /// <param name="value">The value to serialize</param>
        /// <param name="writer">The writer provided by the template's Serialize method</param>
        public static void SerializeNetworkEndpointPacked(NetworkEndpoint value, ref DataStreamWriter writer)
        {
            Assert.IsTrue((uint)value.Family <= 255); // sanity checking in case transport changes their enum
            writer.WriteRawBits((uint)value.Family, 8);
            if (value.Family != NetworkFamily.Invalid)
            {
                var adrBytes = value.GetRawAddressBytes();
                for (int i = 0; i < adrBytes.Length; i++)
                {
                    // writes variable amount of data to the stream depending on family. 4 bytes for IPv4, 16 for IPv6 and 60 for custom
                    writer.WriteRawBits(adrBytes[i], 8);
                }

                if (value.Family == NetworkFamily.Ipv4 || value.Family == NetworkFamily.Ipv6)
                    writer.WriteRawBits(value.Port, 16);
            }
        }

        /// <summary>
        /// Symmetrical Deserialize for the <see cref="SerializeNetworkEndpointPacked"/> method
        /// </summary>
        /// <param name="reader">The reader provided by the template's Deserialize method</param>
        /// <returns>The NetworkEndpoint read from the reader</returns>
        public static NetworkEndpoint DeserializeNetworkEndpointPacked(ref DataStreamReader reader)
        {
            NetworkEndpoint value = default;
            value.Family = (NetworkFamily)reader.ReadRawBits(8);
            if (value.Family != NetworkFamily.Invalid)
            {
                var adrBytes = new NativeArray<byte>(value.Length, Allocator.Temp); // reads variable amount of data (dynamically set by Length) to the stream depending on family. 4 bytes for IPv4, 16 for IPv6 and 60 for custom
                for (int i = 0; i < value.Length; i++)
                {
                    adrBytes[i] = (byte)reader.ReadRawBits(8);
                }

                value.SetRawAddressBytes(adrBytes, value.Family);
                if (value.Family == NetworkFamily.Ipv4 || value.Family == NetworkFamily.Ipv6)
                    value.Port = (ushort)reader.ReadRawBits(16);
            }

            return value;
        }

        /// <summary>
        /// Serializes a NetworkEndpoint using unpacked DataStreamWriter methods (byte aligned).
        /// Allows writing to an already unpacked stream like RPC serialization.
        /// Note: This should ONLY be used on unpacked streams
        /// </summary>
        /// <param name="value">The value to serialize</param>
        /// <param name="writer">The writer provided by the template's Serialize method</param>
        public static void SerializeNetworkEndpointUnpacked(NetworkEndpoint value, ref DataStreamWriter writer)
        {
            writer.WriteByte((byte)value.Family);
            if (value.Family != NetworkFamily.Invalid)
            {
                writer.WriteBytes(value.GetRawAddressBytes()); // writes variable amount of data to the stream depending on family. 4 bytes for IPv4, 16 for IPv6 and 60 for custom
                if (value.Family == NetworkFamily.Ipv4 || value.Family == NetworkFamily.Ipv6)
                    writer.WriteUShort(value.Port);
            }
        }

        /// <summary>
        /// Symmetrical Deserialize for the <see cref="SerializeNetworkEndpointUnpacked"/> method
        /// </summary>
        /// <param name="reader">The reader provided by the template's Deserialize method</param>
        /// <returns>The NetworkEndpoint read from the reader</returns>
        public static NetworkEndpoint DeserializeNetworkEndpointUnpacked(ref DataStreamReader reader)
        {
            NetworkEndpoint value = default;
            value.Family = (NetworkFamily)reader.ReadByte();
            if (value.Family != NetworkFamily.Invalid)
            {
                var adrBytes = new NativeArray<byte>(value.Length, Allocator.Temp); // reads variable amount of data (dynamically set by Length) to the stream depending on family. 4 bytes for IPv4, 16 for IPv6 and 60 for custom
                reader.ReadBytes(adrBytes);
                value.SetRawAddressBytes(adrBytes, value.Family);
                if (value.Family == NetworkFamily.Ipv4 || value.Family == NetworkFamily.Ipv6)
                    value.Port = reader.ReadUShort();
            }

            return value;
        }
        #endregion
    }
}
