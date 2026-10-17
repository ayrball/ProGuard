using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;
using ProGuard.Shared.Contracts;

namespace ProGuard.Engine.Services;

/// <summary>A setting's possible values when it is a range rather than a list of choices.</summary>
public readonly record struct PowerSettingRange(uint Min, uint Max, uint Increment);

/// <summary>The power-plan calls <see cref="CpuTuningService"/> makes, beyond <see cref="ICoreParkingPower"/>'s. Tests replace them.</summary>
public interface IPowerSettingsApi : ICoreParkingPower
{
    IReadOnlyList<PowerPlanInfo> Plans();

    /// <summary>Which Windows plan (Balanced, High performance, Power saver) <paramref name="scheme"/> was made from; Guid.Empty if unknown.</summary>
    Guid Personality(Guid scheme);

    /// <summary>The setting's out-of-the-box values for a personality, or null if Windows doesn't say.</summary>
    AcDc? ReadDefault(Guid personality, Guid setting);

    /// <summary>The setting's range, or null for a setting that is a list of choices (or unreadable).</summary>
    PowerSettingRange? ReadRange(Guid setting);

    /// <summary>The setting's attributes (<see cref="Powrprof.AttributeHide"/>).</summary>
    uint ReadAttributes(Guid setting);

    bool WriteAttributes(Guid setting, uint attributes);

    /// <summary>The CPU has more than one efficiency class (P-cores and E-cores).</summary>
    bool IsHybrid();
}

public sealed class Win32PowerSettings : Win32CoreParkingPower, IPowerSettingsApi
{
    public IReadOnlyList<PowerPlanInfo> Plans() => PowerPlans.Enumerate();

    public Guid Personality(Guid scheme)
    {
        var setting = Powrprof.SchemePersonality;
        var buffer = new byte[16];
        uint size = (uint)buffer.Length;
        if (Powrprof.PowerReadACValue(IntPtr.Zero, ref scheme, IntPtr.Zero, ref setting, out _, buffer, ref size) == 0 && size == 16)
            return new Guid(buffer);
        // The three Windows plans are their own personality.
        return scheme == Powrprof.Balanced || scheme == Powrprof.HighPerformance || scheme == Powrprof.PowerSaver ? scheme : Guid.Empty;
    }

    public AcDc? ReadDefault(Guid personality, Guid setting)
    {
        if (personality == Guid.Empty) return null;
        var subgroup = Powrprof.ProcessorSettingsSubgroup;
        if (Powrprof.PowerReadACDefaultIndex(IntPtr.Zero, ref personality, ref subgroup, ref setting, out var ac) != 0) return null;
        if (Powrprof.PowerReadDCDefaultIndex(IntPtr.Zero, ref personality, ref subgroup, ref setting, out var dc) != 0) return null;
        return new AcDc(ac, dc);
    }

    public PowerSettingRange? ReadRange(Guid setting)
    {
        var subgroup = Powrprof.ProcessorSettingsSubgroup;
        if (Powrprof.PowerReadValueMin(IntPtr.Zero, ref subgroup, ref setting, out var min) != 0) return null;
        if (Powrprof.PowerReadValueMax(IntPtr.Zero, ref subgroup, ref setting, out var max) != 0) return null;
        if (Powrprof.PowerReadValueIncrement(IntPtr.Zero, ref subgroup, ref setting, out var increment) != 0 || increment == 0) increment = 1;
        return min <= max ? new PowerSettingRange(min, max, increment) : null;
    }

    public uint ReadAttributes(Guid setting)
    {
        var subgroup = Powrprof.ProcessorSettingsSubgroup;
        return Powrprof.PowerReadSettingAttributes(ref subgroup, ref setting);
    }

    public bool WriteAttributes(Guid setting, uint attributes)
    {
        var subgroup = Powrprof.ProcessorSettingsSubgroup;
        return Powrprof.PowerWriteSettingAttributes(ref subgroup, ref setting, attributes) == 0;
    }

    public bool IsHybrid()
    {
        try { return CpuSets.GetSystemCpuSets().Select(s => s.EfficiencyClass).Distinct().Count() > 1; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or EntryPointNotFoundException) { return false; }
    }
}

