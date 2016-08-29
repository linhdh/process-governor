using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using VsChromium.Core.Win32;
using WinHandles = VsChromium.Core.Win32.Handles;
using WinProcesses = VsChromium.Core.Win32.Processes;
using WinJobs = LowLevelDesign.Win32.Jobs;
using WinInterop = VsChromium.Core.Win32.Interop;

namespace LowLevelDesign
{
    public class ProcessGovernor : IDisposable
    {
        private uint maxProcessMemory;
        private long cpuAffinityMask;
        private readonly Dictionary<string, string> additionalEnvironmentVars = new Dictionary<string, string>();
        private Thread listener;

        private WinProcesses.SafeProcessHandle hProcess;
        private IntPtr hIOCP, hJob;

        public void AttachToProcess(int pid)
        {
            hProcess = CheckResult(WinProcesses.NativeMethods.OpenProcess(WinProcesses.ProcessAccessFlags.All, false, pid));

            AssignProcessToJobObject();

            WaitForTheChildProcess();
        }

        public void StartProcess(IList<string> procargs)
        {
            WinProcesses.PROCESS_INFORMATION pi = new WinProcesses.PROCESS_INFORMATION();
            WinProcesses.STARTUPINFO si = new WinProcesses.STARTUPINFO();

            CheckResult(WinProcesses.NativeMethods.CreateProcess(null, new StringBuilder(string.Join(" ", procargs)), null, null, false,
                        WinProcesses.ProcessCreationFlags.CREATE_SUSPENDED | WinProcesses.ProcessCreationFlags.CREATE_NEW_CONSOLE,
                        GetEnvironmentString(), null, si, pi));

            hProcess = new WinProcesses.SafeProcessHandle(pi.hProcess);

            AssignProcessToJobObject();

            // resume process main thread
            CheckResult(WinProcesses.NativeMethods.ResumeThread(pi.hThread));
            // and we can close the thread handle
            CloseHandle(pi.hThread);

            WaitForTheChildProcess();
        }

        private StringBuilder GetEnvironmentString()
        {
            if (additionalEnvironmentVars.Count == 0) {
                return null;
            }

            StringBuilder envEntries = new StringBuilder();
            foreach (string env in Environment.GetEnvironmentVariables().Keys) {
                if (additionalEnvironmentVars.ContainsKey(env)) {
                    continue; // overwrite existing env
                }
                envEntries.Append(env).Append("=").Append(
                    Environment.GetEnvironmentVariable(env)).Append("\0");
            }
            foreach (var kv in additionalEnvironmentVars) {
                envEntries.Append(kv.Key).Append("=").Append(
                    kv.Value).Append("\0");
            }
            envEntries.Append("\0");

            return envEntries;
        }

        void AssignProcessToJobObject()
        {
                var securityAttributes = new WinInterop.SecurityAttributes();
                securityAttributes.nLength = Marshal.SizeOf(securityAttributes);

                hJob = CheckResult(WinJobs.NativeMethods.CreateJobObject(securityAttributes, "procgov-" + Guid.NewGuid()));

                // create completion port
                hIOCP = CheckResult(WinJobs.NativeMethods.CreateIoCompletionPort(WinHandles.NativeMethods.INVALID_HANDLE_VALUE, IntPtr.Zero, IntPtr.Zero, 1));
                var assocInfo = new WinJobs.JOBOBJECT_ASSOCIATE_COMPLETION_PORT {
                    CompletionKey = IntPtr.Zero,
                    CompletionPort = hIOCP
                };
                uint size = (uint)Marshal.SizeOf(assocInfo);
                CheckResult(WinJobs.NativeMethods.SetInformationJobObject(hJob, WinJobs.JOBOBJECTINFOCLASS.AssociateCompletionPortInformation,
                        ref assocInfo, size));

                // start listening thread
                listener = new Thread(CompletionPortListener);
                listener.Start(hIOCP);

                WinJobs.JobInformationLimitFlags flags = WinJobs.JobInformationLimitFlags.JOB_OBJECT_LIMIT_BREAKAWAY_OK
                                        | WinJobs.JobInformationLimitFlags.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
                if (maxProcessMemory > 0) {
                    flags |= WinJobs.JobInformationLimitFlags.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
                }
                if (cpuAffinityMask != 0) {
                    flags |= WinJobs.JobInformationLimitFlags.JOB_OBJECT_LIMIT_AFFINITY;
                }

                long systemAffinity, processAffinity;
                CheckResult(WinProcesses.NativeMethods.GetProcessAffinityMask(hProcess, out processAffinity, out systemAffinity));

                // configure constraints
                var limitInfo = new WinJobs.JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
                    BasicLimitInformation = new WinJobs.JOBOBJECT_BASIC_LIMIT_INFORMATION {
                        LimitFlags = flags,
                        Affinity = systemAffinity & cpuAffinityMask
                    },
                    ProcessMemoryLimit = maxProcessMemory
                };
                size = (uint)Marshal.SizeOf(limitInfo);
                CheckResult(WinJobs.NativeMethods.SetInformationJobObject(hJob, WinJobs.JOBOBJECTINFOCLASS.ExtendedLimitInformation,
                        ref limitInfo, size));

