using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrafficVista.Business;

internal sealed record SystemPressureSample(bool Available, double CpuPercent, ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes, ulong AvailableCommitBytes)
{
    internal string? UnavailableReason { get; init; }
}

/// <summary>Shared instance samples once per second in the background; UI and admission read the same immutable cache.</summary>
internal sealed class SystemPressureProbe : IDisposable
{
    private static readonly Lazy<SystemPressureProbe> SharedInstance = new(CreateShared);
    internal static SystemPressureProbe Shared => SharedInstance.Value;
    internal static int LogicalProcessorCount => checked((int)GetActiveProcessorCount(0xffff));
    private readonly object _sync = new();
    private long _sampled;
    private ulong _idle, _total;
    private double _cpu;
    private SystemPressureSample _last = new(false, 0, 0, 0, 0)
        { UnavailableReason = "正在采样系统 CPU，等待新任务准入" };
    private readonly SystemCpuCounter? _counter;
    private readonly CancellationTokenSource _stop = new();
    private Task? _background;
    private bool _disposed;
    internal SystemPressureSample Latest => Volatile.Read(ref _last);

    internal SystemPressureProbe(bool forceCounter = false)
    { if (forceCounter || GetActiveProcessorGroupCount() > 1) _counter = new(); }

    private static SystemPressureProbe CreateShared()
    {
        var probe = new SystemPressureProbe();
        probe._background = Task.Run(async () =>
        {
            try
            {
                while (!probe._stop.IsCancellationRequested)
                { probe.Read(); await Task.Delay(1000, probe._stop.Token).ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (probe._stop.IsCancellationRequested) { }
        });
        return probe;
    }
    internal static void StopShared() { if (SharedInstance.IsValueCreated) SharedInstance.Value.Dispose(); }
    private SystemPressureSample Publish(SystemPressureSample sample) { Volatile.Write(ref _last, sample); return sample; }

    internal SystemPressureSample Read()
    {
        lock (_sync)
        {
            if (_disposed) return _last;
            if (_sampled != 0 && Stopwatch.GetElapsedTime(_sampled) < TimeSpan.FromSeconds(1)) return _last;
            _sampled = Stopwatch.GetTimestamp();
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory))
                return Publish(new(false, 0, 0, 0, 0) { UnavailableReason = "系统内存采样失败。" });
            if (_counter is not null)
            {
                try
                {
                    var measured = _counter.Read();
                    if (measured is null) return Publish(new(false, 0, memory.TotalPhysical, memory.AvailablePhysical, memory.AvailablePageFile)
                        { UnavailableReason = "正在采样系统 CPU，等待新任务准入" });
                    _cpu = _last.Available ? .6 * _cpu + .4 * measured.Value : measured.Value;
                    return Publish(new(true, _cpu, memory.TotalPhysical, memory.AvailablePhysical, memory.AvailablePageFile));
                }
                catch (Exception ex)
                { return Publish(new(false, 0, memory.TotalPhysical, memory.AvailablePhysical, memory.AvailablePageFile) { UnavailableReason = ex.Message }); }
            }
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return Publish(new(false, 0, memory.TotalPhysical, memory.AvailablePhysical, memory.AvailablePageFile)
                    { UnavailableReason = "系统CPU采样失败。" });
            var total = kernel + user;
            var first = _total == 0;
            if (_total != 0 && total > _total && idle >= _idle)
            {
                var current = Math.Clamp(100.0 * (1 - (double)(idle - _idle) / (total - _total)), 0, 100);
                _cpu = _last.Available ? .6 * _cpu + .4 * current : current;
            }
            _idle = idle; _total = total;
            if (first)
                return Publish(new(false, 0, memory.TotalPhysical, memory.AvailablePhysical, memory.AvailablePageFile)
                    { UnavailableReason = "正在采样系统 CPU，等待新任务准入" });
            return Publish(new(true, _cpu, memory.TotalPhysical, memory.AvailablePhysical, memory.AvailablePageFile));
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _stop.Cancel(); _counter?.Dispose();
            Publish(new(false, 0, 0, 0, 0) { UnavailableReason = "系统资源采样已停止。" });
        }
        if (_background is null) _stop.Dispose();
        else _ = _background.ContinueWith(task => { _ = task.Exception; _stop.Dispose(); }, TaskScheduler.Default);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
}