/// <summary>A plan's values before ProGuard first changed it.</summary>
public sealed class CpuTuningPlanOriginals
{
    public DateTime TakenUtc { get; set; }
    public Dictionary<Guid, AcDc> Values { get; set; } = new();
}

/// <summary>What <see cref="CpuTuningService"/> keeps so the user can always go back.</summary>
public sealed class CpuTuningOriginals
{
    public Dictionary<Guid, CpuTuningPlanOriginals> Plans { get; set; } = new();

    /// <summary>Settings ProGuard made visible in Power Options, with their attributes before; hidden again once no plan is tuned.</summary>
    public Dictionary<Guid, uint> Unhidden { get; set; } = new();
}

public interface ICpuTuningOriginalsStore
{
    CpuTuningOriginals Load();
    void Save(CpuTuningOriginals originals);
}

public sealed class MemoryCpuTuningOriginalsStore : ICpuTuningOriginalsStore
{
    public CpuTuningOriginals Originals { get; set; } = new();
    public CpuTuningOriginals Load() => Originals;
    public void Save(CpuTuningOriginals originals) => Originals = originals;
}

/// <summary>
/// %ProgramData%\ProGuard\cpu-tuning-originals.json, next to the other recovery records and written the same way
/// (through to disk, then renamed over the old file), so the values to revert to survive a reinstall of the database
/// and a crash halfway through a save.
/// </summary>
public sealed class FileCpuTuningOriginalsStore : ICpuTuningOriginalsStore
{
    private readonly string _path;
    private readonly ILogger _logger;

    public FileCpuTuningOriginalsStore(ILogger? logger = null)
        : this(Path.Combine(Path.GetDirectoryName(ProGuardDbContext.DatabasePath)!, "cpu-tuning-originals.json"), logger)
    {
    }

    public FileCpuTuningOriginalsStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    public CpuTuningOriginals Load()
    {
        try
        {
            if (!File.Exists(_path)) return new CpuTuningOriginals();
            return JsonSerializer.Deserialize<CpuTuningOriginals>(File.ReadAllText(_path)) ?? new CpuTuningOriginals();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't read the power plan values to revert to from {Path}; starting afresh", _path);
            return new CpuTuningOriginals();
        }
    }

    public void Save(CpuTuningOriginals originals)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                JsonSerializer.Serialize(stream, originals);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't save the power plan values to revert to in {Path}", _path);
        }
    }
}

/// <summary>The outcome of <see cref="CpuTuningService.Set"/>: the plan afterwards, what changed, and what Windows refused.</summary>
public sealed record CpuTuningChange(CpuTuningDto Tuning, string Summary, string? Error);

/// <summary>
/// Core parking and processor frequency per power plan, plugged in (AC) and on battery (DC), as Bitsum's tool for it
/// does: <see cref="CpuTuningCatalog.Settings"/> read with <c>PowerReadACValueIndex</c>/<c>PowerReadDCValueIndex</c> and
/// written with <c>PowerWriteACValueIndex</c>/<c>PowerWriteDCValueIndex</c>, then <c>PowerSetActiveScheme</c> when the
/// plan is the active one, so Windows picks the values up at once. Another plan's values take effect when it is next
/// switched to; it is never made active here.
/// <list type="bullet">
/// <item><b>Unavailable settings.</b> A setting Windows can't read in the plan is reported unavailable, as are the
/// efficiency class 1 twins on a CPU with one kind of core.</item>
/// <item><b>Hidden settings.</b> Most of these are hidden in Power Options. One ProGuard changes is made visible there,
/// so what changed can be seen without ProGuard; once no plan is tuned any more (every one reverted) it is hidden again.</item>
/// <item><b>Revert.</b> Before the first change to a plan, its values are recorded (<see cref="ICpuTuningOriginalsStore"/>);
/// <see cref="CpuTuningPresets.Revert"/> writes them back and drops the record.</item>
/// <item><b>ProBalance.</b> While <see cref="CoreParkingService"/> holds a plan's cores unparked, the min cores settings
/// it changed are left at 100 %: a new value goes into what it puts back (<see cref="CoreParkingService.WriteUserValue"/>),
/// and reads report the user's value with the live one alongside. So the hold isn't fought, and ends on the user's
/// tuned values rather than the ones it found.</item>
/// </list>
/// </summary>
public sealed class CpuTuningService
{
    private const uint AllCores = 100;

