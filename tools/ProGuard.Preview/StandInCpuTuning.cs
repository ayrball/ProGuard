using ProGuard.Shared.Contracts;

namespace ProGuard.Preview;

/// <summary>
/// The stand-in machine's processor power settings per plan, for <see cref="ProGuard.Shared.Messages.CommandTypes.GetCpuTuning"/>
/// and <see cref="ProGuard.Shared.Messages.CommandTypes.SetCpuTuning"/>: plausible Windows values for each plan, one setting
/// this "Windows" lacks, the values before the first change, and the parking state they lead to. Not thread-safe:
/// <see cref="StandInMachine"/> holds its lock around every call.
/// </summary>
internal sealed class StandInCpuTuning
{
    /// <summary>A setting this machine's Windows doesn't have, so the page shows what "unavailable" looks like.</summary>
    private const string Missing = "DISTRIBUTEUTIL";

    private readonly List<PowerPlanDto> _plans;
    private readonly Dictionary<Guid, Dictionary<string, (uint Ac, uint Dc)>> _values = new();
    private readonly Dictionary<Guid, (DateTime Taken, Dictionary<string, (uint Ac, uint Dc)> Values)> _originals = new();
    private readonly HashSet<string> _unhidden = new();

    public StandInCpuTuning(List<PowerPlanDto> plans)
    {
        _plans = plans;
        foreach (var plan in plans) _values[plan.Id] = Defaults(plan.Id);
        // The user has already made Balanced keep its P-cores awake.
        _originals[StandInData.Balanced] = (DateTime.UtcNow.AddDays(-2), new(_values[StandInData.Balanced]));
        _values[StandInData.Balanced]["CPMINCORES1"] = (50, 25);
        _unhidden.Add("CPMINCORES1");
    }

    private static Guid Personality(Guid plan) => plan == StandInData.Ultimate ? StandInData.HighPerformance : plan;

    /// <summary>Windows' out-of-the-box values for the plan's personality.</summary>
    private static Dictionary<string, (uint Ac, uint Dc)> Defaults(Guid plan)
    {
        var personality = Personality(plan);
        bool high = personality == StandInData.HighPerformance, saver = personality == StandInData.PowerSaver;
        static (uint, uint) V(uint ac, uint dc) => (ac, dc);
        var v = new Dictionary<string, (uint, uint)>
        {
            ["CPMINCORES"] = high ? V(100, 100) : saver ? V(5, 5) : V(10, 5),
            ["CPMINCORES1"] = high ? V(100, 100) : saver ? V(5, 5) : V(10, 5),
            ["CPMAXCORES"] = (100, saver ? 50u : 100u),
            ["CPMAXCORES1"] = (100, saver ? 50u : 100u),
            ["PROCTHROTTLEMIN"] = high ? V(100, 100) : V(5, 5),
            ["PROCTHROTTLEMIN1"] = high ? V(100, 100) : V(5, 5),
            ["PROCTHROTTLEMAX"] = (100, saver ? 70u : 100u),
            ["PROCTHROTTLEMAX1"] = (100, saver ? 70u : 100u),
            ["PERFBOOSTMODE"] = high ? V(2, 2) : saver ? V(3, 0) : V(2, 3),
            ["PERFEPP"] = high ? V(0, 0) : saver ? V(70, 85) : V(25, 50),
            ["PERFEPP1"] = high ? V(0, 0) : saver ? V(70, 85) : V(25, 50),
            ["PERFINCTHRESHOLD"] = high ? V(30, 30) : V(60, 60),
            ["PERFINCTHRESHOLD1"] = high ? V(30, 30) : V(60, 60),
            ["PERFDECTHRESHOLD"] = high ? V(10, 10) : V(20, 20),
            ["PERFDECTHRESHOLD1"] = high ? V(10, 10) : V(20, 20),
            ["CPCONCURRENCY"] = V(95, 95),
            ["CPHEADROOM"] = high ? V(10, 10) : V(50, 50),
            ["CPDISTRIBUTION"] = V(90, 90),
            ["CPOVERUTIL"] = V(60, 60),
            ["CPINCREASETIME"] = V(3, 3),
            ["CPDECREASETIME"] = V(10, 10),
            ["CPINCREASEPOL"] = V(0, 0),
            ["CPDECREASEPOL"] = V(0, 0),
            ["CPPERF"] = (0, 0),
        };
        return v;
    }

