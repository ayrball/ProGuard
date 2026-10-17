using System;
using System.Collections.Generic;
using System.Linq;

namespace ProGuard.Shared.Contracts;

/// <summary>How a CPU tuning setting is edited.</summary>
public enum CpuTuningKind
{
    /// <summary>0-100 %.</summary>
    Percent,

    /// <summary>A count in <see cref="CpuTuningSettingDto.Unit"/> (time checks), between Min and Max.</summary>
    Count,

    /// <summary>One of <see cref="CpuTuningSettingDto.Choices"/>.</summary>
    Choice,
}

public sealed class CpuTuningChoiceDto
{
    public uint Value { get; set; }
    public string Name { get; set; } = string.Empty;

    public CpuTuningChoiceDto() { }
    public CpuTuningChoiceDto(uint value, string name) { Value = value; Name = name; }
}

/// <summary>One processor power setting of a plan, for <see cref="Messages.CommandTypes.GetCpuTuning"/>.</summary>
public sealed class CpuTuningSettingDto
{
    /// <summary>The powercfg alias (CPMINCORES), which also names the setting in <see cref="CpuTuningValueDto.Key"/>.</summary>
    public string Key { get; set; } = string.Empty;
    public Guid Id { get; set; }
    /// <summary>One of <see cref="CpuTuningCatalog.Groups"/>.</summary>
    public string Group { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>What it does, in one line.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 0 for the setting every CPU has; 1 for its twin that Windows applies to processor power efficiency class 1, the
    /// faster cores (P-cores) of a hybrid CPU. On a hybrid CPU the class 0 setting then covers the efficient cores.
    /// </summary>
    public int EfficiencyClass { get; set; }

    public CpuTuningKind Kind { get; set; }
    public uint Min { get; set; }
    public uint Max { get; set; }
    public uint Increment { get; set; } = 1;
    public string Unit { get; set; } = string.Empty;
    public List<CpuTuningChoiceDto> Choices { get; set; } = new();

    /// <summary>False when this PC's Windows or firmware doesn't have the setting; <see cref="UnavailableReason"/> says why.</summary>
    public bool Available { get; set; }
    public string? UnavailableReason { get; set; }

    /// <summary>Windows' Power Options doesn't show it (powercfg's ATTRIB_HIDE). Changing it from ProGuard shows it there.</summary>
    public bool Hidden { get; set; }

    /// <summary>The plan's values (plugged in, on battery): the user's own, even while ProBalance overrides them for now.</summary>
    public uint Ac { get; set; }
    public uint Dc { get; set; }

    /// <summary>
    /// Set while ProBalance's "unpark every core while restraining" holds a different value in force: what Windows
    /// uses right now. <see cref="Ac"/>/<see cref="Dc"/> come back when it lets go.
    /// </summary>
    public uint? LiveAc { get; set; }
    public uint? LiveDc { get; set; }

    /// <summary>Windows' out-of-the-box value for the plan's personality (Balanced, High performance, Power saver); null if unknown.</summary>
    public uint? DefaultAc { get; set; }
    public uint? DefaultDc { get; set; }

    /// <summary>The value before ProGuard first changed this plan; null until it has.</summary>
    public uint? OriginalAc { get; set; }
    public uint? OriginalDc { get; set; }
}

/// <summary>A plan's CPU tuning, the reply to <see cref="Messages.CommandTypes.GetCpuTuning"/> and <see cref="Messages.CommandTypes.SetCpuTuning"/>.</summary>
public sealed class CpuTuningDto
{
    public Guid PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    /// <summary>The plan Windows has active: changes to it take effect at once, to any other when it is next switched to.</summary>
    public bool IsActivePlan { get; set; }
    /// <summary>Every installed plan, for the picker.</summary>
    public List<PowerPlanDto> Plans { get; set; } = new();

    /// <summary>The CPU has more than one efficiency class (P-cores and E-cores), so class 1 settings mean something.</summary>
    public bool IsHybrid { get; set; }

