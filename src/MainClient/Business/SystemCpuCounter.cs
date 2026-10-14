using System.Runtime.InteropServices;

namespace TrafficVista.Business;

/// <summary>Processor Information total includes all processor groups; English counter path is locale independent.</summary>
internal sealed class SystemCpuCounter : IDisposable
{
    private nint _query, _counter;
    private bool _primed;
    internal double? Read()
    {
        if (_query == 0)
        {
            Check(PdhOpenQueryW(null, 0, out _query));
            try { Check(PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Time", 0, out _counter)); }
            catch { Dispose(); throw; }
        }
        Check(PdhCollectQueryData(_query));
        if (!_primed) { _primed = true; return null; }
        Check(PdhGetFormattedCounterValue(_counter, 0x200, 0, out var value));
        if (value.Status > 1 || !double.IsFinite(value.Value)) throw new InvalidOperationException("CPU计数器数据暂不可用。");
        return Math.Clamp(value.Value, 0, 100);
    }
    private static void Check(uint status)
    { if (status != 0) throw new InvalidOperationException($"系统CPU性能计数器失败：0x{status:X8}。"); }
    public void Dispose()
    { if (_query != 0) { PdhCloseQuery(_query); _query = _counter = 0; _primed = false; } }
    [StructLayout(LayoutKind.Sequential)] private struct CounterValue { public uint Status; public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, nuint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint user, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, nint type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
