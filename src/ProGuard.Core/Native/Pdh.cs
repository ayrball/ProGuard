using System;
using System.Runtime.InteropServices;

namespace ProGuard.Core.Native;

[StructLayout(LayoutKind.Sequential)]
public struct PDH_FMT_COUNTERVALUE
{
    public uint CStatus;
    // The value is a union (long / double / long long); with PDH_FMT_DOUBLE only the double is set. It is 8-byte
    // aligned, so sequential layout puts it at offset 8 on both x86 and x64, as in the native struct.
    public double DoubleValue;
}

[StructLayout(LayoutKind.Sequential)]
public struct PDH_FMT_COUNTERVALUE_ITEM
{
    public IntPtr Name;
    public PDH_FMT_COUNTERVALUE Value;
}

/// <summary>Performance Data Helper: the counters Task Manager and Process Lasso's GUI read.</summary>
public static class Pdh
{
    public const uint ERROR_SUCCESS = 0;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_CSTATUS_VALID_DATA = 0;
    public const uint PDH_CSTATUS_NEW_DATA = 1;
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    /// <summary>Don't cap at 100: <c>% Processor Utility</c> passes 100 while the CPU runs above base frequency.</summary>
    public const uint PDH_FMT_NOCAP100 = 0x00008000;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    /// <summary>Takes the English counter path whatever the system language is.</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);
}
