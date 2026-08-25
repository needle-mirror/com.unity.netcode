using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests.NetDebugTests
{
    struct Message
    {
        public string Condition;
        public string Stacktrace;
        public LogType Type;
    }

    class LogListener : IDisposable
    {
        readonly List<Message> m_Results;
        public List<Message> Results
        {
            get
            {
                return m_Results;
            }
        }
        public LogListener()
        {
            m_Results = new List<Message>();
            Application.logMessageReceivedThreaded += OnLog;
        }

        void OnLog(string condition, string stacktrace, LogType type)
        {
            m_Results.Add(new Message { Condition = condition, Stacktrace = stacktrace, Type = type });
        }

        public void Dispose()
        {
            Application.logMessageReceivedThreaded -= OnLog;
        }
    }

    class NativeStringSupport
    {
        [Test]
        public void Bytes4096()
        {
            using var logListener = new LogListener();
            var netDebug = new Unity.NetCode.NetDebug();
            netDebug.Initialize();
            Assert.DoesNotThrow(() => netDebug.LogError(new FixedString4096Bytes("TestMessage")));
            Assert.That(logListener.Results.Count, Is.EqualTo(1));
            var result = logListener.Results[0];
            Assert.That(result.Condition, Is.EqualTo("TestMessage"));
            Assert.That(result.Type, Is.EqualTo(LogType.Error));
            LogAssert.Expect(LogType.Error, "TestMessage");
        }

        [Test]
        public void Bytes512()
        {
            using var logListener = new LogListener();
            var netDebug = new Unity.NetCode.NetDebug();
            netDebug.Initialize();
            Assert.DoesNotThrow(() => netDebug.LogError(new FixedString512Bytes("TestMessage")));
            Assert.That(logListener.Results.Count, Is.EqualTo(1));
            var result = logListener.Results[0];
            Assert.That(result.Condition, Is.EqualTo("TestMessage"));
            Assert.That(result.Type, Is.EqualTo(LogType.Error));
            LogAssert.Expect(LogType.Error, "TestMessage");
        }
    }
}
