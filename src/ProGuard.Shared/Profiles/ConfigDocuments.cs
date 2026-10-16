using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ProGuard.Shared.Contracts;

namespace ProGuard.Shared.Profiles;

/// <summary>
/// The config file format and what is worked out from it, shared by the engine and the preview tool's stand-in:
/// reading and writing (with the schema version check), the differences between two configurations, and which values
/// only belong to the machine a document was saved on.
/// </summary>
public static class ConfigDocuments
{
    /// <summary>The largest document accepted, in characters. A real one with hundreds of rules is a few hundred KB.</summary>
    public const int MaxDocumentLength = 512 * 1024;

    /// <summary>The name of an export of the configuration in force rather than of a saved profile.</summary>
    public const string CurrentName = "Current configuration";

    /// <summary>Suggested file extension (the file is plain JSON).</summary>
    public const string FileExtension = ".proguard.json";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // Enums by name, so the file reads well and doesn't depend on member order; numbers are still accepted.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The file form: indented JSON, enums by name.</summary>
    public static string Serialize(ConfigDocument document) => JsonSerializer.Serialize(document, FileOptions);

    /// <summary>
    /// Reads a config file. Returns null and sets <paramref name="error"/> (fit to show) when the text isn't a ProGuard
    /// config, is too large, or has a schema version this build can't read. Older versions are upgraded here.
    /// </summary>
    public static ConfigDocument? Parse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "The file is empty."; return null; }
        if (text.Length > MaxDocumentLength) { error = $"The file is too large to be a ProGuard config (over {MaxDocumentLength / 1024} KB)."; return null; }

        // The format and version are checked before the rest is read: a newer version may not fit this build's types.
        int version;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGet(root, nameof(ConfigDocument.Format), out var format) || format.ValueKind != JsonValueKind.String
                || !string.Equals(format.GetString(), ConfigDocument.FormatName, StringComparison.Ordinal))
            {
                error = "This isn't a ProGuard config file.";
                return null;
            }
            if (!TryGet(root, nameof(ConfigDocument.SchemaVersion), out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out version) || version < 1)
            {
                error = "This ProGuard config has no valid schema version.";
                return null;
            }
        }
        catch (JsonException ex)
        {
            error = "This isn't a ProGuard config file: it isn't valid JSON (" + ex.Message + ").";
            return null;
        }

        if (version > ConfigDocument.CurrentSchemaVersion)
        {
            error = $"This config was made by a newer version of ProGuard (format version {version}; this version reads up to {ConfigDocument.CurrentSchemaVersion}). Update ProGuard to import it.";
            return null;
        }

        ConfigDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ConfigDocument>(text, FileOptions);
        }
        catch (JsonException ex)
        {
            error = "The ProGuard config is damaged: " + ex.Message;
            return null;
        }
        if (document == null) { error = "The file is empty."; return null; }

        // Version 1 is the first; an upgrade from version N to N+1 goes here when there is one.
        document.SchemaVersion = ConfigDocument.CurrentSchemaVersion;
        document.ProcessRules ??= new();
        document.WatchdogRules ??= new();
        document.Settings ??= new();
        document.ProcessRules.RemoveAll(r => r == null);
        document.WatchdogRules.RemoveAll(r => r == null);
        foreach (var rule in document.ProcessRules) rule.Id = 0;
        foreach (var rule in document.WatchdogRules) rule.Id = 0;
        return document;
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        }
        value = default;
        return false;
    }

    /// <summary>A deep copy.</summary>
    public static ConfigDocument Clone(ConfigDocument document) =>
        JsonSerializer.Deserialize<ConfigDocument>(JsonSerializer.Serialize(document, FileOptions), FileOptions)!;

    /// <summary>The key rules are matched by: the file name, without ".exe", in lower case (as the engine matches them).</summary>
    public static string RuleKey(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return string.Empty;
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    // ------------------------------------------------------------------ differences

    /// <summary>What replacing <paramref name="current"/> with <paramref name="incoming"/> changes, rules first, then settings.</summary>
    public static List<ConfigChangeDto> Diff(ConfigDocument current, ConfigDocument incoming)
    {
        var changes = new List<ConfigChangeDto>();

        var before = current.ProcessRules.GroupBy(r => RuleKey(r.ProcessName)).ToDictionary(g => g.Key, g => g.First());
        var after = incoming.ProcessRules.GroupBy(r => RuleKey(r.ProcessName)).ToDictionary(g => g.Key, g => g.First());
        foreach (var (key, rule) in after.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(key, out var old))
                changes.Add(new ConfigChangeDto { Kind = ConfigChangeKind.Added, Area = "Process rule", Name = rule.ProcessName.Trim(), Detail = DescribeRule(rule) });
            else if (ChangedProperties(old, rule) is { Count: > 0 } fields)
                changes.Add(new ConfigChangeDto { Kind = ConfigChangeKind.Changed, Area = "Process rule", Name = rule.ProcessName.Trim(), Detail = string.Join(", ", fields) });
        }
        foreach (var (key, rule) in before.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!after.ContainsKey(key))
                changes.Add(new ConfigChangeDto { Kind = ConfigChangeKind.Removed, Area = "Process rule", Name = rule.ProcessName.Trim(), Detail = DescribeRule(rule) });
        }

        // Watchdog rules have no natural key (a program may have several), so they are matched whole.
        var remaining = current.WatchdogRules.Select(WatchdogKey).ToList();
        var added = new List<WatchdogRuleDto>();
        foreach (var rule in incoming.WatchdogRules)
        {
            if (!remaining.Remove(WatchdogKey(rule))) added.Add(rule);
        }
        var unmatched = current.WatchdogRules.Where(r => remaining.Contains(WatchdogKey(r))).ToList();
        foreach (var rule in added)
            changes.Add(new ConfigChangeDto { Kind = ConfigChangeKind.Added, Area = "Watchdog rule", Name = rule.TargetProcessName.Trim(), Detail = DescribeWatchdog(rule) });
        foreach (var rule in unmatched)
            changes.Add(new ConfigChangeDto { Kind = ConfigChangeKind.Removed, Area = "Watchdog rule", Name = rule.TargetProcessName.Trim(), Detail = DescribeWatchdog(rule) });

        foreach (var property in SettingProperties)
        {
            var old = property.GetValue(current.Settings);
            var now = property.GetValue(incoming.Settings);
            if (!Equals(old, now))
                changes.Add(new ConfigChangeDto { Kind = ConfigChangeKind.Changed, Area = "Setting", Name = Words(property.Name), Detail = $"{Show(old)} → {Show(now)}" });
        }
        return changes;
    }

    private static readonly PropertyInfo[] SettingProperties = typeof(EngineSettingsDto).GetProperties().Where(p => p.CanRead && p.CanWrite).ToArray();
    private static readonly PropertyInfo[] RuleProperties = typeof(ProcessRuleDto).GetProperties().Where(p => p.CanRead && p.CanWrite && p.Name != nameof(ProcessRuleDto.Id)).ToArray();

    private static List<string> ChangedProperties(ProcessRuleDto old, ProcessRuleDto now) =>
        RuleProperties.Where(p => !Equals(Normalize(p, p.GetValue(old)), Normalize(p, p.GetValue(now))))
            .Select(p => $"{Words(p.Name)} {Show(p.GetValue(old))} → {Show(p.GetValue(now))}")
            .ToList();

    private static object? Normalize(PropertyInfo property, object? value) =>
        property.Name == nameof(ProcessRuleDto.ProcessName) && value is string s ? RuleKey(s) : value;

    /// <summary>True when the two rules do the same thing (ids and the case of the name aside).</summary>
    public static bool SameRule(ProcessRuleDto a, ProcessRuleDto b) => ChangedProperties(a, b).Count == 0;

    /// <summary>A Watchdog rule as text that is equal for two rules exactly when they do the same thing.</summary>
    public static string WatchdogKey(WatchdogRuleDto rule)
    {
        var copy = JsonSerializer.Deserialize<WatchdogRuleDto>(JsonSerializer.Serialize(rule))!;
        copy.Id = 0;
        copy.TargetProcessName = RuleKey(copy.TargetProcessName);
        copy.ActionArgument = string.IsNullOrWhiteSpace(copy.ActionArgument) ? null : copy.ActionArgument.Trim();
        return JsonSerializer.Serialize(copy);
    }

    /// <summary>A short account of what a rule does, for the change list.</summary>
    public static string DescribeRule(ProcessRuleDto rule)
    {
        var parts = new List<string>();
        if (rule.DisallowExecution) parts.Add("not allowed to run");
        if (rule.PriorityClass is int p) parts.Add("priority " + PriorityName(p));
        if (rule.AffinityMask is long mask) parts.Add("cores " + Mask(mask));
        if (rule.CpuLimitPercent is double cap) parts.Add($"CPU capped at {cap.ToString("0.#", CultureInfo.InvariantCulture)}%");
        if (rule.MaxInstances is int max) parts.Add($"at most {max} instance{(max == 1 ? "" : "s")}");
        if (rule.IoPriority is int io) parts.Add($"I/O priority {io}");
        if (rule.MemoryPriority is int memory) parts.Add($"memory priority {memory}");
        if (rule.EfficiencyMode is bool eco) parts.Add(eco ? "efficiency mode" : "efficiency mode off");
        if (!string.IsNullOrEmpty(rule.CpuSetIds)) parts.Add("CPU Sets " + rule.CpuSetIds);
        if (rule.PowerProfileGuid != null) parts.Add("power plan");
        if (rule.ExcludeFromProBalance) parts.Add("excluded from ProBalance");
        if (rule.KeepRunning) parts.Add("kept running");
        return parts.Count == 0 ? "no settings" : string.Join(", ", parts);
    }

    /// <summary>"CPU above 70 for 30 s → SetPriority Idle".</summary>
    public static string DescribeWatchdog(WatchdogRuleDto rule)
    {
        var threshold = rule.ThresholdValue.ToString("0.##", CultureInfo.InvariantCulture);
        var condition = rule.ConditionType switch
        {
            WatchdogTrigger.CpuAbove => $"CPU above {threshold}%",
            WatchdogTrigger.RamAbove => $"memory above {threshold} MB",
            WatchdogTrigger.RunningLongerThan => $"running longer than {threshold} s",
            WatchdogTrigger.InstanceCountAbove => $"more than {threshold} instances",
            _ => rule.ConditionType.ToString(),
        };
        var action = Words(rule.ActionType.ToString());
        if (!string.IsNullOrWhiteSpace(rule.ActionArgument)) action += " " + rule.ActionArgument.Trim();
        return $"{condition}{(rule.DurationSeconds > 0 ? $" for {rule.DurationSeconds} s" : "")} → {action}{(rule.IsEnabled ? "" : " (off)")}";
    }

    private static string PriorityName(int value) => value switch
    {
        64 => "Idle", 16384 => "Below normal", 32 => "Normal", 32768 => "Above normal", 128 => "High", 256 => "Realtime",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static string Show(object? value) => value switch
    {
        null => "default",
        string s when s.Length == 0 => "empty",
        string s => s.Length > 60 ? s[..57] + "…" : s,
        bool b => b ? "on" : "off",
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>"ProBalanceCpuThreshold" → "ProBalance CPU threshold".</summary>
    public static string Words(string name)
    {
        var words = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ").Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = words[i] switch
            {
                "Cpu" => "CPU", "Io" => "I/O", "Ram" => "RAM", "Mb" or "MB" => "MB", "Ms" => "ms", "Guid" => "GUID", "Smt" => "SMT",
                _ when i == 0 => words[i],
                _ => words[i].ToLowerInvariant(),
            };
        }
        return string.Join(' ', words).Replace("Pro balance", "ProBalance").Replace("Smart trim", "SmartTrim");
    }

    // ------------------------------------------------------------------ machine-specific values

    /// <summary>
    /// The document as it can be applied on <paramref name="here"/>: values that only work on the machine it was saved
    /// on are left out, and each one is listed in <paramref name="flags"/>. The document itself is not changed.
    /// <list type="bullet">
    /// <item>CPU Set IDs, unless the document comes from a machine with the same CPU Set layout and every ID exists here.</item>
    /// <item>Affinity masks that name a logical processor this machine doesn't have (rules, ProBalance's restraint cores,
    /// Watchdog "set affinity" rules, which are turned off instead since they can't do without one).</item>
    /// <item>Power plan GUIDs of plans that aren't installed here (the setting goes back to its default).</item>
    /// </list>
    /// </summary>
    public static ConfigDocument FitToMachine(ConfigDocument document, MachineDescription here, out List<MachineSpecificValueDto> flags)
    {
        var fitted = Clone(document);
        var found = new List<MachineSpecificValueDto>();
        var source = document.Machine;
        int logical = Math.Max(1, here.LogicalProcessors);
        long allowed = logical >= 64 ? -1L : (1L << logical) - 1;
        var installed = here.PowerPlans.Select(p => p.Id).ToHashSet();
        var cpuSetIds = CpuSetIds(here.CpuSetLayout);
        bool sameLayout = source?.CpuSetLayout != null && string.Equals(source.CpuSetLayout, here.CpuSetLayout, StringComparison.Ordinal);

        string PlanName(string guid) =>
            source?.PowerPlans.FirstOrDefault(p => string.Equals(p.Id.ToString(), guid, StringComparison.OrdinalIgnoreCase))?.Name is { Length: > 0 } name
                ? $"{name} ({guid})" : guid;
        bool Missing(string? guid) => !string.IsNullOrWhiteSpace(guid) && (!Guid.TryParse(guid, out var id) || !installed.Contains(id));
        string Beyond(long mask) => $"names processors this PC doesn't have (it has {logical})";

        foreach (var rule in fitted.ProcessRules)
        {
            var where = "Process rule " + rule.ProcessName.Trim();
            if (rule.AffinityMask is long mask && (mask & ~allowed) != 0)
            {
                found.Add(new() { Where = where, Setting = "Cores (affinity)", Value = Mask(mask), Reason = Beyond(mask), Outcome = "Left out: the rule doesn't change the cores" });
                rule.AffinityMask = null;
            }
            if (!string.IsNullOrWhiteSpace(rule.CpuSetIds))
            {
                var ids = CpuSetIds(rule.CpuSetIds);
                string? reason = ids == null || ids.Any(id => !cpuSetIds!.Contains(id)) ? "names CPU Sets this PC doesn't have"
                    : !sameLayout ? "CPU Set IDs belong to the machine they were picked on, and this PC's CPU Sets are laid out differently"
                    : null;
                if (reason != null)
                {
                    found.Add(new() { Where = where, Setting = "CPU Sets", Value = rule.CpuSetIds, Reason = reason, Outcome = "Left out: the rule doesn't change CPU Sets" });
                    rule.CpuSetIds = null;
                }
            }
            if (Missing(rule.PowerProfileGuid))
            {
                found.Add(new() { Where = where, Setting = "Power plan", Value = PlanName(rule.PowerProfileGuid!), Reason = "isn't installed on this PC", Outcome = "Left out: the rule doesn't switch plans" });
                rule.PowerProfileGuid = null;
            }
        }

        foreach (var rule in fitted.WatchdogRules)
        {
            if (rule.ActionType != WatchdogResponse.SetAffinity || !TryParseMask(rule.ActionArgument, out var mask) || (mask & ~allowed) == 0) continue;
            found.Add(new()
            {
                Where = "Watchdog rule " + rule.TargetProcessName.Trim(), Setting = "Set cores", Value = Mask(mask), Reason = Beyond(mask),
                Outcome = rule.IsEnabled ? "The rule is imported turned off" : "The rule stays off",
            });
            rule.IsEnabled = false;
        }

        var s = fitted.Settings;
        if ((s.ProBalanceRestraintAffinity & ~allowed) != 0)
        {
            found.Add(new() { Where = "Settings", Setting = "ProBalance restraint cores", Value = Mask(s.ProBalanceRestraintAffinity), Reason = Beyond(s.ProBalanceRestraintAffinity), Outcome = "Left out: no cores are picked" });
            s.ProBalanceRestraintAffinity = 0;
        }
        foreach (var (name, get, set, fallback) in new (string, Func<string?>, Action<string?>, string)[]
        {
            ("IdleSaver plan", () => s.IdlePowerPlanGuid, v => s.IdlePowerPlanGuid = v, "Power saver"),
            ("Performance Mode plan", () => s.PerformanceModePlanGuid, v => s.PerformanceModePlanGuid = v, "High performance"),
            ("Load switcher low-load plan", () => s.LoadSwitcherLowPlanGuid, v => s.LoadSwitcherLowPlanGuid = v, "Balanced"),
            ("Load switcher high-load plan", () => s.LoadSwitcherHighPlanGuid, v => s.LoadSwitcherHighPlanGuid = v, "High performance"),
            ("Plan at engine start", () => s.StartWithPowerPlanGuid, v => s.StartWithPowerPlanGuid = v, "leave the plan alone"),
        })
        {
            if (!Missing(get())) continue;
            found.Add(new() { Where = "Settings", Setting = name, Value = PlanName(get()!), Reason = "isn't installed on this PC", Outcome = $"Left out: uses the default ({fallback})" });
            set(null);
        }

        fitted.Machine = here;
        flags = found;
        return fitted;
    }

    /// <summary>"id:core:class,…" for a machine's CPU Sets, in the form <see cref="MachineDescription.CpuSetLayout"/> holds.</summary>
    public static string CpuSetLayout(IEnumerable<(uint Id, int Core, int EfficiencyClass)> sets) =>
        string.Join(",", sets.OrderBy(s => s.Id).Select(s => FormattableString.Invariant($"{s.Id}:{s.Core}:{s.EfficiencyClass}")));

    /// <summary>The IDs in a layout or a rule's ID list (the first number of each item); null when unreadable.</summary>
    private static HashSet<uint>? CpuSetIds(string? text)
    {
        var ids = new HashSet<uint>();
        if (string.IsNullOrWhiteSpace(text)) return ids;
        foreach (var item in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var id = item.Split(':')[0];
            if (!uint.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return null;
            ids.Add(value);
        }
        return ids;
    }

    private static string Mask(long mask) => "0x" + mask.ToString("X", CultureInfo.InvariantCulture);

    private static bool TryParseMask(string? text, out long mask)
    {
        mask = 0;
        var t = text?.Trim() ?? string.Empty;
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask)
            : long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask);
    }
}
