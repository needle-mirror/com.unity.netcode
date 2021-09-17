#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif

using System;
using System.IO;
using Unity.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

#if USE_UNITY_LOGGING
using Unity.Logging;
using Unity.Logging.Internal;
using Unity.Logging.Sinks;
#endif

#if NETCODE_DEBUG
namespace Unity.NetCode.LowLevel.Unsafe
{

#if USE_UNITY_LOGGING
    public unsafe struct NetDebugPacket
    {
        private LoggerHandle m_NetDebugPacketLoggerHandle;

        public void Init(string worldName, int connectionId)
        {
            LogMemoryManagerParameters.GetDefaultParameters(out var parameters);
            parameters.InitialBufferCapacity *= 64;
            parameters.OverflowBufferSize *= 32;

            m_NetDebugPacketLoggerHandle = new LoggerConfig()
                                           .OutputTemplate("{Message}")
                                           .MinimumLevel.Set(LogLevel.Verbose)
                                           .WriteTo.File($"{NetDebug.GetAndCreateLogFolder()}/NetcodePackets-New-{worldName}-{connectionId}.log")
                                           .CreateLogger(parameters).Handle;
        }

        public bool IsCreated => m_NetDebugPacketLoggerHandle.IsValid;

        public void Dispose()
        {
        }

        public void Log(in FixedString512Bytes msg)
        {
            Unity.Logging.Log.To(m_NetDebugPacketLoggerHandle).Info(msg);
        }
    }
#else
    public unsafe struct NetDebugPacket
    {
        [NativeDisableUnsafePtrRestriction]
        private UnsafeList* Messages;

        public int MessageCount => Messages->Length;

        public FixedString512Bytes ElementAt(int index)
        {
            return UnsafeUtility.ReadArrayElement<FixedString512Bytes>(Messages->Ptr, index);
        }

        public void Clear()
        {
            Messages->Clear();
        }

        public void Init(string worldName, int connectionId)
        {
            if (Messages == null)
            {
                Messages = UnsafeList.Create(UnsafeUtility.SizeOf<FixedString512Bytes>(), UnsafeUtility.AlignOf<FixedString512Bytes>(),
                    256, Allocator.Persistent);
            }
        }

        public bool IsCreated => Messages != null && Messages->IsCreated;

        public void Dispose()
        {
            if (Messages != null)
                Messages->Dispose();
        }

        public void Log(in FixedString512Bytes msg)
        {
            Messages->Add(msg);
        }
    }

#endif

}
#endif

namespace Unity.NetCode
{
    public struct EnablePacketLogging : IComponentData
    { }

#if NETCODE_DEBUG
    public struct PrefabDebugName : IComponentData
    {
        public FixedString64Bytes Name;
    }

#if USE_UNITY_LOGGING
    public class NetDebugPacketLoggers
    {
        public NetDebugPacketLoggers() {}

        public void Init(string worldName, int connectionId) {}

        public void Dispose() {}

        public void Process(ref NetDebugPacket logger, int index) {}
    }
#else
    public class NetDebugPacketLoggers
    {
        private Dictionary<int, FileStream> m_LogFileStream;
        private byte[] m_WriteBytes;

        public NetDebugPacketLoggers()
        {
            m_LogFileStream = new Dictionary<int, FileStream>();
            m_WriteBytes = new byte[512];
        }

        public void Init(string worldName, int connectionId)
        {
            var logPath = NetDebug.GetAndCreateLogFolder();
            var fileStream = new FileStream(Path.Combine(logPath, $"NetcodePackets-{worldName}-{connectionId}.log"),
                FileMode.Create, FileAccess.Write, FileShare.None);
            if (m_LogFileStream.ContainsKey(connectionId))
            {
                UnityEngine.Debug.LogError($"Connection {connectionId} already has a packet log file initialized");
            }
            m_LogFileStream.Add(connectionId, fileStream);
        }

