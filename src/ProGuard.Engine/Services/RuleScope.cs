using System;
using System.Collections.Concurrent;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Shared.Security;

namespace ProGuard.Engine.Services;

/// <summary>Reads who owns a process. An interface so the rule-scoping decisions can be tested against fake processes.</summary>
public interface IProcessOwnerReader
{
    /// <summary>The process's token user and elevation, or null when it can't be read (gone, or protected).</summary>
    TokenIdentity? Read(int pid);
}

public sealed class Win32ProcessOwnerReader : IProcessOwnerReader
{
    public TokenIdentity? Read(int pid) => TokenIdentity.OfProcess(pid);
}

/// <summary>
/// Decides whether a stored rule may act on a process. The engine runs as SYSTEM and acts on rules by process name, so
/// a rule a standard user saved would otherwise reach every later process of that name: other users', services',
/// elevated ones. Here a rule from a standard user (<see cref="RuleAuthorship.IsScopedToAuthor"/>) applies only to
/// processes whose token user is its author and that aren't elevated, which is exactly what the author could change
/// themselves. A UAC-filtered administrator's unelevated UI counts as a standard user, so its rules leave that
/// administrator's own elevated processes alone too. Rules from administrators and the engine, and rules stored before
/// authors were recorded, apply to every process as before.
/// <para>
/// Anything that can't be read counts as not matching: a scoped rule never acts on a process whose owner is unknown.
/// Owners are cached by process id and start time, so the answer for a process is never reused for another that took
/// over its id.
/// </para>
/// </summary>
public sealed class RuleScope
{
    /// <summary>The one every service uses unless a test supplies its own.</summary>
    public static RuleScope Default { get; } = new(new Win32ProcessOwnerReader());

    private const int MaxCached = 8192;

    private readonly IProcessOwnerReader _owners;
    private readonly ConcurrentDictionary<(int Pid, long StartTicks), TokenIdentity?> _cache = new();

    public RuleScope(IProcessOwnerReader owners) => _owners = owners;

    /// <summary>Whether <paramref name="rule"/> may act on <paramref name="process"/>.</summary>
    public bool AppliesTo(IAuthoredRule rule, ProcessInfo process) => AppliesTo(rule, process.Pid, process.StartTime);

    /// <summary>Whether <paramref name="rule"/> may act on process <paramref name="pid"/>, started at <paramref name="startTimeUtc"/> if known.</summary>
    public bool AppliesTo(IAuthoredRule rule, int pid, DateTime startTimeUtc = default)
    {
        if (!rule.IsScopedToAuthor()) return true;
        if (pid <= 4) return false;
        return Owner(pid, startTimeUtc) is { } owner
            && !owner.Elevated
            && string.Equals(owner.UserSid, rule.AuthorSid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rule stored for <paramref name="process"/>'s name, if it may act on that process; otherwise null.</summary>
    public ProcessRule? RuleFor(IRuleStore store, ProcessInfo process) =>
        store.GetProcessRule(process.Name) is { } rule && AppliesTo(rule, process) ? rule : null;

    private TokenIdentity? Owner(int pid, DateTime startTimeUtc)
    {
        if (startTimeUtc == default || startTimeUtc == DateTime.MinValue) return _owners.Read(pid);
        var key = (pid, startTimeUtc.Ticks);
        if (_cache.TryGetValue(key, out var known)) return known;
        var owner = _owners.Read(pid);
        if (owner == null) return null; // maybe not readable yet (still starting): ask again next time
        if (_cache.Count >= MaxCached) _cache.Clear();
        _cache[key] = owner;
        return owner;
    }
}