                // assign a process to a job to apply constraints
                CheckResult(WinJobs.NativeMethods.AssignProcessToJobObject(hJob, hProcess.DangerousGetHandle()));
        }

        void WaitForTheChildProcess()
        {

            if (WinHandles.NativeMethods.WaitForSingleObject(hProcess, Constants.INFINITE) == 0xFFFFFFFF) {
                throw new Win32Exception();
            }

            if (hIOCP != IntPtr.Zero) {
                CloseHandle(hIOCP);
            }
            if (listener.IsAlive) {
                if (!listener.Join(TimeSpan.FromMilliseconds(500))) {
                    listener.Abort();
                }
            }
        }

        void CompletionPortListener(object o)
        {
            uint msgIdentifier;
            IntPtr pCompletionKey, lpOverlapped;

            while (WinJobs.NativeMethods.GetQueuedCompletionStatus(hIOCP, out msgIdentifier, out pCompletionKey,
                        out lpOverlapped, Constants.INFINITE)) {
                if (msgIdentifier == (uint)WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_NEW_PROCESS) {
                    Trace.TraceInformation("{0}: process {1} has started", msgIdentifier, (int)lpOverlapped);
                } else if (msgIdentifier == (uint)WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_EXIT_PROCESS ||
                    msgIdentifier == (uint)WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS) {
                    Trace.TraceInformation("{0}: process {1} exited", msgIdentifier, (int)lpOverlapped);
                } else if (msgIdentifier == (uint)WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO) {
                    // nothing
                } else if (msgIdentifier == (uint)WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT) {
                    Trace.TraceInformation("{0}: process {1} exceeded its memory limit", msgIdentifier, (int)lpOverlapped);
                } else {
                    Trace.TraceInformation("Unknown message: {0}", msgIdentifier);
                }
            }
        }

        public uint MaxProcessMemory
        {
            get { return maxProcessMemory; }
            set { maxProcessMemory = value; }
        }

        public long CpuAffinityMask
        {
            get { return cpuAffinityMask; }
            set { cpuAffinityMask = value; }
        }

        public Dictionary<string, string> AdditionalEnvironmentVars
        {
            get { return additionalEnvironmentVars; }
        }

        public void Dispose(bool disposing)
        {
            if (disposing) {
                if (hProcess != null && !hProcess.IsClosed) {
                    hProcess.Close();
                }
            }

            if (hJob != IntPtr.Zero) {
                CloseHandle(hJob);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ProcessGovernor()
        {
            Dispose(false);
        }

        /* Win32 API helper methods */

        private static void CloseHandle(IntPtr handle)
        {
            if (handle != IntPtr.Zero) {
                WinHandles.NativeMethods.CloseHandle(handle);
            }
        }

        private static void CheckResult(bool result)
        {
            if (!result) {
                throw new Win32Exception();
            }
        }

        private static IntPtr CheckResult(IntPtr result)
        {
            if (result == IntPtr.Zero) {
                throw new Win32Exception();
            }
            return result;
        }

        private static T CheckResult<T>(T handle) where T : SafeHandle
        {
            if (handle.IsInvalid) {
                throw new Win32Exception();
            }
            return handle;
        }

        private static void CheckResult(int result)
        {
            if (result == -1) {
                throw new Win32Exception();
            }
        }
    }
}