    /// <summary>ProGuard has changed this plan and kept its earlier values, so <see cref="CpuTuningPresets.Revert"/> can put them back.</summary>
    public bool HasOriginals { get; set; }
    public DateTime? OriginalsTakenUtc { get; set; }

    /// <summary>ProBalance holds every core of this plan unparked right now (the min cores settings show <see cref="CpuTuningSettingDto.LiveAc"/>).</summary>
    public bool ParkingOverrideActive { get; set; }

    public List<CpuTuningSettingDto> Settings { get; set; } = new();
}

/// <summary>One setting to change; a null value leaves that power source as it is.</summary>
public sealed class CpuTuningValueDto
{
    public string Key { get; set; } = string.Empty;
    public uint? Ac { get; set; }
    public uint? Dc { get; set; }
}

/// <summary>The argument of <see cref="Messages.CommandTypes.SetCpuTuning"/>: either values or a preset (one of <see cref="CpuTuningPresets"/>).</summary>
public sealed class CpuTuningArgument
{
    public Guid PlanId { get; set; }
    public List<CpuTuningValueDto> Values { get; set; } = new();
    public string? Preset { get; set; }
}

/// <summary>The presets <see cref="CpuTuningArgument.Preset"/> takes.</summary>
public static class CpuTuningPresets
{
    /// <summary>Min and max unparked cores 100 % for every class, plugged in and on battery: no core is ever parked.</summary>
    public const string DisableParking = "DisableParking";

    /// <summary>The core parking settings as Windows' Balanced plan has them out of the box (parking on). Leaves frequency alone.</summary>
    public const string BalancedParking = "BalancedParking";

    /// <summary>Every setting back to Windows' out-of-the-box value for the plan's personality.</summary>
    public const string WindowsDefaults = "WindowsDefaults";

    /// <summary>Every setting back to what it was before ProGuard first changed the plan, and the record dropped.</summary>
    public const string Revert = "Revert";

    public static readonly IReadOnlyList<string> All = new[] { DisableParking, BalancedParking, WindowsDefaults, Revert };

    public static string Describe(string preset) => preset switch
    {
        DisableParking => "disable core parking",
        BalancedParking => "Balanced parking",
        WindowsDefaults => "Windows defaults",
        Revert => "the values before ProGuard's first change",
        _ => preset,
    };
}

/// <summary>A processor power setting CPU tuning shows: its GUID, how to edit it and what it does.</summary>
public sealed record CpuTuningSettingInfo(
    string Key,
    Guid Id,
    string Group,
    string Name,
    string Description,
    CpuTuningKind Kind,
    uint Min,
    uint Max,
    string Unit,
    int EfficiencyClass = 0,
    IReadOnlyList<CpuTuningChoiceDto>? Choices = null)
{
    /// <summary>Part of core parking proper: the "Disable parking" and "Balanced parking" presets set these.</summary>
    public bool IsParking => Group is CpuTuningCatalog.Parking or CpuTuningCatalog.ParkingEngine;
}

/// <summary>
/// The processor power settings (subgroup <c>SUB_PROCESSOR</c>) CPU tuning reads and writes, by their powercfg aliases.
/// Settings with a "1" suffix are Windows' twins for processor power efficiency class 1.
/// </summary>
public static class CpuTuningCatalog
{
    public const string Parking = "CORE PARKING";
    public const string Performance = "FREQUENCY & BOOST";
    public const string ParkingEngine = "PARKING ENGINE";
    public static readonly IReadOnlyList<string> Groups = new[] { Parking, Performance, ParkingEngine };

    public static readonly Guid MinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");
    public static readonly Guid MinCores1 = new("0cc5b647-c1df-4637-891a-dec35c318584");
    public static readonly Guid MaxCores = new("ea062031-0e34-4ff1-9b6d-eb1059334028");
    public static readonly Guid MaxCores1 = new("ea062031-0e34-4ff1-9b6d-eb1059334029");

    private const string Checks = "TIME CHECKS";

