using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TrafficVista.Business;

/// <summary>Start suspended, attach to a kill-on-close job, then resume: every Chrome descendant stays owned.</summary>
internal sealed class ChromeProcessJob : IDisposable
{
    private static readonly object RegistrySync = new();
    private static readonly HashSet<ChromeProcessJob> Registry = new();
    internal static int OwnedProcessCount
    {
        get { lock (RegistrySync) return checked((int)Registry.Sum(job => (long)job.ActiveProcesses)); }
    }
    private readonly SafeFileHandle _job;
    internal Process Process { get; }
    internal ulong PeakMemoryBytes => QueryInformationJobObject(_job, 9, out ExtendedLimits limits,
        (uint)Marshal.SizeOf<ExtendedLimits>(), 0) ? limits.PeakJobMemoryUsed.ToUInt64() : 0;
    internal uint ActiveProcesses => QueryInformationJobObject(_job, 1, out Accounting accounting,
        (uint)Marshal.SizeOf<Accounting>(), 0) ? accounting.ActiveProcesses : throw new Win32Exception(Marshal.GetLastWin32Error());

    private ChromeProcessJob(SafeFileHandle job, Process process)
    { _job = job; Process = process; lock (RegistrySync) Registry.Add(this); }

    internal static ChromeProcessJob Start(ProcessStartInfo info)
    {
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        ProcessInformation created = default;
        try
        {
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder(string.Join(" ", new[] { info.FileName }.Concat(info.ArgumentList).Select(Quote)));
            if (!CreateProcessW(info.FileName, command, 0, 0, false, 0x4 | 0x08000000, 0,
                    Path.GetDirectoryName(info.FileName), ref startup, out created))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!AssignProcessToJobObject(job, created.Process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var process = Process.GetProcessById((int)created.ProcessId);
            if (ResumeThread(created.Thread) == uint.MaxValue) { process.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return new ChromeProcessJob(job, process);
        }
        catch
        {
            if (created.Process != 0) TerminateProcess(created.Process, 1);
            job.Dispose(); throw;
        }
        finally
        {
            if (created.Thread != 0) CloseHandle(created.Thread);
            if (created.Process != 0) CloseHandle(created.Process);
        }
    }

    internal void Kill() { if (!_job.IsClosed) TerminateJobObject(_job, 1); }
    public void Dispose()
    { lock (RegistrySync) { Registry.Remove(this); _job.Dispose(); Process.Dispose(); } }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') result.Append('\\', slashes * 2 + 1).Append(ch);
            else result.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

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
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public nint ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
    { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out ExtendedLimits info, uint length, nint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting info, uint length, nint returnLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment,
        string? currentDirectory, ref StartupInfo startup, out ProcessInformation created);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(nint process, uint exitCode);
}
