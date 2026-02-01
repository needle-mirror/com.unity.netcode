using System;
using NUnit.Framework;

namespace Unity.NetCode.Tests
{
    internal class NetworkTickUnitTests
    {
        [Test]
        public void NetworkTick_ComparisonOperators_WorksAsExpected()
        {
            var tick1 = new NetworkTick(100);
            var tick2 = new NetworkTick(100);
            var tick3 = new NetworkTick(200);

            Assert.IsTrue(tick1 == tick2);
            Assert.IsFalse(tick1 != tick2);
            Assert.IsFalse(tick1 == tick3);
            Assert.IsTrue(tick1 != tick3);

            Assert.IsTrue(tick1.IsOlderThan(tick3));
            Assert.IsFalse(tick3.IsOlderThan(tick1));
            Assert.IsTrue(tick3.IsNewerThan(tick1));
            Assert.IsFalse(tick1.IsNewerThan(tick3));
        }

        [Test]
        public void NetworkTick_InvalidTick_BehavesAsExpected()
        {
            var invalidTick = NetworkTick.Invalid;
            var validTick = new NetworkTick(100);

            Assert.IsFalse(invalidTick.IsValid);
            Assert.IsTrue(validTick.IsValid);
            Assert.IsFalse(invalidTick == validTick);
            Assert.IsTrue(invalidTick != validTick);

            Assert.Throws<InvalidOperationException>(() => invalidTick.IsOlderThan(validTick));
            Assert.Throws<InvalidOperationException>(() => invalidTick.IsNewerThan(validTick));
        }

        [Test]
        public void NetworkTick_Arithmetic_WorksAsExpected()
        {
            var tick = new NetworkTick(10);
            tick.Add(5);
            Assert.AreEqual(new NetworkTick(15), tick);
            tick.Subtract(3);
            Assert.AreEqual(new NetworkTick(12), tick);
            tick.Increment();
            Assert.AreEqual(new NetworkTick(13), tick);
            tick.Decrement();
            Assert.AreEqual(new NetworkTick(12), tick);
        }

        [Test]
        public void NetworkTick_TicksSince_WorksAsExpected()
        {
            var tick1 = new NetworkTick(100);
            var tick2 = new NetworkTick(110);
            Assert.AreEqual(10, tick2.TicksSince(tick1));
            Assert.AreEqual(-10, tick1.TicksSince(tick2));
            Assert.AreEqual(0, tick1.TicksSince(tick1));
        }

        [Test]
        public void NetworkTick_SerializedData_WorksAsExpected()
        {
            var tick = new NetworkTick(1234);
            uint data = tick.SerializedData;
            var tick2 = NetworkTick.Invalid;
            tick2.SerializedData = data;
            Assert.AreEqual(tick, tick2);
        }

        [Test]
        public void NetworkTick_ToString_And_ToFixedString_Works()
        {
            var tick = new NetworkTick(42);
            Assert.AreEqual("42", tick.ToString());
            Assert.AreEqual("42", tick.ToFixedString().ToString());
            var invalid = NetworkTick.Invalid;
            Assert.AreEqual("Invalid", invalid.ToString());
            Assert.AreEqual("Invalid", invalid.ToFixedString().ToString());
        }

        [Test]
        public void NetworkTick_TickIndexForValidTick_WorksAndThrows()
        {
            var tick = new NetworkTick(77);
            Assert.AreEqual(77u, tick.TickIndexForValidTick);
            var invalid = NetworkTick.Invalid;
            Assert.Throws<InvalidOperationException>(() => { var _ = invalid.TickIndexForValidTick; });
        }

        [Test]
        public void NetworkTick_GetHashCode_And_Equals_Work()
        {
            var tick1 = new NetworkTick(5);
            var tick2 = new NetworkTick(5);
            var tick3 = new NetworkTick(6);
            Assert.AreEqual(tick1.GetHashCode(), tick2.GetHashCode());
            Assert.IsTrue(tick1.Equals(tick2));
            Assert.IsFalse(tick1.Equals(tick3));
            Assert.IsFalse(tick1.Equals(null));
        }

        [Test]
        public void NetworkTick_EdgeCases_BoundariesAndWrap()
        {
            var minTick = new NetworkTick(0);
            var maxTick = new NetworkTick(uint.MaxValue >> 1);
            Assert.IsTrue(minTick.IsValid);
            Assert.IsTrue(maxTick.IsValid);
            // Wrap-around increment
            var wrapTick = new NetworkTick(uint.MaxValue >> 1);
            wrapTick.Increment();
            Assert.AreEqual(new NetworkTick(0), wrapTick);
        }
    }
}
