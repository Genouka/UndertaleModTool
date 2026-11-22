using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UndertaleModRunner;

namespace UndertaleModTests
{
    /// <summary>
    /// End-to-end checks for the out-of-process Butterscotch debugger. These
    /// run a real host executable and a real data.win from the Butterscotch
    /// test suite, so they prove cross-bitness control and crash containment
    /// instead of just exercising a mock protocol.
    /// </summary>
    [TestClass]
    public class ButterscotchHostTests
    {
        private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));

        private static string TestData => Path.Combine(
            RepositoryRoot, "Butterscotch", "tests", "lives-and-health-events-check", "data.win");

        [TestMethod]
        [Timeout(120000)]
        public void X64Host_DebugSession()
        {
            RunDebugSession(ButterscotchArchitecture.X64);
        }

        [TestMethod]
        [Timeout(120000)]
        public void X86Host_DebugSessionFromCurrentProcess()
        {
            // The whole point of the host: a 64-bit client controls a 32-bit debugger.
            RunDebugSession(ButterscotchArchitecture.X86);
        }

        [TestMethod]
        [Timeout(120000)]
        public void HostCrash_DoesNotTakeDownClient()
        {
            if (!TryCreateSession(ButterscotchArchitecture.X64, out RemoteButterscotchSession session,
                                  out string reason))
            {
                Assert.Inconclusive(reason);
            }

            using (session)
            {
                var crashed = new ManualResetEventSlim(false);
                string errorMessage = null;
                session.Error += message =>
                {
                    errorMessage = message;
                    crashed.Set();
                };

                Assert.IsTrue(session.Start(), "The host failed to start.");
                try
                {
                    Assert.IsTrue(session.HostProcessId > 0, "The host PID was not exposed.");
                    Process host = Process.GetProcessById(session.HostProcessId);
                    host.Kill(entireProcessTree: true);
                    host.Dispose();
                }
                catch (ArgumentException)
                {
                    Assert.Inconclusive("The host exited before it could be killed.");
                }

                Assert.IsTrue(crashed.Wait(TimeSpan.FromSeconds(15)),
                    "The client did not notice that the host died.");
                StringAssert.Contains(errorMessage ?? "", "exited unexpectedly");
                Assert.IsFalse(session.IsRunning, "The session remained active after a host crash.");
                // Reaching these assertions is the proof: the test process (the
                // UTMT side) is still alive after the native process died.
            }
        }

        private static void RunDebugSession(ButterscotchArchitecture architecture)
        {
            if (!TryCreateSession(architecture, out RemoteButterscotchSession session, out string reason))
            {
                Assert.Inconclusive(reason);
            }

            using (session)
            {
                var stopped = new ManualResetEventSlim(false);
                DebugState stopState = null;
                session.Stopped += state =>
                {
                    stopState = state;
                    stopped.Set();
                };
                session.Error += message => Assert.Fail("Debug host error: " + message);

                Assert.IsTrue(session.Start(), "The host failed to start.");
                Assert.IsTrue(stopped.Wait(TimeSpan.FromSeconds(45)),
                    "The runner never stopped at the entry breakpoint.");
                Assert.IsNotNull(stopState);
                Assert.IsTrue(stopState.Paused, "The reported stop state was not paused.");

                session.RefreshInspectionAsync().GetAwaiter().GetResult();
                Assert.IsTrue(session.GetCallStack().Frames.Count > 0,
                    "No call stack was available while paused.");

                Assert.IsTrue(session.Resume(), "Resume failed while paused.");
            }
        }

        private static bool TryCreateSession(ButterscotchArchitecture architecture,
                                             out RemoteButterscotchSession session, out string reason)
        {
            session = null;
            reason = null;
            if (!OperatingSystem.IsWindows())
            {
                reason = "The native debug host is only built for Windows.";
                return false;
            }
            if (!File.Exists(TestData))
            {
                reason = $"Test data not found: {TestData}";
                return false;
            }

            string folder = ButterscotchHost.GetNativeFolder(architecture);
            string host = Path.Combine(RepositoryRoot, "UndertaleModRunner", "native",
                                       folder, "bsdbg-host.exe");
            if (!ButterscotchHost.TryResolveExecutable(architecture, host, out string hostPath, out string error))
            {
                reason = error;
                return false;
            }

            var config = new ButterscotchConfig
            {
                DataWinPath = TestData,
                WorkingDirectory = Path.GetDirectoryName(TestData),
                Renderer = BsdbgRenderer.ModernGL,
                Headless = true,
                StopOnEntry = true,
            };
            session = new RemoteButterscotchSession(config, architecture, hostPath);
            return true;
        }
    }
}