    private static readonly CpuTuningChoiceDto[] BoostModes =
    {
        new(0, "Disabled"), new(1, "Enabled"), new(2, "Aggressive"), new(3, "Efficient enabled"),
        new(4, "Efficient aggressive"), new(5, "Aggressive at guaranteed"), new(6, "Efficient aggressive at guaranteed"),
    };

    private static readonly CpuTuningChoiceDto[] ParkingPolicies =
    {
        new(0, "Ideal number of cores"), new(1, "One core"), new(2, "All possible cores"), new(3, "One eighth of the cores"),
    };

    public static readonly IReadOnlyList<CpuTuningSettingInfo> Settings = new CpuTuningSettingInfo[]
    {
        // Core parking
        new("CPMINCORES", MinCores, Parking, "MIN UNPARKED CORES",
            "Share of cores Windows must keep unparked. 100 % turns parking off.", CpuTuningKind.Percent, 0, 100, "%"),
        new("CPMINCORES1", MinCores1, Parking, "MIN UNPARKED CORES",
            "Share of cores Windows must keep unparked. 100 % turns parking off.", CpuTuningKind.Percent, 0, 100, "%", 1),
        new("CPMAXCORES", MaxCores, Parking, "MAX UNPARKED CORES",
            "Share of cores Windows may unpark at most. Lower holds cores parked even under load.", CpuTuningKind.Percent, 0, 100, "%"),
        new("CPMAXCORES1", MaxCores1, Parking, "MAX UNPARKED CORES",
            "Share of cores Windows may unpark at most. Lower holds cores parked even under load.", CpuTuningKind.Percent, 0, 100, "%", 1),

        // Frequency and boost
        new("PROCTHROTTLEMIN", new("893dee8e-2bef-41e0-89c6-b55d0929964c"), Performance, "MINIMUM PROCESSOR STATE",
            "Lowest speed the CPU may drop to. Higher keeps clocks up when idle and costs power.", CpuTuningKind.Percent, 0, 100, "%"),
        new("PROCTHROTTLEMIN1", new("893dee8e-2bef-41e0-89c6-b55d0929964d"), Performance, "MINIMUM PROCESSOR STATE",
            "Lowest speed the CPU may drop to. Higher keeps clocks up when idle and costs power.", CpuTuningKind.Percent, 0, 100, "%", 1),
        new("PROCTHROTTLEMAX", new("bc5038f7-23e0-4960-96da-33abaf5935ec"), Performance, "MAXIMUM PROCESSOR STATE",
            "Highest speed allowed. Below 100 % caps the clock and usually stops boost.", CpuTuningKind.Percent, 0, 100, "%"),
        new("PROCTHROTTLEMAX1", new("bc5038f7-23e0-4960-96da-33abaf5935ed"), Performance, "MAXIMUM PROCESSOR STATE",
            "Highest speed allowed. Below 100 % caps the clock and usually stops boost.", CpuTuningKind.Percent, 0, 100, "%", 1),
        new("PERFBOOSTMODE", new("be337238-0d82-4146-a960-4f3749d470c7"), Performance, "BOOST MODE",
            "How eagerly the CPU runs above its base clock.", CpuTuningKind.Choice, 0, 6, string.Empty, 0, BoostModes),
        new("PERFEPP", new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), Performance, "ENERGY PERFORMANCE PREFERENCE",
            "Hint to the CPU's own power manager: 0 % favours speed, 100 % favours battery life.", CpuTuningKind.Percent, 0, 100, "%"),
        new("PERFEPP1", new("36687f9e-e3a5-4dbf-b1dc-15eb381c6864"), Performance, "ENERGY PERFORMANCE PREFERENCE",
            "Hint to the CPU's own power manager: 0 % favours speed, 100 % favours battery life.", CpuTuningKind.Percent, 0, 100, "%", 1),
        new("PERFINCTHRESHOLD", new("06cadf0e-64ed-448a-8927-ce7bf90eb35d"), Performance, "SPEED-UP THRESHOLD",
            "How busy a core must get before Windows raises its speed.", CpuTuningKind.Percent, 0, 100, "%"),
        new("PERFINCTHRESHOLD1", new("06cadf0e-64ed-448a-8927-ce7bf90eb35e"), Performance, "SPEED-UP THRESHOLD",
            "How busy a core must get before Windows raises its speed.", CpuTuningKind.Percent, 0, 100, "%", 1),
        new("PERFDECTHRESHOLD", new("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6"), Performance, "SLOW-DOWN THRESHOLD",
            "How idle a core must get before Windows lowers its speed.", CpuTuningKind.Percent, 0, 100, "%"),
        new("PERFDECTHRESHOLD1", new("12a0ab44-fe28-4fa9-b3bd-4b64f44960a7"), Performance, "SLOW-DOWN THRESHOLD",
            "How idle a core must get before Windows lowers its speed.", CpuTuningKind.Percent, 0, 100, "%", 1),

