using System;
using System.Collections.Generic;
using ProGuard.Core.Models;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary>The process a rule is being applied to. <see cref="FilePath"/> may be null when it couldn't be read.</summary>
public readonly record struct ProcessTarget(int Pid, string Name, string? FilePath = null);

public enum RuleApplyOutcome
{
    /// <summary>The rule doesn't set this.</summary>
    NotConfigured,
    Applied,
    /// <summary>Only checking for changes, and the process already had the rule's value.</summary>
    Unchanged,
    /// <summary>Only checking for changes, and the current value couldn't be read, so nothing was done.</summary>
    Skipped,
    Failed,
}

/// <param name="Detail">The value applied (and, on a re-apply, what it replaced), or the error.</param>
public readonly record struct RuleApplyResult(string Setting, RuleApplyOutcome Outcome, string? Detail = null);

/// <summary>Applies one persistent per-process setting of a <see cref="ProcessRule"/>. Never throws.</summary>
public interface IProcessRuleApplier
{
    /// <summary>Name for logs, e.g. "I/O priority".</summary>
    string Setting { get; }

    /// <param name="onlyIfChanged">Read the current value first and leave the process alone when it already matches.</param>
    RuleApplyResult Apply(ProcessRule rule, ProcessTarget target, bool onlyIfChanged);
}

/// <summary>
/// An applier for a value that is read and written per process: works out the rule's value, optionally compares it
/// with the current one, writes it, and turns every failure into a <see cref="RuleApplyOutcome.Failed"/> result.
/// </summary>
public abstract class ProcessSettingApplier<T> : IProcessRuleApplier where T : notnull
{
    protected ProcessSettingApplier(IProcessTuningNative native) => Native = native;

    protected IProcessTuningNative Native { get; }

    public abstract string Setting { get; }

    /// <summary>Returns false when the rule doesn't set this; sets <paramref name="error"/> when it sets something invalid.</summary>
    protected abstract bool TryGetDesired(ProcessRule rule, out T value, out string? error);
    protected abstract bool TryRead(int pid, out T value);
    protected abstract void Write(int pid, T value);
    protected virtual bool Matches(T current, T desired) => EqualityComparer<T>.Default.Equals(current, desired);
    protected virtual string Describe(T value) => value.ToString() ?? string.Empty;

    public RuleApplyResult Apply(ProcessRule rule, ProcessTarget target, bool onlyIfChanged)
    {
        try
        {
            if (!TryGetDesired(rule, out var desired, out var invalid)) return Result(RuleApplyOutcome.NotConfigured);
            if (invalid != null) return Result(RuleApplyOutcome.Failed, invalid);

            string? previous = null;
            if (onlyIfChanged)
            {
                if (!TryRead(target.Pid, out var current)) return Result(RuleApplyOutcome.Skipped, "current value can't be read");
                if (Matches(current, desired)) return Result(RuleApplyOutcome.Unchanged, Describe(desired));
                previous = Describe(current);
            }

            Write(target.Pid, desired);
            return Result(RuleApplyOutcome.Applied, previous == null ? Describe(desired) : $"{Describe(desired)} (was {previous})");
        }
        catch (Exception ex)
        {
            return Result(RuleApplyOutcome.Failed, ex.Message);
        }
    }

    private RuleApplyResult Result(RuleApplyOutcome outcome, string? detail = null) => new(Setting, outcome, detail);

    /// <summary>Validation shared by the appliers whose value is an index into a <see cref="ProcessRuleOptions"/> list.</summary>
    protected static bool TryGetOption(int? stored, IReadOnlyList<string> options, out int value, out string? error, int min = 0)
    {
        value = stored ?? 0;
        error = null;
        if (stored == null) return false;
        if (!ProcessRuleOptions.IsValid(options, value, min)) error = $"{value} is out of range ({min}..{options.Count - 1})";
        return true;
    }
}
