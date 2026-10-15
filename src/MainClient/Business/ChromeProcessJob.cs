using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrafficVista.Business;

/// <summary>Owns a Playwright-launched Chrome and its descendants in a kill-on-close job.</summary>
internal sealed class ChromeProcessJob : IDisposable
{
    private static readonly object RegistrySync = new();
    private static readonly HashSet<ChromeProcessJob> Registry = new();
    internal static int OwnedProcessCount
    {
        get { lock (RegistrySync) return checked((int)Registry.Sum(job => (long)job.ActiveProcesses)); }
    }
    private readonly SafeFileHandle _job;
    private readonly List<SafeFileHandle> _startupJobs = [];
    internal Process Process { get; }
    // Sum of job peaks is a conservative upper bound when startup children need separate nested jobs.
    internal ulong PeakMemoryBytes => AllJobs.Aggregate(0UL, (sum, job) => sum +
        (QueryInformationJobObject(job, 9, out ExtendedLimits limits,
        (uint)Marshal.SizeOf<ExtendedLimits>(), 0) ? limits.PeakJobMemoryUsed.ToUInt64() : 0));
    private SafeFileHandle[] AllJobs
    {
        get { lock (RegistrySync) return new[] { _job }.Concat(_startupJobs).ToArray(); }
    }
    internal uint ActiveProcesses => AllJobs.Aggregate(0U, (sum, job) => sum +
        (QueryInformationJobObject(job, 1, out Accounting accounting,
        (uint)Marshal.SizeOf<Accounting>(), 0) ? accounting.ActiveProcesses : throw new Win32Exception(Marshal.GetLastWin32Error())));

    private ChromeProcessJob(SafeFileHandle job, Process process)
    { _job = job; Process = process; lock (RegistrySync) Registry.Add(this); }

    internal static ChromeProcessJob Attach(int browserProcessId)
    {
        var process = Process.GetProcessById(browserProcessId);
        var job = CreateJobObjectW(0, null);
        try
        {
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                || !AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new ChromeProcessJob(job, process);
        }
        catch { job.Dispose(); process.Dispose(); throw; }
    }

    // Include startup helpers omitted by CDP (for example crash handlers).
    // Only descendants of the browser PID are selected, never processes matched by name.
    internal void AttachExistingProcesses()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var parents = new Dictionary<int, int>();
        if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do { parents[(int)entry.Id] = (int)entry.ParentId; } while (Process32NextW(snapshot, ref entry));
        var processIds = new List<int> { Process.Id };
        for (var index = 0; index < processIds.Count; index++)
            processIds.AddRange(parents.Where(p => p.Value == processIds[index] && !processIds.Contains(p.Key)).Select(p => p.Key));
        foreach (var id in processIds)
        {
            if (id == Process.Id) continue;
            try
            {
                using var child = Process.GetProcessById(id);
                // A snapshot can contain an older process whose recorded parent PID has
                // since been reused by this browser. It is not ours; neither attach nor kill it.
                if (child.StartTime < Process.StartTime) continue;
                var alreadyOwned = false;
                foreach (var ownedJob in AllJobs)
                {
                    if (!IsProcessInJob(child.Handle, ownedJob, out alreadyOwned))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (alreadyOwned) break;
                }
                if (!alreadyOwned && !AssignProcessToJobObject(_job, child.Handle))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (child.HasExited) continue;
                    if (error != 5) throw new Win32Exception(error);
                    // Chromium sandbox jobs created before attachment can have an incompatible
                    // hierarchy with the browser job. Give these processes their own nested job.
                    var nested = CreateJobObjectW(0, null);
                    try
                    {
                        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
                        if (nested.IsInvalid || !SetInformationJobObject(nested, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                            || !AssignProcessToJobObject(nested, child.Handle))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        lock (RegistrySync) _startupJobs.Add(nested);
                    }
                    catch { nested.Dispose(); throw; }
                }
            }
            catch (ArgumentException) { } // The reported process has already exited.
        }
    }

    internal void Kill() { foreach (var job in AllJobs) if (!job.IsClosed) TerminateJobObject(job, 1); }
    public void Dispose()
    { lock (RegistrySync) { Registry.Remove(this); foreach (var job in AllJobs) job.Dispose(); Process.Dispose(); } }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        public uint TotalPageFaults, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(nint process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out ExtendedLimits info, uint length, nint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting info, uint length, nint returnLength);
}