        // The hidden settings that drive the parking decisions
        new("CPCONCURRENCY", new("2430ab6f-a520-44a2-9601-f7f23b5134b1"), ParkingEngine, "CONCURRENCY THRESHOLD",
            "Load across the unparked cores above which Windows unparks more.", CpuTuningKind.Percent, 0, 100, "%"),
        new("CPHEADROOM", new("f735a673-2066-4f80-a0c5-ddee0cf1bf5d"), ParkingEngine, "CONCURRENCY HEADROOM",
            "Spare capacity Windows keeps unparked above the current load.", CpuTuningKind.Percent, 0, 100, "%"),
        new("CPDISTRIBUTION", new("4bdaf4e9-d103-46d7-a5f0-6280121616ef"), ParkingEngine, "DISTRIBUTION THRESHOLD",
            "Per-core load below which Windows considers moving work onto fewer cores.", CpuTuningKind.Percent, 0, 100, "%"),
        new("CPOVERUTIL", new("943c8cb6-6f93-4227-ad87-e9a3feec08d1"), ParkingEngine, "OVERUTILIZATION THRESHOLD",
            "Load at which a core counts as overloaded, so Windows won't park around it.", CpuTuningKind.Percent, 5, 100, "%"),
        new("CPINCREASETIME", new("2ddd5a84-5a71-437e-912a-db0b8c788732"), ParkingEngine, "UNPARK DELAY",
            "Time checks Windows waits before unparking more cores.", CpuTuningKind.Count, 1, 100, Checks),
        new("CPDECREASETIME", new("dfd10d17-d5eb-45dd-877a-9a34ddd15c82"), ParkingEngine, "PARK DELAY",
            "Time checks Windows waits before parking cores again.", CpuTuningKind.Count, 1, 100, Checks),
        new("CPINCREASEPOL", new("c7be0679-2817-4d69-9d02-519a537ed0c6"), ParkingEngine, "UNPARK POLICY",
            "How many cores Windows unparks at a time when load rises.", CpuTuningKind.Choice, 0, 3, string.Empty, 0, ParkingPolicies),
        new("CPDECREASEPOL", new("71021b41-c749-4d21-be74-a00f335d582b"), ParkingEngine, "PARK POLICY",
            "How many cores Windows parks at a time when load falls.", CpuTuningKind.Choice, 0, 3, string.Empty, 0, ParkingPolicies),
        new("CPPERF", new("447235c7-6a8d-4cc0-8e24-9eaf70b96e2b"), ParkingEngine, "PARKED CORE STATE",
            "Speed a core is left at while parked.", CpuTuningKind.Choice, 0, 2, string.Empty, 0,
            new CpuTuningChoiceDto[] { new(0, "No preference"), new(1, "Deepest"), new(2, "Lightest") }),
        new("DISTRIBUTEUTIL", new("e0007330-f589-42ed-a401-5ddb10e785d3"), ParkingEngine, "UTILITY DISTRIBUTION",
            "Weighs how evenly work could spread before parking a core.", CpuTuningKind.Choice, 0, 1, string.Empty, 0,
            new CpuTuningChoiceDto[] { new(0, "Disabled"), new(1, "Enabled") }),
    };

    public static CpuTuningSettingInfo? Find(string key) =>
        Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
}
