#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
#if NETCODE_DEBUG
using Unity.Collections;
using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Unity.NetCode.LowLevel.Unsafe
{
    [Obsolete("The NetDebugPacket has been deprecated and will be removed in future releases.", false)]
    public struct NetDebugPacket : IDisposable
    {
        public bool IsCreated => false;

        public void Init(in FixedString512Bytes logFolder, in FixedString128Bytes worldName, int connectionId)
        {
        }
        public void Log(in FixedString512Bytes msg)
        {
        }
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Internal class, used to append packet dumps logs to file. Detect and ses Unity.Logging if present, ortherwise the
    /// System.IO.File api is used by default.
    /// </summary>
    unsafe struct PacketDumpLogger : IDisposable
    {
        [NativeDisableUnsafePtrRestriction] private IntPtr m_FileStream;
        [NativeDisableUnsafePtrRestriction] private UnsafeText* m_buffer;

        struct FlushJob : IJob
        {
            [NativeDisableUnsafePtrRestriction]
            public IntPtr m_FileStream;
            [NativeDisableUnsafePtrRestriction]
            public UnsafeText* m_buffer;

            public void Execute()
            {
                //if(m_FileInterop.IsCreated && m_buffer.Length > 0)
                ManagedFileInterop.Write(m_FileStream, *m_buffer);
                m_buffer->Clear();
            }
        }

        public bool IsCreated => m_FileStream != IntPtr.Zero;

        public void Init(in FixedString512Bytes logFolder, in FixedString128Bytes worldName, int connectionId)
        {
            m_FileStream = ManagedFileInterop.Open($"{logFolder}/NetcodePacket-{worldName}-{connectionId}.log");
            m_buffer = (UnsafeText*)UnsafeUtility.Malloc(sizeof(UnsafeText), 16, Allocator.Persistent);
            *m_buffer = new UnsafeText(32 * 1024, Allocator.Persistent);
        }

        public void Log(in FixedString512Bytes msg)
        {
            if (IsCreated)
            {
                if (m_buffer->Length + msg.Length >= m_buffer->Capacity)
                {
                    ManagedFileInterop.Write(m_FileStream, *m_buffer);
                    m_buffer->Clear();
                }
                m_buffer->Append(msg);
            }
        }

        public void Flush()
        {
            if (IsCreated)
            {
                ManagedFileInterop.Write(m_FileStream, *m_buffer);
                m_buffer->Clear();
            }
        }

        public JobHandle Flush(JobHandle depedency)
        {
            if (IsCreated)
            {
                return new FlushJob
                {
                    m_FileStream = m_FileStream,
                    m_buffer = m_buffer
                }.Schedule(depedency);
            }
            return depedency;
        }

        public void Dispose()
        {
            if (IsCreated)
            {
                m_buffer->Dispose();
                UnsafeUtility.Free(m_buffer, Allocator.Persistent);
                ManagedFileInterop.Close(m_FileStream);
                m_FileStream = IntPtr.Zero;
            }
        }
    }
}
#endif
