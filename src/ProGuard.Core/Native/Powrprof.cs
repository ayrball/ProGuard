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

    /// <summary>The power mode (slider position) in force. Exported by powrprof since Windows 10 1709; not in the SDK headers.</summary>
    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActualOverlayScheme(out Guid ActualOverlayGuid);

    /// <summary>Sets the power mode, as the Windows settings slider does.</summary>
    [DllImport("powrprof.dll")]
    public static extern uint PowerSetActiveOverlayScheme(Guid OverlaySchemeGuid);

    /// <summary>GUID_PROCESSOR_SETTINGS_SUBGROUP: the processor power management settings of a plan.</summary>
    public static readonly Guid ProcessorSettingsSubgroup = new Guid("54533251-82be-4824-96c1-47b60b740d00");

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint AcValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint DcValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint DcValueIndex);

    /// <summary>The value a setting takes in the given scheme personality (Balanced, High performance, Power saver) out of the box.</summary>
    [DllImport("powrprof.dll")]
    public static extern uint PowerReadACDefaultIndex(IntPtr RootPowerKey, ref Guid SchemePersonalityGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint AcDefaultIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadDCDefaultIndex(IntPtr RootPowerKey, ref Guid SchemePersonalityGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint DcDefaultIndex);

    /// <summary>Reads a setting's raw value; with no subgroup and <see cref="SchemePersonality"/> it gives the plan's personality GUID.</summary>
    [DllImport("powrprof.dll")]
    public static extern uint PowerReadACValue(IntPtr RootPowerKey, ref Guid SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint Type, byte[]? Buffer, ref uint BufferSize);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadValueMin(IntPtr RootPowerKey, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint ValueMinimum);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadValueMax(IntPtr RootPowerKey, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint ValueMaximum);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadValueIncrement(IntPtr RootPowerKey, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint ValueIncrement);

    /// <summary>A setting's attributes (<see cref="AttributeHide"/>); 0 when it has none or doesn't exist.</summary>
    [DllImport("powrprof.dll")]
    public static extern uint PowerReadSettingAttributes(ref Guid SubGroupGuid, ref Guid PowerSettingGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteSettingAttributes(ref Guid SubGroupGuid, ref Guid PowerSettingGuid, uint Attributes);

    /// <summary>POWER_ATTRIBUTE_HIDE: Power Options doesn't show the setting.</summary>
    public const uint AttributeHide = 1;

    /// <summary>GUID_POWERSCHEME_PERSONALITY: which of the three Windows plans a plan was made from.</summary>
    public static readonly Guid SchemePersonality = new Guid("245d8541-3943-4422-b025-13a784f679b7");

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    public static extern uint PowerReadFriendlyName(IntPtr RootPowerKey, ref Guid SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, IntPtr PowerSettingGuid, IntPtr Buffer, ref uint BufferSize);
}
