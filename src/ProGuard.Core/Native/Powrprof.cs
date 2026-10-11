using System;
using System.Runtime.InteropServices;

namespace ProGuard.Core.Native;

public static class Powrprof
{
    public static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

    /// <summary>POWER_DATA_ACCESSOR.ACCESS_SCHEME: makes <see cref="PowerEnumerate"/> list power schemes.</summary>
    public const uint AccessScheme = 16;
    public const uint ErrorNoMoreItems = 259;
    public const uint ErrorMoreData = 234;

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    public static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    public static extern uint PowerEnumerate(IntPtr RootPowerKey, IntPtr SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, uint AccessFlags, uint Index, ref Guid Buffer, ref uint BufferSize);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    public static extern uint PowerReadFriendlyName(IntPtr RootPowerKey, ref Guid SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, IntPtr PowerSettingGuid, IntPtr Buffer, ref uint BufferSize);
}