    private readonly ILogger<CpuTuningService> _logger;
    private readonly IPowerSettingsApi _power;
    private readonly ICpuTuningOriginalsStore _originals;
    private readonly CoreParkingService? _coreParking;
    private readonly object _sync = new();

    public CpuTuningService(ILogger<CpuTuningService>? logger = null, IPowerSettingsApi? power = null,
        ICpuTuningOriginalsStore? originals = null, CoreParkingService? coreParking = null)
    {
        _logger = logger ?? NullLogger<CpuTuningService>.Instance;
        _power = power ?? new Win32PowerSettings();
        // A faked power API never leaves a record on the real disk.
        _originals = originals ?? (power == null ? new FileCpuTuningOriginalsStore(_logger) : new MemoryCpuTuningOriginalsStore());
        _coreParking = coreParking;
    }

    /// <summary>The plan's settings; <paramref name="plan"/> null or empty = the active plan. Throws ArgumentException for a plan that isn't installed.</summary>
    public CpuTuningDto Get(Guid? plan)
    {
        lock (_sync) return Read(ResolvePlan(plan));
    }

    /// <summary>Applies values or a preset to one plan. Throws ArgumentException for a request that can't be applied at all.</summary>
    public CpuTuningChange Set(CpuTuningArgument argument)
    {
        lock (_sync)
        {
            var plan = ResolvePlan(argument.PlanId);
            var info = Describe(plan);
            var targets = argument.Preset is { Length: > 0 } preset ? PresetTargets(plan, info, preset) : ValueTargets(info, argument.Values);
            bool revert = argument.Preset == CpuTuningPresets.Revert;

            var store = _originals.Load();
            if (!revert && !store.Plans.ContainsKey(plan))
            {
                // Before the first change, so there is never a moment the plan is changed and its old values unknown.
                store.Plans[plan] = new CpuTuningPlanOriginals
                {
                    TakenUtc = DateTime.UtcNow,
                    Values = info.Where(s => s.Value.Available).ToDictionary(s => s.Key, s => s.Value.User),
                };
                _originals.Save(store);
            }

            var changed = new List<string>();
            var refused = new List<string>();
            bool reapply = false;
            foreach (var (setting, values) in targets)
            {
                var current = info[setting];
                if (current.User == values) continue;
                var name = Catalog(setting).Key;
                bool written = _coreParking != null
                    ? _coreParking.WriteUserValue(plan, setting, values, () => _power.Write(plan, setting, values), out var deferred)
                    : Write(plan, setting, values, out deferred);
                if (!written) { refused.Add(name); continue; }
                changed.Add(name);
                reapply |= !deferred;
                if (!revert && (current.Attributes & Powrprof.AttributeHide) != 0) Unhide(store, setting, current.Attributes);
            }

            if (revert)
            {
                store.Plans.Remove(plan);
                if (store.Plans.Count == 0) RehideAll(store);
            }
            _originals.Save(store);
            if (reapply && _power.ActiveScheme() == plan) _power.Reapply(plan);

            var planName = PlanName(plan);
            var summary = argument.Preset is { Length: > 0 } p
                ? $"Applied {CpuTuningPresets.Describe(p)} to the {planName} power plan ({Count(changed.Count)} changed)."
                : $"Changed {string.Join(", ", changed)} on the {planName} power plan.";
            if (changed.Count == 0 && refused.Count == 0) summary = $"The {planName} power plan already had those values.";
            string? error = refused.Count == 0 ? null : $"Windows wouldn't change {string.Join(", ", refused)} on the {planName} power plan.";
            _logger.LogInformation("{Summary}{Error}", summary, error == null ? string.Empty : " " + error);
            return new CpuTuningChange(Read(plan), summary, error);
        }
    }

    private string PlanName(Guid plan) => _power.Plans().FirstOrDefault(p => p.Id == plan).Name ?? PowerPlans.Describe(plan);

    private static string Count(int n) => n == 1 ? "1 setting" : $"{n} settings";

    private bool Write(Guid plan, Guid setting, AcDc values, out bool deferred)
    {
        deferred = false;
        return _power.Write(plan, setting, values);
    }

