using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.NetCode.Generators;
using Debug = Unity.NetCode.Generators.Debug;

namespace Unity.NetCode.GeneratorTests
{
    /// <summary>
    /// Provides the rough functionality of the Unity TestRunner LogAssert class.
    /// </summary>
    /// <remarks>
    /// Set up the LogAssert tracking by calling <see cref="StartLogValidation"/>.
    /// Validate expected logs using <see cref="ValidateAllLogsWereHandled"/>.
    /// Setup and validation is done automatically when extending from <see cref="BaseTest"/>.
    /// </remarks>
    internal static class LogAssert
    {
        private static readonly Queue<(LogType, Regex)> k_ExpectedLogs = new();
        static readonly List<string> k_UnexpectedLogMessages = [];

        /// <summary>
        /// Set this to `true` to prevent unexpected error log messages from triggering an assertion.
        /// </summary>
        public static bool IgnoreFailingLogs;

        internal static void StartLogValidation()
        {
            ResetDebugLogger();
            Debug.s_OnLogAction += HandleTestLog;
        }

        internal static void ResetDebugLogger()
        {
            Debug.s_OnLogAction -= HandleTestLog;
            k_ExpectedLogs.Clear();
            k_UnexpectedLogMessages.Clear();
            IgnoreFailingLogs = false;

        }

        /// <summary>
        /// Checks whether this new log is the next expected log.
        /// If there's an issue, adds a result to <see cref="k_UnexpectedLogMessages"/>.
        /// </summary>
        /// <remarks>
        /// It's important to not fail the test inside this function.
        /// Failing the test while running the generators breaks the test framework.
        /// </remarks>
        private static void HandleTestLog(LogType level, string message)
        {
            var expectedNextString = "";
            var hasExpected = k_ExpectedLogs.TryPeek(out var next);
            if (hasExpected)
            {
                var (expectedLevel, expectedRegex) = next;
                if (level == expectedLevel && expectedRegex.IsMatch(message))
                {
                    // Remove from queue if this log was the next expected log
                    k_ExpectedLogs.Dequeue();
                    return;
                }

                expectedNextString = $". Expected next message: Level: {expectedLevel}, Regex: {expectedRegex}";
            }

            // We have to add logs to a list to be checked later
            // Failing here will break the test rather than report a coherent failure.
            if (level is LogType.Error or LogType.Exception && !IgnoreFailingLogs)
            {
                k_UnexpectedLogMessages.Add($"{message}{expectedNextString}");
            }
        }

        /// <summary>
        /// Verifies that a log message of a specified type appears in the log.
        /// A test won't fail from an expected error, assertion, or exception log message.
        /// A test will fail if an expected message does not appear in the log.
        /// </summary>
        /// <param name="type">A type of log to expect. It can take one of the <see cref="LogType"/> values.</param>
        /// <param name="message">A regular expression pattern to match the expected message.</param>
        public static void Expect(LogType type, Regex message)
        {
            k_ExpectedLogs.Enqueue((type, message));
        }

        public static void ValidateAllLogsWereHandled()
        {
            if (k_UnexpectedLogMessages.Count > 0)
            {
                Assert.Fail($"Unexpected error log: {k_UnexpectedLogMessages[0]}. Use GeneratorTests.LogAssert.Expect");
            }

            if (k_ExpectedLogs.Count > 0)
            {
                var (level, expectedMessage) = k_ExpectedLogs.Dequeue();
                var nextExpected = $"[{level}] Regex: {expectedMessage}";

                Assert.Fail($"Expected log was never received: {nextExpected}");
            }
        }

    }
}
