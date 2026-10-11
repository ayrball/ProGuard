using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind; // System.Diagnostics has one too

namespace ProGuard.Engine.Services;

/// <summary>
/// Reads, validates, stores and deletes the process and Watchdog rules the thin client edits, checks the caller may
/// make the change (<see cref="RuleAuthorization"/>), and puts a saved or deleted process rule into effect on the
/// instances already running. Every method returns the reply to send; none throws for a refusal.
/// </summary>
public sealed class RuleService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<RuleService> _logger;
    private readonly IRuleStore _store;
    private readonly IProcessMonitor _monitor;
    private readonly ICpuLimiter _cpuLimiter;
    private readonly IProBalanceEngine _proBalance;
    private readonly IRuleProcessAccess _access;
    private readonly PersistentRuleService? _persistent;
    private readonly InstanceBalancer? _balancer;
    private readonly LaunchPriorityService? _launchPriorities;
    private readonly ActivityLog _activity;

    public RuleService(
        ILogger<RuleService> logger,
        IRuleStore store,
        IProcessMonitor monitor,
        ICpuLimiter cpuLimiter,
        IProBalanceEngine proBalance,
        IRuleProcessAccess access,
        ActivityLog activity,
        PersistentRuleService? persistent = null,
        InstanceBalancer? balancer = null,
        LaunchPriorityService? launchPriorities = null)
    {
        _logger = logger;
        _store = store;
        _monitor = monitor;
        _cpuLimiter = cpuLimiter;
        _proBalance = proBalance;
        _access = access;
        _activity = activity;
        _persistent = persistent;
        _balancer = balancer;
        _launchPriorities = launchPriorities;
    }

    // === Process rules ===

    public CommandResult GetProcessRules() =>
        CommandResult.Ok(Serialize(_store.GetProcessRules()
            .OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(RuleDtoMapper.ToDto).ToList()));

    public CommandResult SaveProcessRule(ProcessRuleDto dto, IIpcCaller caller)
    {
        if (Validate(dto) is { } invalid) return CommandResult.Fail(invalid);

        var rules = _store.GetProcessRules().ToList();
        var existing = dto.Id != 0 ? rules.FirstOrDefault(r => r.Id == dto.Id) : null;
        if (dto.Id != 0 && existing == null) return CommandResult.Fail("That rule no longer exists. Refresh the list and try again.");

        var key = ProcessExtensions.NormalizeName(dto.ProcessName);
        var sameName = rules.FirstOrDefault(r => ProcessExtensions.NormalizeName(r.ProcessName) == key);
        if (sameName != null && sameName.Id != dto.Id)
        {
            // Not allowed to replace someone's rule by saving under its name, so say that rather than "duplicate".
            if (RuleAuthorization.CheckProcessRule(sameName, caller, _monitor) is { } protectedRule)
                return IpcAuthorization.Deny(caller, protectedRule);
            return CommandResult.Fail($"There is already a rule for {sameName.ProcessName}.");
        }

        var rule = new ProcessRule();
        RuleDtoMapper.CopyTo(dto, rule);
        rule.ProcessName = dto.ProcessName.Trim();
        rule.Id = existing?.Id ?? 0;

        // Both the rule being replaced and the one replacing it must be ones this caller could have made.
        if (existing != null && RuleAuthorization.CheckProcessRule(existing, caller, _monitor) is { } cannotChange)
            return IpcAuthorization.Deny(caller, cannotChange);
        if (RuleAuthorization.CheckProcessRule(rule, caller, _monitor) is { } denied)
            return IpcAuthorization.Deny(caller, denied);

        _store.SaveProcessRule(rule);
        _launchPriorities?.Sync(_store.GetProcessRules());
        ApplyToRunning(rule, existing);
        _activity.Add(ActivityKind.Manual, rule.ProcessName, 0, existing == null ? $"Added a rule for {rule.ProcessName}." : $"Changed the rule for {rule.ProcessName}.");
        return CommandResult.Ok(Serialize(RuleDtoMapper.ToDto(rule)));
    }

    public CommandResult DeleteProcessRule(int id, IIpcCaller caller)
    {
        var existing = _store.GetProcessRules().FirstOrDefault(r => r.Id == id);
        if (existing == null) return CommandResult.Fail("That rule no longer exists. Refresh the list and try again.");
        if (RuleAuthorization.CheckProcessRule(existing, caller, _monitor) is { } denied)
            return IpcAuthorization.Deny(caller, denied);

        _store.DeleteProcessRule(id);
        _launchPriorities?.Sync(_store.GetProcessRules());
        ReleaseCap(existing);
        _balancer?.Tick(); // gives balanced instances their own affinity back now rather than at the next check
        _activity.Add(ActivityKind.Manual, existing.ProcessName, 0, $"Deleted the rule for {existing.ProcessName}.");
        return CommandResult.Ok();
    }

    /// <summary>Returns what is wrong with a rule, in words fit to show, or null when it can be stored.</summary>
    public static string? Validate(ProcessRuleDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.ProcessName)) return "A rule needs a process name";
        if (ProcessExtensions.ProgramNameProblem(dto.ProcessName) is { } badName) return badName;
        if (!IsPriority(dto.PriorityClass)) return "Invalid priority class";
        if (dto.AffinityMask == 0) return "Choose at least one processor";
        if (dto.CpuLimitPercent is { } cap && !(cap >= 1 && cap <= 99)) return "A CPU limit must be between 1% and 99%";
        if (dto.MaxInstances is < 1) return "The instance limit must be at least 1";
        if (dto.PowerProfileGuid != null && !Guid.TryParse(dto.PowerProfileGuid, out _)) return "Invalid power plan GUID";
        if (dto.IoPriority is int io && !ProcessRuleOptions.IsValid(ProcessRuleOptions.IoPriorities, io)) return "Invalid I/O priority";
        if (dto.MemoryPriority is int mem && !ProcessRuleOptions.IsValid(ProcessRuleOptions.MemoryPriorities, mem, ProcessRuleOptions.MinMemoryPriority)) return "Invalid memory priority";
        if (dto.GpuPriority is int gpu && !ProcessRuleOptions.IsValid(ProcessRuleOptions.GpuPriorities, gpu)) return "Invalid GPU priority";
        if (dto.GpuPreference is int pref && !ProcessRuleOptions.IsValid(ProcessRuleOptions.GpuPreferences, pref)) return "Invalid GPU preference";
        if (dto.CpuSetIds != null && !CpuSets.TryParseIds(dto.CpuSetIds, out _)) return "Invalid CPU Set IDs";

        // CPU Limiter (affinity reduction).
        if (dto.CpuLimitThresholdPercent is { } threshold && !(threshold > 0 && threshold <= 100)) return "The CPU Limiter threshold must be above 0% and at most 100%";
        if (dto.CpuLimitOverThresholdSeconds is < 1) return "The CPU Limiter time over the threshold must be at least 1 second";
        if (dto.CpuLimitCoresToRemove is < 1) return "The CPU Limiter must remove at least 1 core";
        if (dto.CpuLimitDurationSeconds is < 1) return "The CPU Limiter duration must be at least 1 second";

        // Instance Balancer.
        if (dto.InstanceBalancerMode is { } mode && !Enum.IsDefined(mode)) return "Invalid Instance Balancer mode";
        if (dto.InstanceBalancerCoresPerInstance is < 1) return "Instances need at least 1 core each";
        if (dto.InstanceBalancerReservedCores is < 0) return "Reserved cores can't be negative";
        if (dto.InstanceBalancerMinCpuPercent is { } minCpu && !(minCpu >= 0 && minCpu <= 100)) return "The Instance Balancer minimum CPU must be between 0% and 100%";

        // Startup boost.
        if (!IsPriority(dto.StartupBoostPriorityClass)) return "Invalid startup boost priority class";
        if (dto.StartupBoostSeconds is int seconds && (seconds < StartupBoostService.MinSeconds || seconds > StartupBoostService.MaxSeconds))
            return $"The startup boost must last {StartupBoostService.MinSeconds}-{StartupBoostService.MaxSeconds} seconds";

        // Launch-time priorities (Image File Execution Options).
        if (dto.ApplyPrioritiesAtCreation)
        {
            if (LaunchPriorityService.ImageName(dto.ProcessName) == null)
                return "Priorities at launch need a plain program name such as game.exe: Windows can't match a wildcard or a path there";
            if (dto.PriorityClass == (int)ProcessPriorityClass.RealTime)
                return "Windows doesn't apply Realtime priority at launch; pick another priority or switch off priorities at launch";
            if (dto.PriorityClass == null && dto.IoPriority == null && dto.MemoryPriority == null)
                return "Priorities at launch need a priority, I/O priority or memory priority to apply";
        }
        return null;
    }

    private static bool IsPriority(int? value) => value == null || Enum.IsDefined((ProcessPriorityClass)value.Value);

    // === Applying a rule to what is already running ===

    private void ApplyToRunning(ProcessRule rule, ProcessRule? previous)
    {
        try
        {
            // A cap this rule had put on, which it no longer asks for (cap removed, or the rule moved to another name).
            if (previous != null && previous.CpuLimitPercent != null
                && (rule.CpuLimitPercent == null || ProcessExtensions.NormalizeName(previous.ProcessName) != ProcessExtensions.NormalizeName(rule.ProcessName)))
                ReleaseCap(previous);

            foreach (var p in Running(rule.ProcessName))
                ApplyTo(rule, p);

            _balancer?.Tick();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying the rule for {Name} to running processes failed", rule.ProcessName);
        }
    }

    /// <summary>Lifts the hard CPU cap a rule applied. Only for rules that set one, so a cap set by hand on the same name stays.</summary>
    private void ReleaseCap(ProcessRule rule)
    {
        if (rule.CpuLimitPercent == null) return;
        foreach (var p in Running(rule.ProcessName)) _cpuLimiter.RemoveLimit(p.Pid);
    }

    private List<ProcessInfo> Running(string name)
    {
        var key = ProcessExtensions.NormalizeName(name);
        return _monitor.GetAllProcesses().Where(p => ProcessExtensions.NormalizeName(p.Name) == key).ToList();
    }

    private void ApplyTo(ProcessRule rule, ProcessInfo p)
    {
        if (rule.DisallowExecution)
        {
            if (p.Pid <= 4 || p.Pid == Environment.ProcessId || CriticalProcesses.IsCriticalName(p.Name) || _access.IsCritical(p.Pid)) return;
            if (_access.Terminate(p.Pid))
                _activity.Add(ActivityKind.Rule, p.Name, p.Pid, $"Ended {p.Name}: a rule doesn't allow it to run.");
            return;
        }

        var applied = new List<string>();

        // First, so a process ProBalance holds down is put back before the rule sets its own priority.
        if (rule.ExcludeFromProBalance && _proBalance.ReleaseRestraint(p.Pid)) applied.Add("released from ProBalance");

        if (rule.PriorityClass is int priority && _access.SetPriorityClass(p.Pid, (ProcessPriorityClass)priority))
            applied.Add($"{(ProcessPriorityClass)priority} priority");
        if (rule.AffinityMask is long mask && mask != 0 && _access.SetAffinityMask(p.Pid, mask))
            applied.Add($"affinity 0x{mask:X}");
        if (rule.CpuLimitPercent is double cap)
        {
            _cpuLimiter.AddLimit(p.Pid, cap);
            applied.Add($"a {cap:0}% CPU limit");
        }

        if (_persistent != null)
        {
            var results = _persistent.Apply(rule, new ProcessTarget(p.Pid, p.Name, p.FilePath));
            applied.AddRange(results.Where(r => r.Outcome == RuleApplyOutcome.Applied).Select(r => r.Setting.ToLowerInvariant()));
        }

        if (applied.Count > 0)
            _activity.Add(ActivityKind.Rule, p.Name, p.Pid, $"Applied the rule to {p.Name}: {string.Join(", ", applied)}.");
    }

    // === Watchdog rules ===

    public CommandResult GetWatchdogRules() =>
        CommandResult.Ok(Serialize(_store.GetWatchdogRules().Select(DtoMapper.Map<WatchdogRule, WatchdogRuleDto>).ToList()));

    public CommandResult SaveWatchdogRule(WatchdogRuleDto dto, IIpcCaller caller)
    {
        if (Validate(dto) is { } invalid) return CommandResult.Fail(invalid);

        WatchdogRule? existing = null;
        if (dto.Id != 0)
        {
            existing = _store.GetWatchdogRules().FirstOrDefault(r => r.Id == dto.Id);
            if (existing == null) return CommandResult.Fail("That watchdog rule no longer exists. Refresh the list and try again.");
        }

        var rule = DtoMapper.Map<WatchdogRuleDto, WatchdogRule>(dto);
        rule.TargetProcessName = dto.TargetProcessName.Trim();
        rule.ActionArgument = string.IsNullOrWhiteSpace(dto.ActionArgument) ? null : dto.ActionArgument.Trim();
        rule.Id = existing?.Id ?? 0;

        if (existing != null && RuleAuthorization.CheckWatchdogRule(existing, caller, _monitor) is { } cannotChange)
            return IpcAuthorization.Deny(caller, cannotChange);
        if (RuleAuthorization.CheckWatchdogRule(rule, caller, _monitor) is { } denied)
            return IpcAuthorization.Deny(caller, denied);

        if (existing == null) _store.AddWatchdogRule(rule);
        else _store.UpdateWatchdogRule(rule);
        _activity.Add(ActivityKind.Manual, rule.TargetProcessName, 0,
            existing == null ? $"Added a watchdog rule for {rule.TargetProcessName}." : $"Changed the watchdog rule for {rule.TargetProcessName}.");
        return CommandResult.Ok(Serialize(DtoMapper.Map<WatchdogRule, WatchdogRuleDto>(rule)));
    }

    public CommandResult DeleteWatchdogRule(int id, IIpcCaller caller)
    {
        var existing = _store.GetWatchdogRules().FirstOrDefault(r => r.Id == id);
        if (existing == null) return CommandResult.Fail("That watchdog rule no longer exists. Refresh the list and try again.");
        if (RuleAuthorization.CheckWatchdogRule(existing, caller, _monitor) is { } denied)
            return IpcAuthorization.Deny(caller, denied);

        _store.DeleteWatchdogRule(id);
        _activity.Add(ActivityKind.Manual, existing.TargetProcessName, 0, $"Deleted the watchdog rule for {existing.TargetProcessName}.");
        return CommandResult.Ok();
    }

    /// <summary>Returns what is wrong with a Watchdog rule, or null. The argument is checked the way the Watchdog will read it.</summary>
    public static string? Validate(WatchdogRuleDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.TargetProcessName)) return "A watchdog rule needs a process name";
        if (ProcessExtensions.ProgramNameProblem(dto.TargetProcessName) is { } badName) return badName;
        if (!Enum.IsDefined(dto.ConditionType)) return "Invalid watchdog condition";
        if (!Enum.IsDefined(dto.ActionType)) return "Invalid watchdog action";
        if (!Enum.IsDefined(dto.MemoryMetric)) return "Invalid memory measure";
        if (!double.IsFinite(dto.ThresholdValue) || dto.ThresholdValue <= 0) return "The threshold must be greater than zero";
        if (dto.ConditionType == WatchdogTrigger.CpuAbove && dto.ThresholdValue >= 100) return "A CPU threshold must be below 100%";
        if (dto.DurationSeconds < 0) return "The time a condition must hold can't be negative";
        if (dto.ActionDurationSeconds < 0) return "The action duration can't be negative";
        if (dto.CooldownSeconds < 0) return "The cooldown can't be negative";

        var argument = dto.ActionArgument;
        var blank = string.IsNullOrWhiteSpace(argument);
        switch (dto.ActionType)
        {
            case WatchdogResponse.SetPriority when !blank && !WatchdogArguments.TryParsePriority(argument, out _):
                return $"Unknown priority '{argument}'";
            case WatchdogResponse.SetIoPriority when !blank && !WatchdogArguments.TryParseIoPriority(argument, out _):
                return $"Unknown I/O priority '{argument}'";
            case WatchdogResponse.SetAffinity when !WatchdogArguments.TryParseAffinity(argument, out _):
                return "Set affinity needs a CPU mask such as 0x3";
            case WatchdogResponse.Launch when !WatchdogArguments.TrySplitCommandLine(argument, out _, out _):
                return "Launch needs a command line";
            case WatchdogResponse.TrimWorkingSet when !blank && !WatchdogArguments.IsContinuousTrim(argument):
                return "A trim rule's argument is empty or \"continuous\"";
        }
        return null;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
}
