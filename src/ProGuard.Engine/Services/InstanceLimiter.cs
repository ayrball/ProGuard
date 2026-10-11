using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Enforces <see cref="ProcessRule.MaxInstances"/> (Process Lasso's instance count limits): when a matching
/// process starts and there are more instances than the limit, the newest ones are terminated. Instances are
/// counted in the new process's session unless the rule says <see cref="ProcessRule.MaxInstancesAllSessions"/>.
/// </summary>
public sealed class InstanceLimiter
{
    private readonly ILogger<InstanceLimiter> _logger;
    private readonly IProcessMonitor _monitor;
    private readonly IRuleProcessAccess _access;

    // Terminated by us but not reported gone yet: the monitor still lists them, and they must not be counted
    // (or terminated) twice.
    private readonly object _gate = new();
    private readonly HashSet<int> _terminated = new();

    public InstanceLimiter(ILogger<InstanceLimiter> logger, IProcessMonitor monitor, IRuleProcessAccess? access = null)
    {
        _logger = logger;
        _monitor = monitor;
        _access = access ?? new Win32RuleProcessAccess();
    }

    /// <summary>
    /// Call when <paramref name="started"/> starts (or is first seen) and matches <paramref name="rule"/>.
    /// Returns true if <paramref name="started"/> itself was terminated, so the caller applies nothing else to it.
    /// </summary>
    public bool Enforce(ProcessInfo started, ProcessRule rule)
    {
        if (rule.MaxInstances is not int limit || limit < 1) return false;
        if (CriticalProcesses.IsCriticalName(started.Name)) return false;

        var key = ProcessExtensions.NormalizeName(started.Name);
        int? session = null;
        if (!rule.MaxInstancesAllSessions)
        {
            session = _access.GetSessionId(started.Pid);
            if (session == null) return false; // can't tell which instances it competes with
        }

        lock (_gate)
        {
            var pids = _monitor.GetAllProcesses()
                .Where(p => ProcessExtensions.NormalizeName(p.Name) == key)
                .Select(p => p.Pid)
                .Append(started.Pid) // the monitor normally lists it already
                .Distinct()
                .Where(pid => !_terminated.Contains(pid))
                .Where(pid => session == null || _access.GetSessionId(pid) == session)
                .ToList();

            int excess = pids.Count - limit;
            if (excess <= 0) return false;

            // Newest first. An instance whose start time can't be read is never picked: it is most likely one
            // we couldn't terminate anyway.
            var newest = pids
                .Select(pid => (Pid: pid, Start: _access.GetStartTimeUtc(pid)))
                .Where(x => x.Start != null)
                .OrderByDescending(x => x.Start)
                .ThenByDescending(x => x.Pid)
                .Take(excess)
                .ToList();

            bool terminatedStarted = false;
            foreach (var (pid, _) in newest)
            {
                if (_access.IsCritical(pid))
                {
                    _logger.LogWarning("Instance limit for {Name}: not terminating {Pid}, it is a critical system process", started.Name, pid);
                    continue;
                }

                if (_access.Terminate(pid))
                {
                    _terminated.Add(pid);
                    terminatedStarted |= pid == started.Pid;
                    _logger.LogWarning("Instance limit for {Name}: terminated {Pid} ({Count} instances {Scope}, limit {Limit})",
                        started.Name, pid, pids.Count, session == null ? "in all sessions" : $"in session {session}", limit);
                }
                else
                {
                    _logger.LogWarning("Instance limit for {Name}: failed to terminate {Pid}", started.Name, pid);
                }
            }

            return terminatedStarted;
        }
    }

    public void OnProcessExited(int pid)
    {
        lock (_gate) _terminated.Remove(pid);
    }
}
