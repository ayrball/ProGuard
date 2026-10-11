using System.Collections.Generic;
using System.Linq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary>
/// <see cref="ProcessRule.CpuSetIds"/> via SetProcessDefaultCpuSets. Unlike affinity, CPU Sets are a soft preference
/// the scheduler can override, and they survive the process changing its own affinity. An empty list clears them.
/// </summary>
public sealed class CpuSetsApplier : ProcessSettingApplier<IReadOnlyList<uint>>
{
    private HashSet<uint>? _knownIds;

    public CpuSetsApplier(IProcessTuningNative native) : base(native) { }

    public override string Setting => "CPU Sets";

    protected override bool TryGetDesired(ProcessRule rule, out IReadOnlyList<uint> value, out string? error)
    {
        error = null;
        if (rule.CpuSetIds == null)
        {
            value = System.Array.Empty<uint>();
            return false;
        }

        if (!CpuSets.TryParseIds(rule.CpuSetIds, out value))
        {
            error = $"'{rule.CpuSetIds}' is not a list of CPU Set IDs";
            return true;
        }

        // Catch IDs from another machine (or a changed topology) before Windows rejects the whole call.
        if (value.Count > 0)
        {
            var known = _knownIds ??= Native.GetSystemCpuSets().Select(s => s.Id).ToHashSet();
            var unknown = value.Where(id => !known.Contains(id)).ToList();
            if (unknown.Count > 0) error = $"no CPU Set with ID {CpuSets.FormatIds(unknown)} on this machine";
        }
        return true;
    }

    protected override bool TryRead(int pid, out IReadOnlyList<uint> value) => Native.TryGetDefaultCpuSets(pid, out value);
    protected override void Write(int pid, IReadOnlyList<uint> value) => Native.SetDefaultCpuSets(pid, value);
    protected override bool Matches(IReadOnlyList<uint> current, IReadOnlyList<uint> desired) => current.ToHashSet().SetEquals(desired);
    protected override string Describe(IReadOnlyList<uint> value) => value.Count == 0 ? "none" : CpuSets.FormatIds(value);
}