    public CpuTuningDto Get(Guid plan, Guid activePlan)
    {
        var values = _values[plan];
        var defaults = Defaults(plan);
        var originals = _originals.TryGetValue(plan, out var o) ? o : default;
        var dto = new CpuTuningDto
        {
            PlanId = plan,
            PlanName = _plans.First(p => p.Id == plan).Name,
            IsActivePlan = plan == activePlan,
            Plans = _plans,
            IsHybrid = true,
            HasOriginals = originals.Values != null,
            OriginalsTakenUtc = originals.Values != null ? originals.Taken : null,
        };
        foreach (var s in CpuTuningCatalog.Settings)
        {
            bool available = s.Key != Missing;
            var current = available ? values[s.Key] : default;
            var original = originals.Values != null && originals.Values.TryGetValue(s.Key, out var ov) ? ov : ((uint, uint)?)null;
            dto.Settings.Add(new CpuTuningSettingDto
            {
                Key = s.Key, Id = s.Id, Group = s.Group, Name = s.Name, Description = s.Description,
                EfficiencyClass = s.EfficiencyClass, Kind = s.Kind, Min = s.Min, Max = s.Max, Unit = s.Unit,
                Choices = s.Choices?.ToList() ?? new(),
                Available = available,
                UnavailableReason = available ? null : "Windows on this PC doesn't have this setting.",
                Hidden = !s.Key.StartsWith("PROCTHROTTLE", StringComparison.Ordinal) && !_unhidden.Contains(s.Key),
                Ac = current.Ac, Dc = current.Dc,
                DefaultAc = available ? defaults[s.Key].Ac : null, DefaultDc = available ? defaults[s.Key].Dc : null,
                OriginalAc = original?.Item1, OriginalDc = original?.Item2,
            });
        }
        return dto;
    }

    /// <summary>Applies the argument; returns an error to fail with (nothing changed), or null and a summary.</summary>
    public string? Set(CpuTuningArgument argument, Guid plan, bool apply, out string summary)
    {
        summary = string.Empty;
        var values = _values[plan];
        var targets = new Dictionary<string, (uint Ac, uint Dc)>();
        if (argument.Preset is { Length: > 0 } preset)
        {
            switch (preset)
            {
                case CpuTuningPresets.DisableParking:
                    foreach (var key in new[] { "CPMINCORES", "CPMINCORES1", "CPMAXCORES", "CPMAXCORES1" }) targets[key] = (100, 100);
                    break;
                case CpuTuningPresets.BalancedParking:
                    foreach (var (key, v) in Defaults(StandInData.Balanced))
                        if (CpuTuningCatalog.Find(key)!.IsParking) targets[key] = v;
                    break;
                case CpuTuningPresets.WindowsDefaults:
                    foreach (var (key, v) in Defaults(plan)) targets[key] = v;
                    break;
                case CpuTuningPresets.Revert:
                    if (!_originals.TryGetValue(plan, out var o)) return "ProGuard hasn't changed this power plan, so there is nothing to revert.";
                    foreach (var (key, v) in o.Values) targets[key] = v;
                    break;
                default:
                    return $"Unknown preset '{preset}'.";
            }
            summary = $"Applied {CpuTuningPresets.Describe(preset)} to the {_plans.First(p => p.Id == plan).Name} power plan.";
        }
        else
        {
            if (argument.Values.Count == 0) return "Name at least one setting to change, or a preset.";
            foreach (var value in argument.Values)
            {
                var s = CpuTuningCatalog.Find(value.Key);
                if (s == null) return $"Unknown CPU setting '{value.Key}'.";
                if (s.Key == Missing) return $"{s.Key} can't be changed: Windows on this PC doesn't have this setting.";
                var now = targets.TryGetValue(s.Key, out var pending) ? pending : values[s.Key];
                var next = (value.Ac ?? now.Ac, value.Dc ?? now.Dc);
                foreach (var v in new[] { next.Item1, next.Item2 })
                {
                    if (s.Kind == CpuTuningKind.Choice ? s.Choices!.All(c => c.Value != v) : v < s.Min || v > s.Max)
                        return $"{s.Key} must be between {s.Min} and {s.Max}.";
                }
                targets[s.Key] = next;
            }
            summary = $"Changed {string.Join(", ", targets.Keys)} on the {_plans.First(p => p.Id == plan).Name} power plan.";
        }

        if (!apply) return null;
        if (argument.Preset == CpuTuningPresets.Revert)
        {
            _originals.Remove(plan);
            if (_originals.Count == 0) _unhidden.Clear();
        }
        else
        {
            if (!_originals.ContainsKey(plan)) _originals[plan] = (DateTime.UtcNow, new(values));
            foreach (var key in targets.Keys.Where(k => values[k] != targets[k])) _unhidden.Add(key);
        }
        foreach (var (key, v) in targets) values[key] = v;
        return null;
    }

    /// <summary>
    /// Which logical processors the active plan leaves parked at this load: each kind of core keeps at least its min
    /// unparked share, unparks more as load rises, and never more than its max share. The highest-numbered park first.
    /// </summary>
    public List<bool> Parked(Guid activePlan, double load)
    {
        var values = _values[activePlan];
        var parked = new List<bool>(StandInData.Logical);
        void Class(int first, int count, string min, string max)
        {
            int floor = (int)Math.Ceiling(count * values[min].Ac / 100.0);
            int ceiling = (int)Math.Ceiling(count * values[max].Ac / 100.0);
            int wanted = (int)Math.Ceiling(count * Math.Clamp(load * 1.4, 0, 100) / 100.0);
            int unparked = Math.Clamp(Math.Max(floor, wanted), 1, Math.Max(1, ceiling));
            for (int i = 0; i < count; i++) parked.Add(i >= unparked);
        }
        Class(0, 16, "CPMINCORES1", "CPMAXCORES1");  // P-core threads: efficiency class 1
        Class(16, 8, "CPMINCORES", "CPMAXCORES");    // E-cores: class 0
        return parked;
    }
}
