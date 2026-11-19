using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProGuard.Core.Models;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary>
/// <see cref="ProcessRule.GpuPreference"/>: the per-user, per-executable graphics preference that Settings &gt; Display
/// &gt; Graphics writes, i.e. the value <c>&lt;full exe path&gt; = "GpuPreference=N;"</c> under
/// <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>. DXGI reads it when the app enumerates adapters, so it
/// takes effect from the next launch.
/// <para>
/// The engine runs as LocalSystem, whose HKCU is the SYSTEM profile, so it writes to <c>HKU\&lt;SID&gt;</c> of the user
/// the process runs as: that is exactly the HKCU the app itself reads. Only if the owner can't be read does it fall
/// back to the user at the console. Other values in the same string (e.g. "SwapEffectUpgradeEnable=1;") are kept.
/// </para>
/// </summary>
public sealed class GpuPreferenceApplier : IProcessRuleApplier
{
    private const string Key = "GpuPreference";
    private readonly IProcessTuningNative _native;
    private readonly IGpuPreferenceLedger? _ledger;

    /// <param name="ledger">Where each write is recorded, so it can be taken back out (<see cref="GpuPreferenceService"/>).</param>
    internal GpuPreferenceApplier(IProcessTuningNative native, IGpuPreferenceLedger? ledger)
    {
        _native = native;
        _ledger = ledger;
    }

    public GpuPreferenceApplier(IProcessTuningNative native) : this(native, null) { }

    public string Setting => "GPU preference";

    // The registry value is compared every time: rewriting an identical value would only churn the user's hive.
    public RuleApplyResult Apply(ProcessRule rule, ProcessTarget target, bool onlyIfChanged)
    {
        if (rule.GpuPreference is not int preference) return Result(RuleApplyOutcome.NotConfigured);
        if (!ProcessRuleOptions.IsValid(ProcessRuleOptions.GpuPreferences, preference))
            return Result(RuleApplyOutcome.Failed, $"{preference} is out of range (0..{ProcessRuleOptions.GpuPreferences.Count - 1})");

        try
        {
            var path = string.IsNullOrEmpty(target.FilePath) ? _native.GetImagePath(target.Pid) : target.FilePath;
            if (string.IsNullOrEmpty(path)) return Result(RuleApplyOutcome.Failed, "the process's full path can't be read");

            var sid = _native.GetProcessOwnerSid(target.Pid) ?? _native.GetConsoleUserSid();
            if (sid == null) return Result(RuleApplyOutcome.Failed, "neither the process owner nor a console user could be determined");

            var existing = _native.ReadUserGpuPreference(sid, path);
            var label = ProcessRuleOptions.Label(ProcessRuleOptions.GpuPreferences, preference);
            if (TryParse(existing, out var current) && current == preference)
                return Result(RuleApplyOutcome.Unchanged, $"{label} for {sid}");

            // Recorded first: a write that lands without its record could never be taken back out.
            _ledger?.Record(sid, path, preference, TryParse(existing, out var found) ? found : null);
            _native.WriteUserGpuPreference(sid, path, Merge(existing, preference));
            return Result(RuleApplyOutcome.Applied, $"{label} for {sid} at {path} (takes effect on next launch)");
        }
        catch (Exception ex)
        {
            return Result(RuleApplyOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Reads N from a "GpuPreference=N;..." value.</summary>
    public static bool TryParse(string? value, out int preference)
    {
        preference = 0;
        var entry = Split(value).FirstOrDefault(e => e.Key.Equals(Key, StringComparison.OrdinalIgnoreCase));
        return entry.Key != null && int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out preference);
    }

    /// <summary>Sets GpuPreference in an existing "k=v;k=v;" value, keeping its other entries.</summary>
    public static string Merge(string? existing, int preference)
    {
        var entries = Split(existing).Where(e => !e.Key.Equals(Key, StringComparison.OrdinalIgnoreCase)).ToList();
        entries.Insert(0, new KeyValuePair<string, string>(Key, preference.ToString(CultureInfo.InvariantCulture)));
        return string.Concat(entries.Select(e => $"{e.Key}={e.Value};"));
    }

    /// <summary>The value without its GpuPreference entry, keeping the others; null when nothing else is left.</summary>
    public static string? Strip(string? existing)
    {
        var rest = Split(existing).Where(e => !e.Key.Equals(Key, StringComparison.OrdinalIgnoreCase)).ToList();
        return rest.Count == 0 ? null : string.Concat(rest.Select(e => $"{e.Key}={e.Value};"));
    }

    private static IEnumerable<KeyValuePair<string, string>> Split(string? value)
    {
        if (string.IsNullOrEmpty(value)) yield break;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) yield return new KeyValuePair<string, string>(part[..eq].Trim(), part[(eq + 1)..].Trim());
        }
    }

    private RuleApplyResult Result(RuleApplyOutcome outcome, string? detail = null) => new(Setting, outcome, detail);
}