        public void Dispose()
        {
            foreach (var file in m_LogFileStream)
            {
                file.Value.Close();
            }
        }

        public void Process(ref NetDebugPacket logger, int index)
        {
            for (int i = 0; i < logger.MessageCount; ++i)
            {
                var msg = logger.ElementAt(i);
                unsafe
                {
                    byte* msgPtr = msg.GetUnsafePtr();
                    fixed (byte* d = m_WriteBytes)
                        UnsafeUtility.MemCpy(d, msgPtr, msg.Length);
                    m_LogFileStream[index].Write(m_WriteBytes, 0, msg.Length);
                }
            }
            logger.Clear();
        }
    }
#endif


#endif

    public struct DisconnectReasonEnumToString
    {
        private static readonly FixedString32Bytes ConnectionClose = "ConnectionClose";
        private static readonly FixedString32Bytes Timeout = "Timeout";
        private static readonly FixedString32Bytes MaxConnectionAttempts = "MaxConnectionAttempts";
        private static readonly FixedString32Bytes ClosedByRemote = "ClosedByRemote";
        private static readonly FixedString32Bytes BadProtocolVersion = "BadProtocolVersion";
        private static readonly FixedString32Bytes InvalidRpc = "InvalidRpc";

        public static FixedString32Bytes Convert(int index)
        {
            switch (index)
            {
                case 0: return ConnectionClose;
                case 1: return Timeout;
                case 2: return MaxConnectionAttempts;
                case 3: return ClosedByRemote;
                case 4: return BadProtocolVersion;
                case 5: return InvalidRpc;
            }
            return "";
        }
    }

    public struct NetDebug
    {
        static public string LogFolderForPlatform()
        {
#if UNITY_DOTSRUNTIME
                var args = Environment.GetCommandLineArgs();
                var optIndex = System.Array.IndexOf(args, "-logFile");
                if (optIndex >=0 && ++optIndex < (args.Length - 1) && !args[optIndex].StartsWith("-"))
                    return args[optIndex];
                //FIXME: should return the common application log path (if that exist defined somewhere)
                return "Logs";
#else
            //by default logs are output in the same location as player and console output does
            return Path.GetDirectoryName(UnityEngine.Application.consoleLogPath);
#endif
        }

        //TODO: logging should already give us a good folder for that purpose by default
        public static string GetAndCreateLogFolder()
        {
            var logPath = LogFolderForPlatform();
            if (!Directory.Exists(logPath))
                Directory.CreateDirectory(logPath);
            return logPath;
        }

        private LogLevelType m_LogLevel;

#if USE_UNITY_LOGGING
        private LogLevel m_CurrentLogLevel;
        private LoggerHandle m_LoggerHandle;

        private void SetLoggerLevel(LogLevelType newLevel)
        {
            m_CurrentLogLevel = newLevel switch
            {
                LogLevelType.Debug => Logging.LogLevel.Debug,
                LogLevelType.Notify => Logging.LogLevel.Info,
                LogLevelType.Warning => Logging.LogLevel.Warning,
                LogLevelType.Error => Logging.LogLevel.Error,
                LogLevelType.Exception => Logging.LogLevel.Fatal,
                _ => throw new ArgumentOutOfRangeException()
            };

            var logger = GetOrCreateLogger();
            logger.SetMinimalLogLevelAcrossAllSinks(m_CurrentLogLevel);
        }

        private Logger GetOrCreateLogger()
        {
            Logger logger = null;
            if (m_LoggerHandle.IsValid)
                logger = LoggerManager.GetLogger(m_LoggerHandle);

            if (logger == null)
            {
                logger = new LoggerConfig().MinimumLevel
                    .Set(m_CurrentLogLevel)
#if !UNITY_DOTSRUNTIME
                    //Use correct format that is compatible with current unity logging
                    .WriteTo.UnityDebugLog(minLevel: m_CurrentLogLevel, outputTemplate: new FixedString512Bytes("{Message}"))
#else
                    .WriteTo.Console()
                    .WriteTo.File($"{NetDebug.GetAndCreateLogFolder()}/Netcode-{Guid.NewGuid()}.txt")
#endif
                    .CreateLogger();
                m_LoggerHandle = logger.Handle;
            }

            return logger;
        }
#endif

