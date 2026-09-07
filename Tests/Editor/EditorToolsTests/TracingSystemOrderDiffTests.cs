using NUnit.Framework;
using Unity.Entities;
using Unity.Netcode.Tracing;

namespace Tests.Editor
{
    /// <summary>
    /// Unit tests for the system-order mismatch detection (<see cref="TickData.ProcessSystemOrderDiff"/>):
    /// systems traced on both sides but running in a different relative order are flagged with
    /// <see cref="DiffInfo.DiffReasons.SystemOrder"/> on the tick and on the mismatching systems.
    /// </summary>
    class TracingSystemOrderDiffTests
    {
        const float k_TickDt = 1f / 60f;

        TickData m_ClientTick;
        TickData m_ServerTick;

        [SetUp]
        public void SetUp()
        {
            TypeManager.Initialize();
            m_ClientTick = new TickData(k_TickDt, default, TraceType.Default);
            m_ServerTick = new TickData(k_TickDt, default, TraceType.Default);
        }

        [TearDown]
        public void TearDown()
        {
            m_ClientTick.Dispose();
            m_ServerTick.Dispose();
        }

        // The tracing state is left empty: ProcessSystemOrderDiff never reads it, and initializing it
        // keeps TickData.Dispose happy.
        static SystemID AddSystem(ref TickData tickData, SystemTypeIndex type, ulong executionOrder)
        {
            var systemID = new SystemID(type, TracePosition.after) { executionOrder = executionOrder };
            var systemData = new SystemData(TracePosition.after, default, WorldID.WorldType.Undefined, TraceType.Default)
            {
                MainGameTracingState = new ProcessedTracingStateData(default),
            };
            tickData.PerSystemData.Add(systemID, systemData);
            tickData.SystemIds.Add(systemID);
            return systemID;
        }

        static DiffInfo.DiffReasons SystemReasons(TickData tickData, SystemID systemID)
            => tickData.PerSystemData[systemID].DiffInfo.DiffReasonFlags;

        [Test]
        public void SharedSystemsInDifferentOrder_FlagTickAndSystems()
        {
            var typeA = TypeManager.GetSystemTypeIndex<SimulationSystemGroup>();
            var typeB = TypeManager.GetSystemTypeIndex<PresentationSystemGroup>();
            var clientA = AddSystem(ref m_ClientTick, typeA, 1);
            var clientB = AddSystem(ref m_ClientTick, typeB, 2);
            var serverB = AddSystem(ref m_ServerTick, typeB, 1);
            var serverA = AddSystem(ref m_ServerTick, typeA, 2);

            m_ClientTick.ProcessSystemOrderDiff(m_ServerTick);

            Assert.That(m_ClientTick.DiffInfo.HasDiff, Is.True);
            Assert.That(m_ClientTick.DiffInfo.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.SystemOrder));
            Assert.That(SystemReasons(m_ClientTick, clientA), Is.EqualTo(DiffInfo.DiffReasons.SystemOrder), "both mismatching client systems should be flagged");
            Assert.That(SystemReasons(m_ClientTick, clientB), Is.EqualTo(DiffInfo.DiffReasons.SystemOrder));
            Assert.That(SystemReasons(m_ServerTick, serverA), Is.EqualTo(DiffInfo.DiffReasons.SystemOrder), "both mismatching server systems should be flagged");
            Assert.That(SystemReasons(m_ServerTick, serverB), Is.EqualTo(DiffInfo.DiffReasons.SystemOrder));
        }

        [Test]
        public void SharedSystemsInSameOrder_AreNotFlagged()
        {
            var typeA = TypeManager.GetSystemTypeIndex<SimulationSystemGroup>();
            var typeB = TypeManager.GetSystemTypeIndex<PresentationSystemGroup>();
            var clientA = AddSystem(ref m_ClientTick, typeA, 1);
            AddSystem(ref m_ClientTick, typeB, 2);
            AddSystem(ref m_ServerTick, typeA, 1);
            AddSystem(ref m_ServerTick, typeB, 2);

            m_ClientTick.ProcessSystemOrderDiff(m_ServerTick);

            Assert.That(m_ClientTick.DiffInfo.HasDiff, Is.False);
            Assert.That(m_ClientTick.DiffInfo.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.Undefined));
            Assert.That(SystemReasons(m_ClientTick, clientA), Is.EqualTo(DiffInfo.DiffReasons.Undefined));
        }

        [Test]
        public void OneSidedSystems_DoNotAffectTheOrderComparison()
        {
            // The client traces an extra system between A and B; the relative order of the shared
            // systems still matches, so nothing should be flagged (one-sided systems are reported
            // separately as missing/extra systems).
            var typeA = TypeManager.GetSystemTypeIndex<SimulationSystemGroup>();
            var typeB = TypeManager.GetSystemTypeIndex<PresentationSystemGroup>();
            var typeClientOnly = TypeManager.GetSystemTypeIndex<InitializationSystemGroup>();
            AddSystem(ref m_ClientTick, typeA, 1);
            AddSystem(ref m_ClientTick, typeClientOnly, 2);
            AddSystem(ref m_ClientTick, typeB, 3);
            AddSystem(ref m_ServerTick, typeA, 1);
            AddSystem(ref m_ServerTick, typeB, 2);

            m_ClientTick.ProcessSystemOrderDiff(m_ServerTick);

            Assert.That(m_ClientTick.DiffInfo.HasDiff, Is.False);
            Assert.That(m_ClientTick.DiffInfo.DiffReasonFlags, Is.EqualTo(DiffInfo.DiffReasons.Undefined));
        }
    }
}
