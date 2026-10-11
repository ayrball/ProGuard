using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using ProGuard.Core.Native;

namespace ProGuard.Core.Helpers;

/// <summary>One reading of <see cref="ProcessorCounters"/>. A null (or empty) part could not be read.</summary>
/// <param name="UtilityPercent"><c>\Processor Information(_Total)\% Processor Utility</c>, uncapped: Task Manager's
/// CPU figure before it caps at 100.</param>
/// <param name="PerCorePercent"><c>\Processor Information(g,n)\% Processor Time</c> for each logical CPU, in group
/// and index order.</param>
/// <param name="QueueLength"><c>\System\Processor Queue Length</c>: threads ready to run but waiting for a CPU.</param>
public sealed record ProcessorSample(double? UtilityPercent, IReadOnlyList<double> PerCorePercent, double? QueueLength)
{
    public static readonly ProcessorSample Empty = new(null, Array.Empty<double>(), null);
}

/// <summary>
/// The PDH counters Process Lasso's GUI (and Task Manager) use for CPU load, in one query so a single collection
/// reads them all. Rate counters need two collections, so the first <see cref="Collect"/> has no CPU figures.
/// Each counter is optional: one that can't be added (an old Windows without <c>% Processor Utility</c>, disabled
/// performance counters) just reads as null.
/// </summary>
public sealed class ProcessorCounters : IDisposable
{
    internal const string UtilityPath = @"\Processor Information(_Total)\% Processor Utility";
    internal const string PerCorePath = @"\Processor Information(*)\% Processor Time";
    internal const string QueueLengthPath = @"\System\Processor Queue Length";

    private IntPtr _query;
    private readonly IntPtr _utility;
    private readonly IntPtr _perCore;
    private readonly IntPtr _queueLength;

    private ProcessorCounters(IntPtr query, IntPtr utility, IntPtr perCore, IntPtr queueLength)
    {
        _query = query;
        _utility = utility;
        _perCore = perCore;
        _queueLength = queueLength;
    }

    /// <summary>Null when PDH can't be used at all, or none of the counters exists.</summary>
    public static ProcessorCounters? TryOpen()
    {
        try
        {
            if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out var query) != Pdh.ERROR_SUCCESS) return null;
            var utility = Add(query, UtilityPath);
            var perCore = Add(query, PerCorePath);
            var queueLength = Add(query, QueueLengthPath);
            if (utility == IntPtr.Zero && perCore == IntPtr.Zero && queueLength == IntPtr.Zero)
            {
                Pdh.PdhCloseQuery(query);
                return null;
            }

            var counters = new ProcessorCounters(query, utility, perCore, queueLength);
            counters.Collect(); // baseline for the rate counters
            return counters;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static IntPtr Add(IntPtr query, string path) =>
        Pdh.PdhAddEnglishCounter(query, path, IntPtr.Zero, out var counter) == Pdh.ERROR_SUCCESS ? counter : IntPtr.Zero;

    /// <summary>Reads every counter. Not thread-safe; <see cref="SystemInfo"/> serialises its callers.</summary>
    public ProcessorSample Collect()
    {
        if (_query == IntPtr.Zero || Pdh.PdhCollectQueryData(_query) != Pdh.ERROR_SUCCESS) return ProcessorSample.Empty;
        return new ProcessorSample(
            ReadValue(_utility, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100),
            ReadPerCore(),
            ReadValue(_queueLength, Pdh.PDH_FMT_DOUBLE));
    }

    private static double? ReadValue(IntPtr counter, uint format)
    {
        if (counter == IntPtr.Zero) return null;
        if (Pdh.PdhGetFormattedCounterValue(counter, format, out _, out var value) != Pdh.ERROR_SUCCESS) return null;
        return IsValid(value) ? Math.Max(0, value.DoubleValue) : null;
    }

    private IReadOnlyList<double> ReadPerCore()
    {
        if (_perCore == IntPtr.Zero) return Array.Empty<double>();

        uint size = 0;
        var status = Pdh.PdhGetFormattedCounterArray(_perCore, Pdh.PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
        if (status != Pdh.PDH_MORE_DATA || size == 0) return Array.Empty<double>();

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Pdh.PdhGetFormattedCounterArray(_perCore, Pdh.PDH_FMT_DOUBLE, ref size, out var count, buffer) != Pdh.ERROR_SUCCESS)
                return Array.Empty<double>();

            var cores = new List<(int Group, int Index, double Percent)>((int)count);
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                var name = Marshal.PtrToStringUni(item.Name);
                if (!TryParseCoreInstance(name, out var group, out var index) || !IsValid(item.Value)) continue;
                cores.Add((group, index, Math.Clamp(item.Value.DoubleValue, 0, 100)));
            }

            cores.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Index.CompareTo(b.Index));
            var result = new double[cores.Count];
            for (var i = 0; i < cores.Count; i++) result[i] = cores[i].Percent;
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsValid(PDH_FMT_COUNTERVALUE value) =>
        value.CStatus is Pdh.PDH_CSTATUS_VALID_DATA or Pdh.PDH_CSTATUS_NEW_DATA && !double.IsNaN(value.DoubleValue);

    /// <summary>
    /// <c>Processor Information</c> instances are "group,index" for each logical CPU, plus "group,_Total" per group
    /// and "_Total"; only the first kind is a core.
    /// </summary>
    public static bool TryParseCoreInstance(string? name, out int group, out int index)
    {
        group = index = 0;
        if (string.IsNullOrEmpty(name)) return false;
        var comma = name.IndexOf(',');
        return comma > 0
               && int.TryParse(name.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out group)
               && int.TryParse(name.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        Pdh.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}

/// <summary>
/// Turns <c>\System\Processor Queue Length</c> into Process Lasso's "responsiveness" figure. The counter is the
/// number of threads ready to run but waiting for a CPU, across all CPUs; Microsoft's rule of thumb is that more
/// than two waiting per CPU, sustained, means the CPU is the bottleneck. So responsiveness is 100 % with nothing
/// waiting and falls linearly to 0 % at <see cref="SaturatedQueuePerCpu"/> waiting per CPU. The counter is an
/// instantaneous snapshot and jumps around, so it is averaged over the last <see cref="WindowSamples"/> readings.
/// </summary>
public sealed class ResponsivenessMeter
{
    public const double SaturatedQueuePerCpu = 2.0;
    public const int WindowSamples = 5;

    private readonly Queue<double> _window = new();
    private readonly int _processors;
    private double _sum;

    public ResponsivenessMeter(int logicalProcessors) => _processors = Math.Max(1, logicalProcessors);

    /// <summary>Average queue length over the window, or null before the first reading.</summary>
    public double? AverageQueueLength => _window.Count == 0 ? null : _sum / _window.Count;

    /// <summary>Responsiveness in percent, or null before the first reading.</summary>
    public double? Percent => AverageQueueLength is { } q ? FromQueueLength(q, _processors) : null;

    public void Add(double queueLength)
    {
        queueLength = Math.Max(0, queueLength);
        _window.Enqueue(queueLength);
        _sum += queueLength;
        while (_window.Count > WindowSamples) _sum -= _window.Dequeue();
    }

    public static double FromQueueLength(double queueLength, int logicalProcessors)
    {
        var perCpu = Math.Max(0, queueLength) / Math.Max(1, logicalProcessors);
        return Math.Clamp(100.0 * (1 - perCpu / SaturatedQueuePerCpu), 0, 100);
    }
}