        public void Initialize()
        {
            LogLevel = LogLevelType.Notify;
        }
        public void Dispose()
        {
#if USE_UNITY_LOGGING
            if (!m_LoggerHandle.IsValid)
                return;
            var logger = LoggerManager.GetLogger(m_LoggerHandle);
            if (logger != null)
            {
                Logging.Internal.LoggerManager.FlushAll();
                logger.Dispose();
            }
            m_LoggerHandle = default;
#endif
        }

        public LogLevelType LogLevel
        {
            set
            {
                m_LogLevel = value;

#if USE_UNITY_LOGGING
                SetLoggerLevel(m_LogLevel);
#endif
            }
            get => m_LogLevel;
        }

        public enum LogLevelType
        {
            Debug = 1,
            Notify = 2,
            Warning = 3,
            Error = 4,
            Exception = 5,
        }

#if USE_UNITY_LOGGING
        public void DebugLog(in FixedString512Bytes msg)
        {
            Unity.Logging.Log.To(m_LoggerHandle).Debug(msg);
        }

        public void Log(in FixedString512Bytes msg)
        {
            Unity.Logging.Log.To(m_LoggerHandle).Info(msg);
        }

        public void LogWarning(in FixedString512Bytes msg)
        {
            Unity.Logging.Log.To(m_LoggerHandle).Warning(msg);
        }

        public void LogError(in FixedString512Bytes msg)
        {
            Unity.Logging.Log.To(m_LoggerHandle).Error(msg);
        }
#else
        public void DebugLog(in FixedString512Bytes msg)
        {
            if (m_LogLevel == LogLevelType.Debug)
                UnityEngine.Debug.Log(msg);
        }

        public void Log(in FixedString512Bytes msg)
        {
            if (m_LogLevel <= LogLevelType.Notify)
                UnityEngine.Debug.Log(msg);
        }

        public void LogWarning(in FixedString512Bytes msg)
        {
            if (m_LogLevel <= LogLevelType.Warning)
                UnityEngine.Debug.LogWarning(msg);
        }

        public void LogError(in FixedString512Bytes msg)
        {
            if (m_LogLevel <= LogLevelType.Error)
                UnityEngine.Debug.LogError(msg);
        }
#endif
        public static FixedString64Bytes PrintMask(uint mask)
        {
            FixedString64Bytes maskString = default;
            for (int i = 0; i < 32; ++i)
            {
                var bit = (mask>>31)&1;
                mask <<= 1;
                if (maskString.Length == 0 && bit == 0)
                    continue;
                maskString.Append(bit);
            }

            if (maskString.Length == 0)
                maskString = "0";
            return maskString;
        }

        public static FixedString32Bytes PrintHex(ulong value, int bitSize)
        {
            FixedString32Bytes temp = new FixedString32Bytes();
            temp.Add((byte)'0');
            temp.Add((byte)'x');
            if (value == 0)
            {
                temp.Add((byte)'0');
                return temp;
            }
            int i = bitSize;
            do
            {
                i -= 4;
                int nibble = (int) (value >> i) & 0xF;
                if(nibble == 0 && temp.Length == 2)
                    continue;
                nibble += (nibble >= 10) ? 'A' - 10 : '0';
                temp.Add((byte)nibble);
            } while (i > 0);
            return temp;
        }
        public static FixedString32Bytes PrintHex(uint value)
        {
            return PrintHex(value, 32);
        }
        public static FixedString32Bytes PrintHex(ulong value)
        {
            return PrintHex(value, 64);
        }
    }
}
