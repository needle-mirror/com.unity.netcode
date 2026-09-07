using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Netcode.NetcodeTime;
using Unity.Transforms;
using UnityEngine;

namespace Unity.Netcode.Tests
{
    internal class SingleWorldHostSharedTest
    {
        // shared validation reusable by both GO and ECS tests
        public static async Task ValidateHostInterpolation(NetCodeTestWorld testWorld, SingleWorldHostInterpolationMode mode, Func<LocalTransform> getLocal, Func<LocalTransform> getWorld)
        {
            List<(NetworkTick ServerTick, float InterpolationTickFraction, bool IsOffFrame,
                float3 SimulationPosition, float3 InterpolatedPosition)> expectedValues = new()
            {
                (new NetworkTick(2), 0.0f, false, new float3(0.0f), new float3(0.0f)),
                (new NetworkTick(2), 1f/3f, true, new float3(0.0f), new float3(0.0f)),
                (new NetworkTick(2), 2f/3f, true, new float3(0.0f), new float3(0.0f)),
                (new NetworkTick(3), 0.0f, false, new float3(0.05f), new float3(3.72529E-09f)),
                (new NetworkTick(3), 1f/3f, true, new float3(0.05f), new float3(0.01666667f)),
                (new NetworkTick(3), 2f/3f, true, new float3(0.05f), new float3(0.03333334f)),
                (new NetworkTick(4), 0.0f, false, new float3(0.1f), new float3(0.05f)),
                (new NetworkTick(4), 1f/3f, true, new float3(0.1f), new float3(0.06666667f)),
                (new NetworkTick(4), 2f/3f, true, new float3(0.1f), new float3(0.08333334f)),
                (new NetworkTick(5), 0.0f, false, new float3(0.15f), new float3(0.1f)),
                (default,                 0.0f, true,  default,            default), // See note below on IsOffFrame
            };

            var isInterpolating = mode == SingleWorldHostInterpolationMode.Interpolate;
            for (int i = 0; i < 10; i++)
            {
                string context = $"test iteration {i}";

                var networkTime = testWorld.GetSingleton<NetworkTime>(testWorld.ServerWorld);
                var localTransform = getLocal();
                var l2w = getWorld();
                //Debug.Log($"\t[{i}] {mode} | {networkTime}\n\t trans:{localTransform.Position} ({math.EulerXYZ(localTransform.Rotation)}) l2w:{l2w.Position} ({math.EulerXYZ(l2w.Rotation)})");

                var expected = expectedValues[i];
                Assert.That(networkTime.ServerTick, Is.EqualTo(expected.ServerTick), context);

                bool expectedOffFrame;
                // Note on IsOffFrame: playmode tests yield in the middle of the frame, between initialization and simulation, which means IsOffFrame is actually the one for the next frame.
                // SamB: Spent a few hours trying to make unity yield in EndOfFrameAsync, looks like so far this doesn't work if there's no rendering. So using this compromise instead :(
                if (testWorld.m_WorldStrategy is PlayModeTestWorldStrategy)
                    expectedOffFrame = expectedValues[i + 1].IsOffFrame;
                else
                    expectedOffFrame = expected.IsOffFrame;

                Assert.That(networkTime.IsOffFrame, Is.EqualTo(expectedOffFrame), context);
                var expectedInterpolationTick = networkTime.ServerTick;
                expectedInterpolationTick.Subtract(1u);
                Assert.That(networkTime.InterpolationTick, Is.EqualTo(expectedInterpolationTick), context);
                Assert.That(networkTime.InterpolationTickFraction, Is.EqualTo(expected.InterpolationTickFraction).Within(0.001f), context);
                var expectedInputTargetTick = networkTime.ServerTick;
                expectedInputTargetTick.Add(expected.IsOffFrame ? 1u : 0u);
                Assert.That(networkTime.EffectiveInputLatencyTicks, Is.EqualTo(expected.IsOffFrame ? 1u : 0u), context);
                Assert.That(networkTime.InputTargetTick, Is.EqualTo(expectedInputTargetTick), context);
                Assert.That(networkTime.NumPredictedTicksExpected, Is.EqualTo(expected.IsOffFrame ? 0 : 1), context);
                Assert.That(networkTime.PredictedTickIndex, Is.EqualTo(expected.IsOffFrame ? 0 : 1), context);

                // Interpolated value assertions:
                Assert.That(math.distance(localTransform.Position, expected.SimulationPosition), Is.EqualTo(0).Within(0.001f), context);
                Assert.That(math.distance(l2w.Position, isInterpolating ? expected.InterpolatedPosition : expected.SimulationPosition), Is.EqualTo(0).Within(0.001f), context);

                var simulatedRotation = Mathematics.quaternion.Euler(0, expected.SimulationPosition.x, 0);
                var interpolatedRotation = Mathematics.quaternion.Euler(0, expected.InterpolatedPosition.x, 0);
                Assert.That(math.angle(localTransform.Rotation, simulatedRotation) * math.TODEGREES, Is.EqualTo(0).Within(10f), context); // Because of slerp.
                Assert.That(math.angle(l2w.Rotation, isInterpolating ? interpolatedRotation : simulatedRotation) * math.TODEGREES, Is.EqualTo(0).Within(10f), context);

                await testWorld.TickAsync();
            }
        }
    }
}
