using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Core.Native;

namespace ProGuard.Engine.Services;

/// <summary>
/// The one-off process actions the thin client adds to the original set (priority, affinity, CPU cap, suspend,
/// terminate): efficiency mode, I/O and memory priority, ending a whole process tree. They go through the same native
/// wrappers (<see cref="IProcessTuningNative"/>) the persistent rules use. Authorization is the dispatcher's job; the
/// only caller check here is for the descendants of a tree, which the dispatcher can't know in advance.
/// </summary>
public sealed class ProcessActionService
{
    private readonly ILogger<ProcessActionService> _logger;
    private readonly IProcessTuningNative _native;
    private readonly IProcessMonitor _monitor;
    private readonly IProcessController _controller;

    public ProcessActionService(ILogger<ProcessActionService> logger, IProcessTuningNative native, IProcessMonitor monitor, IProcessController controller)
    {
        _logger = logger;
        _native = native;
        _monitor = monitor;
        _controller = controller;
    }

    public void SetEfficiencyMode(int pid, bool enabled)
    {
        _native.SetEfficiencyMode(pid, enabled);
        _logger.LogInformation("Efficiency mode of {Pid} set {State}", pid, enabled ? "on" : "off");
    }

    public void SetIoPriority(int pid, int priority)
    {
        if (!ProcessRuleOptions.IsValid(ProcessRuleOptions.IoPriorities, priority))
            throw new ArgumentException("I/O priority must be 0 (Very Low) to 3 (High)");
        _native.SetIoPriority(pid, priority);
        _logger.LogInformation("I/O priority of {Pid} set to {Priority}", pid, ProcessRuleOptions.Label(ProcessRuleOptions.IoPriorities, priority));
    }

    public void SetMemoryPriority(int pid, int priority)
    {
        if (!ProcessRuleOptions.IsValid(ProcessRuleOptions.MemoryPriorities, priority, ProcessRuleOptions.MinMemoryPriority))
            throw new ArgumentException("Memory priority must be 1 (Very Low) to 5 (Normal)");
        _native.SetMemoryPriority(pid, priority);
        _logger.LogInformation("Memory priority of {Pid} set to {Priority}", pid, ProcessRuleOptions.Label(ProcessRuleOptions.MemoryPriorities, priority));
    }

    /// <summary>
    /// Ends <paramref name="rootPid"/> and every process descended from it that <paramref name="caller"/> may end,
    /// children before parents. Never the System process or the engine; a descendant that is gone or refuses is
    /// skipped, but failing to end the root is an error. Returns how many processes were ended.
    /// </summary>
    public int TerminateTree(int rootPid, IIpcCaller caller)
    {
        if (rootPid <= 4 || rootPid == Environment.ProcessId)
            throw new InvalidOperationException($"Refusing to end process {rootPid}");
        var root = _monitor.GetProcess(rootPid);
        var descendants = DescendantsDeepestFirst(rootPid, root).Where(p => !IsProtected(p)).ToList();

        // For a standard user each descendant is pinned before it is checked and kept pinned until it has been ended, so
        // the check and the kill reach the same process (see ProcessPin); one that is gone, or whose id now belongs to
        // a process that started later than the one listed, is left out.
        var pins = new List<IDisposable>();
        try
        {
            if (!caller.IsAdministrator && descendants.Count > 0)
            {
                var pinned = new List<ProcessInfo>();
                foreach (var p in descendants)
                {
                    if (PinProcess(p.Pid) is not { } pin) continue;
                    if (p.StartTime != DateTime.MinValue && pin.StartTimeUtc is { } started && started != p.StartTime)
                    {
                        pin.Dispose();
                        continue;
                    }
                    pins.Add(pin);
                    pinned.Add(p);
                }
                var allowed = caller.FilterOpenable(pinned.Select(p => (p.Pid, p.StartTime)).ToList(), ProcessAccessFlags.Terminate);
                descendants = pinned.Where(p => allowed.Contains(p.Pid)).ToList();
            }

            var ended = 0;
            foreach (var child in descendants)
            {
                try
                {
                    _controller.Terminate(child.Pid);
                    ended++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not end {Name} ({Pid}) with its tree", child.Name, child.Pid);
                }
            }

            _controller.Terminate(rootPid); // throws when it is gone or refused: the caller asked for this one above all
            return ended + 1;
        }
        finally
        {
            foreach (var pin in pins) pin.Dispose();
        }
    }

    /// <summary>A pinned process with the start time Windows reports for it.</summary>
    internal interface IPinnedProcess : IDisposable
    {
        DateTime? StartTimeUtc { get; }
    }

    private sealed class Win32Pin : IPinnedProcess
    {
        private readonly ProcessPin _pin;
        public Win32Pin(ProcessPin pin) => _pin = pin;
        public DateTime? StartTimeUtc => _pin.StartTimeUtc;
        public void Dispose() => _pin.Dispose();
    }

    /// <summary>Pins a descendant before it is checked. Tests of fake process trees replace it.</summary>
    internal Func<int, IPinnedProcess?> PinProcess { get; init; } = pid => ProcessPin.Open(pid) is { } pin ? new Win32Pin(pin) : null;

    private static bool IsProtected(ProcessInfo p) =>
        p.Pid <= 4 || p.Pid == Environment.ProcessId || CriticalProcesses.IsCriticalName(p.Name);

    /// <summary>
    /// Every process below <paramref name="rootPid"/> by parent id, deepest first. A process only counts as a child if
    /// it started after its parent: a parent id outlives the parent, and Windows reuses ids.
    /// </summary>
    private IEnumerable<ProcessInfo> DescendantsDeepestFirst(int rootPid, ProcessInfo? root)
    {
        var all = _monitor.GetAllProcesses().ToList();
        var children = all.Where(p => p.ParentPid is > 0).ToLookup(p => p.ParentPid!.Value);

        var order = new List<ProcessInfo>();
        var seen = new HashSet<int> { rootPid };
        var frontier = new Queue<(int Pid, DateTime Start)>();
        frontier.Enqueue((rootPid, root?.StartTime ?? DateTime.MinValue));

        while (frontier.Count > 0)
        {
            var (parentPid, parentStart) = frontier.Dequeue();
            foreach (var child in children[parentPid])
            {
                if (!seen.Add(child.Pid)) continue;
                if (parentStart != DateTime.MinValue && child.StartTime != DateTime.MinValue && child.StartTime < parentStart) continue;
                order.Add(child);
                frontier.Enqueue((child.Pid, child.StartTime));
            }
        }

        order.Reverse(); // breadth-first reversed: every process comes before its parent
        return order;
    }
}
