using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Instance Spread: splits the CPU cores across the running
/// instances of a process with plain affinity masks, re-splitting whenever an instance starts or exits.
/// <para>
/// An instance takes part once it reaches the rule's minimum CPU percent and then stays in until it exits, so
/// usage hovering around the threshold doesn't reshuffle every instance. Instances that haven't qualified keep
/// their own affinity. If something else changes a balanced instance's affinity, the spread lets it go and
/// splits the cores among the rest.
/// </para>
/// </summary>
public sealed class InstanceSpread : IDisposable
{
    public const int DefaultCoresPerInstance = 1;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<InstanceSpread> _logger;
    private readonly IProcessMonitor _monitor;
    private readonly IRuleStore _ruleStore;
    private readonly IRuleProcessAccess _access;
    private readonly Func<CpuTopology> _topology;
    private readonly TimeProvider _time;
    private ITimer? _timer;

    private readonly object _gate = new();
    private readonly Dictionary<int, Member> _members = new();

    private sealed class Member
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public required long OriginalMask { get; init; }
        public DateTime Start { get; init; }
        public bool Included { get; set; }
        public long? AppliedMask { get; set; }
        /// <summary>Someone else changed its affinity: no longer managed.</summary>
        public bool Released { get; set; }
    }

    public InstanceSpread(
        ILogger<InstanceSpread> logger,
        IProcessMonitor monitor,
        IRuleStore ruleStore,
        IRuleProcessAccess? access = null,
        Func<CpuTopology>? topology = null,
        TimeProvider? time = null)
    {
        _logger = logger;
        _monitor = monitor;
        _ruleStore = ruleStore;
        _access = access ?? new Win32RuleProcessAccess();
        _topology = topology ?? SystemInfo.GetCpuTopology;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Starts re-checking periodically, for instances whose CPU use crosses the minimum and for rule edits.</summary>
    public void Start()
    {
        _timer ??= _time.CreateTimer(_ => SafeTick(), null, CheckInterval, CheckInterval);
    }

    /// <summary>Stops re-checking and gives every balanced instance its original affinity back.</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_gate)
        {
            foreach (var pid in _members.Keys.ToList()) Release(pid, restore: true);
            _members.Clear();
        }
    }

    public void Dispose() => Stop();

    /// <summary>The mask the spread gave <paramref name="pid"/>, or null if it isn't balancing it.</summary>
    public long? GetAppliedMask(int pid)
    {
        lock (_gate) return _members.TryGetValue(pid, out var m) && !m.Released ? m.AppliedMask : null;
    }

    public void OnProcessStarted(ProcessInfo process, ProcessRule rule)
    {
        if (rule.InstanceSpreadMode == null) return;

        lock (_gate)
        {
            if (Adopt(process)) Rebalance(ProcessExtensions.NormalizeName(process.Name), rule);
        }
    }

    public void OnProcessExited(int pid)
    {
        lock (_gate)
        {
            if (!_members.Remove(pid, out var member)) return;
            if (member.Released || !member.Included) return; // the others' share is unchanged

            var rule = _ruleStore.GetProcessRule(member.Key);
            if (rule?.InstanceSpreadMode != null) Rebalance(member.Key, rule);
        }
    }

    /// <summary>
    /// Re-checks every balanced process against its current rule and CPU use, and picks up instances that were
    /// already running when balancing was turned on for their rule. Called by the timer; public for tests.
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            var balanced = _ruleStore.GetProcessRules()
                .Where(r => r.InstanceSpreadMode != null)
                .Select(r => ProcessExtensions.NormalizeName(r.ProcessName))
                .ToHashSet();
            if (balanced.Count > 0)
            {
                foreach (var process in _monitor.GetAllProcesses())
                {
                    if (!_members.ContainsKey(process.Pid) && balanced.Contains(ProcessExtensions.NormalizeName(process.Name)))
                        Adopt(process);
                }
            }

            foreach (var key in _members.Values.Select(m => m.Key).Distinct().ToList())
            {
                var rule = _ruleStore.GetProcessRule(key);
                if (rule?.InstanceSpreadMode != null)
                {
                    Rebalance(key, rule);
                    continue;
                }

                // Rule deleted or balancing turned off.
                foreach (var pid in _members.Where(m => m.Value.Key == key).Select(m => m.Key).ToList())
                {
                    Release(pid, restore: true);
                    _members.Remove(pid);
                }
            }
        }
    }

    private void SafeTick()
    {
        // Timer-thread entry point: an exception escaping it would take the engine down.
        try { Tick(); }
        catch (Exception ex) { _logger.LogError(ex, "Instance Spread check failed"); }
    }

    // Caller holds _gate. Returns true if the process is now a member.
    private bool Adopt(ProcessInfo process)
    {
        if (_members.ContainsKey(process.Pid)) return false;
        if (CriticalProcesses.IsCriticalName(process.Name)) return false;

        // Read now, after the rule's own affinity was applied: that is what the instance goes back to.
        if (_access.GetAffinityMask(process.Pid) is not long original || original == 0) return false;

        _members[process.Pid] = new Member
        {
            Key = ProcessExtensions.NormalizeName(process.Name),
            Name = process.Name,
            OriginalMask = original,
            Start = _access.GetStartTimeUtc(process.Pid) ?? _time.GetUtcNow().UtcDateTime,
        };
        return true;
    }

    // Caller holds _gate.
    private void Rebalance(string key, ProcessRule rule)
    {
        double minCpu = rule.InstanceSpreadMinCpuPercent ?? 0;
        var group = _members.Where(m => m.Value.Key == key && !m.Value.Released).ToList();

        foreach (var (pid, member) in group)
        {
            if (member.Included) continue;
            double cpu = _monitor.GetProcess(pid)?.CpuPercent ?? 0;
            if (cpu >= minCpu) member.Included = true;
        }

        // Someone else changed a balanced instance's affinity: theirs wins.
        foreach (var (pid, member) in group.Where(m => m.Value.AppliedMask != null))
        {
            var current = _access.GetAffinityMask(pid);
            if (current == null || current == member.AppliedMask) continue;
            _logger.LogInformation("Instance Spread: affinity of {Name} ({Pid}) was changed elsewhere, no longer balancing it", member.Name, pid);
            member.Released = true;
        }

        var included = group
            .Where(m => m.Value.Included && !m.Value.Released)
            .OrderBy(m => m.Value.Start)
            .ThenBy(m => m.Key)
            .ToList();
        if (included.Count == 0) return;

        var masks = PlanMasks(_topology(), rule.InstanceSpreadMode!.Value, included.Count,
            rule.InstanceSpreadCoresPerInstance ?? DefaultCoresPerInstance,
            rule.InstanceSpreadAvoidSmt, rule.InstanceSpreadReservedCores ?? 0);
        if (masks.Count == 0) return;

        for (int i = 0; i < included.Count; i++)
        {
            var (pid, member) = included[i];
            if (member.AppliedMask == masks[i]) continue;

            if (_access.SetAffinityMask(pid, masks[i]))
            {
                member.AppliedMask = masks[i];
                _logger.LogInformation("Instance Spread: {Name} ({Pid}) instance {Index} of {Count} set to affinity 0x{Mask:X}",
                    member.Name, pid, i + 1, included.Count, masks[i]);
            }
        }
    }

    // Caller holds _gate. Puts the original affinity back unless someone else has changed it since.
    private void Release(int pid, bool restore)
    {
        if (!_members.TryGetValue(pid, out var member) || member.AppliedMask == null || member.Released) return;
        member.Released = true;
        if (!restore) return;

        if (_access.GetAffinityMask(pid) == member.AppliedMask && _access.SetAffinityMask(pid, member.OriginalMask))
            _logger.LogInformation("Instance Spread: {Name} ({Pid}) affinity restored to 0x{Mask:X}", member.Name, pid, member.OriginalMask);
    }

    /// <summary>
    /// The affinity mask for each of <paramref name="instanceCount"/> instances, oldest first. The usable cores
    /// are every logical processor, minus SMT siblings if <paramref name="avoidSmt"/>, minus the lowest
    /// <paramref name="reservedCores"/> (at least one core always stays usable).
    /// <list type="bullet">
    /// <item><see cref="InstanceSpreadMode.EqualCoresPerInstance"/>: consecutive equal blocks, the first
    /// instances getting one extra core when the split is uneven; with more instances than cores, instance i
    /// gets core i mod cores.</item>
    /// <item><see cref="InstanceSpreadMode.FixedCoresPerInstance"/>: <paramref name="coresPerInstance"/>
    /// consecutive cores each, wrapping round to the first core when they run out.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<long> PlanMasks(CpuTopology topology, InstanceSpreadMode mode, int instanceCount,
        int coresPerInstance, bool avoidSmt, int reservedCores)
    {
        if (instanceCount <= 0) return Array.Empty<long>();

        var cores = UsableCores(topology, avoidSmt);
        if (cores.Count == 0) return Array.Empty<long>();
        cores = cores.Skip(Math.Clamp(reservedCores, 0, cores.Count - 1)).ToList();

        int c = cores.Count;
        var masks = new long[instanceCount];

        if (mode == InstanceSpreadMode.FixedCoresPerInstance)
        {
            int n = Math.Clamp(coresPerInstance, 1, c);
            for (int i = 0; i < instanceCount; i++)
                for (int j = 0; j < n; j++)
                    masks[i] |= 1L << cores[(i * n + j) % c];
            return masks;
        }

        if (instanceCount > c)
        {
            for (int i = 0; i < instanceCount; i++) masks[i] = 1L << cores[i % c];
            return masks;
        }

        int each = c / instanceCount, extra = c % instanceCount, next = 0;
        for (int i = 0; i < instanceCount; i++)
        {
            int take = each + (i < extra ? 1 : 0);
            for (int j = 0; j < take; j++) masks[i] |= 1L << cores[next++];
        }
        return masks;
    }

    private static List<int> UsableCores(CpuTopology topology, bool avoidSmt)
    {
        if (topology.Cores.Count > 0)
        {
            return topology.Cores
                .Where(core => core.LogicalIndex < 64 && !(avoidSmt && core.IsSmtSibling))
                .Select(core => core.LogicalIndex)
                .OrderBy(i => i)
                .ToList();
        }

        long mask = avoidSmt && topology.PhysicalCoresMask != 0 ? topology.PhysicalCoresMask : topology.AllCoresMask;
        return Enumerable.Range(0, 64).Where(bit => (mask & (1L << bit)) != 0).ToList();
    }
}