    private void Unhide(CpuTuningOriginals store, Guid setting, uint attributes)
    {
        if (!_power.WriteAttributes(setting, attributes & ~Powrprof.AttributeHide))
        {
            _logger.LogWarning("Couldn't show {Setting} in Power Options", Catalog(setting).Key);
            return;
        }
        store.Unhidden.TryAdd(setting, attributes);
    }

    private void RehideAll(CpuTuningOriginals store)
    {
        foreach (var (setting, attributes) in store.Unhidden)
            if (!_power.WriteAttributes(setting, attributes))
                _logger.LogWarning("Couldn't hide {Setting} in Power Options again", setting);
        store.Unhidden.Clear();
    }

    private Guid ResolvePlan(Guid? plan)
    {
        var id = plan is { } p && p != Guid.Empty ? p : _power.ActiveScheme();
        if (id == Guid.Empty) throw new ArgumentException("Windows didn't say which power plan is active.");
        if (_power.Plans().All(x => x.Id != id)) throw new ArgumentException("That power plan isn't installed on this PC.");
        return id;
    }

    private static CpuTuningSettingInfo Catalog(Guid setting) => CpuTuningCatalog.Settings.First(s => s.Id == setting);

    /// <summary>One setting of a plan as it stands: the user's values (what a hold puts back), the live ones, and its limits.</summary>
    private sealed record SettingState(CpuTuningSettingInfo Info, bool Available, string? Reason, AcDc User, AcDc? Live,
        uint Min, uint Max, uint Increment, uint Attributes);

    private Dictionary<Guid, SettingState> Describe(Guid plan)
    {
        bool hybrid = _power.IsHybrid();
        var states = new Dictionary<Guid, SettingState>();
        foreach (var info in CpuTuningCatalog.Settings)
        {
            var range = info.Kind == CpuTuningKind.Choice ? null : _power.ReadRange(info.Id);
            uint min = range?.Min ?? info.Min, max = range?.Max ?? info.Max, increment = range?.Increment ?? 1;
            var attributes = _power.ReadAttributes(info.Id);

            var read = _power.Read(plan, info.Id);
            string? reason = read == null ? "Windows on this PC doesn't have this setting."
                : info.EfficiencyClass > 0 && !hybrid ? "Only for CPUs with two kinds of cores (P-cores and E-cores)."
                : null;
            var live = read ?? default;
            var held = _coreParking?.HeldOriginal(plan, info.Id);
            states[info.Id] = new SettingState(info, reason == null, reason, held ?? live, held == null ? null : live,
                min, max, increment, attributes);
        }
        return states;
    }

    private CpuTuningDto Read(Guid plan)
    {
        var info = Describe(plan);
        var personality = _power.Personality(plan);
        var originals = _originals.Load().Plans.GetValueOrDefault(plan);
        var dto = new CpuTuningDto
        {
            PlanId = plan,
            PlanName = PlanName(plan),
            IsActivePlan = _power.ActiveScheme() == plan,
            Plans = _power.Plans().Select(p => new PowerPlanDto { Id = p.Id, Name = p.Name }).ToList(),
            IsHybrid = _power.IsHybrid(),
            HasOriginals = originals != null,
            OriginalsTakenUtc = originals?.TakenUtc,
            ParkingOverrideActive = _coreParking?.HeldScheme == plan,
        };
        foreach (var s in info.Values)
        {
            var defaults = s.Available ? _power.ReadDefault(personality, s.Info.Id) : null;
            AcDc? original = originals != null && originals.Values.TryGetValue(s.Info.Id, out var o) ? o : null;
            dto.Settings.Add(new CpuTuningSettingDto
            {
                Key = s.Info.Key,
                Id = s.Info.Id,
                Group = s.Info.Group,
                Name = s.Info.Name,
                Description = s.Info.Description,
                EfficiencyClass = s.Info.EfficiencyClass,
                Kind = s.Info.Kind,
                Min = s.Min,
                Max = s.Max,
                Increment = s.Increment,
                Unit = s.Info.Unit,
                Choices = s.Info.Choices?.Select(c => new CpuTuningChoiceDto(c.Value, c.Name)).ToList() ?? new(),
                Available = s.Available,
                UnavailableReason = s.Reason,
                Hidden = (s.Attributes & Powrprof.AttributeHide) != 0,
                Ac = s.User.Ac,
                Dc = s.User.Dc,
                LiveAc = s.Live?.Ac,
                LiveDc = s.Live?.Dc,
                DefaultAc = defaults?.Ac,
                DefaultDc = defaults?.Dc,
                OriginalAc = original?.Ac,
                OriginalDc = original?.Dc,
            });
        }
        return dto;
    }

    private static Dictionary<Guid, AcDc> ValueTargets(Dictionary<Guid, SettingState> info, IReadOnlyList<CpuTuningValueDto>? values)
    {
        if (values == null || values.Count == 0) throw new ArgumentException("Name at least one setting to change, or a preset.");
        var targets = new Dictionary<Guid, AcDc>();
        foreach (var value in values)
        {
            var catalog = CpuTuningCatalog.Find(value.Key) ?? throw new ArgumentException($"Unknown CPU setting '{value.Key}'.");
            var state = info[catalog.Id];
            if (!state.Available) throw new ArgumentException($"{catalog.Key} can't be changed: {state.Reason}");
            var current = targets.TryGetValue(catalog.Id, out var pending) ? pending : state.User;
            var next = new AcDc(value.Ac ?? current.Ac, value.Dc ?? current.Dc);
            Validate(state, next.Ac);
            Validate(state, next.Dc);
            targets[catalog.Id] = next;
        }
        return targets;
    }

    private static void Validate(SettingState state, uint value)
    {
        var info = state.Info;
        if (info.Kind == CpuTuningKind.Choice)
        {
            if (info.Choices!.All(c => c.Value != value)) throw new ArgumentException($"{value} isn't one of {info.Key}'s choices.");
            return;
        }
        if (value < state.Min || value > state.Max || (value - state.Min) % state.Increment != 0)
            throw new ArgumentException($"{info.Key} must be between {state.Min} and {state.Max}{(state.Increment > 1 ? $" in steps of {state.Increment}" : "")}.");
    }

    private Dictionary<Guid, AcDc> PresetTargets(Guid plan, Dictionary<Guid, SettingState> info, string preset)
    {
        var available = info.Values.Where(s => s.Available).ToList();
        switch (preset)
        {
            case CpuTuningPresets.DisableParking:
                return available.Where(s => s.Info.Id == CpuTuningCatalog.MinCores || s.Info.Id == CpuTuningCatalog.MinCores1
                                            || s.Info.Id == CpuTuningCatalog.MaxCores || s.Info.Id == CpuTuningCatalog.MaxCores1)
                    .ToDictionary(s => s.Info.Id, _ => new AcDc(AllCores, AllCores));

            case CpuTuningPresets.BalancedParking:
                return Defaults(available.Where(s => s.Info.IsParking), Powrprof.Balanced, "Windows doesn't say how its Balanced plan parks cores.");

            case CpuTuningPresets.WindowsDefaults:
                var personality = _power.Personality(plan);
                if (personality == Guid.Empty) throw new ArgumentException("Windows doesn't say which plan this one was made from, so it has no defaults to go back to.");
                return Defaults(available, personality, "Windows doesn't say what this plan's defaults are.");

            case CpuTuningPresets.Revert:
                var originals = _originals.Load().Plans.GetValueOrDefault(plan)
                                ?? throw new ArgumentException("ProGuard hasn't changed this power plan, so there is nothing to revert.");
                return available.Where(s => originals.Values.ContainsKey(s.Info.Id)).ToDictionary(s => s.Info.Id, s => originals.Values[s.Info.Id]);

            default:
                throw new ArgumentException($"Unknown preset '{preset}'.");
        }
    }

    private Dictionary<Guid, AcDc> Defaults(IEnumerable<SettingState> settings, Guid personality, string noneKnown)
    {
        var targets = new Dictionary<Guid, AcDc>();
        foreach (var s in settings)
            if (_power.ReadDefault(personality, s.Info.Id) is { } defaults) targets[s.Info.Id] = defaults;
        return targets.Count > 0 ? targets : throw new ArgumentException(noneKnown);
    }
}
