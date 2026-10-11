using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Moq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services.ProcessRules;

namespace ProGuard.Tests;

/// <summary>
/// The appliers against real Windows: a ping.exe this test starts (so it is ours to change) and the current user's
/// own registry hive. None of this assumes a CPU priority: Process Lasso on this machine lowers busy processes.
/// </summary>
public sealed class PersistentProcessRuleRealProcessTests : IDisposable
{
    private readonly Win32ProcessTuning _native = new();
    private readonly Process _process;
    private readonly ProcessTarget _target;

    public PersistentProcessRuleRealProcessTests()
    {
        _process = Process.Start(new ProcessStartInfo("ping.exe", "-n 121 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!;
        _target = new ProcessTarget(_process.Id, "PING", null);
    }

    public void Dispose()
    {
        try { _process.Kill(); } catch { /* already gone */ }
        _process.Dispose();
    }

    private RuleApplyResult Apply(IProcessRuleApplier applier, ProcessRule rule, bool onlyIfChanged = false)
    {
        var result = applier.Apply(rule, _target, onlyIfChanged);
        Assert.True(result.Outcome is RuleApplyOutcome.Applied or RuleApplyOutcome.Unchanged, $"{result.Setting}: {result.Outcome} {result.Detail}");
        return result;
    }

    [Fact]
    public void IoPriority_IsSetOnARealProcess()
    {
        var applier = new IoPriorityApplier(_native);

        foreach (var level in new[] { 0, 1, 2 })
        {
            Apply(applier, new ProcessRule { IoPriority = level });
            Assert.True(_native.TryGetIoPriority(_process.Id, out var actual));
            Assert.Equal(level, actual);
        }
    }

    [Fact]
    public void EfficiencyMode_IsTurnedOnAndExplicitlyOffOnARealProcess()
    {
        var applier = new EfficiencyModeApplier(_native);

        Apply(applier, new ProcessRule { EfficiencyMode = true });
        Assert.True(_native.TryGetEfficiencyMode(_process.Id, out var controlled, out var enabled));
        Assert.True(controlled);
        Assert.True(enabled);

        Apply(applier, new ProcessRule { EfficiencyMode = false });
        Assert.True(_native.TryGetEfficiencyMode(_process.Id, out controlled, out enabled));
        Assert.True(controlled, "off must keep the control bit so Windows can't throttle it again");
        Assert.False(enabled);
    }

    [Fact]
    public void MemoryPriority_IsSetOnARealProcess()
    {
        Apply(new MemoryPriorityApplier(_native), new ProcessRule { MemoryPriority = 2 });

        Assert.True(_native.TryGetMemoryPriority(_process.Id, out var actual));
        Assert.Equal(2, actual);
    }

    [Fact]
    public void PriorityBoost_IsDisabledForTheProcessAndItsExistingThreads()
    {
        Apply(new PriorityBoostApplier(_native), new ProcessRule { DisablePriorityBoost = true });

        Assert.True(_native.TryGetPriorityBoostDisabled(_process.Id, out var disabled));
        Assert.True(disabled);

        _process.Refresh();
        foreach (ProcessThread thread in _process.Threads)
        {
            var h = OpenThread(0x0040 /* THREAD_QUERY_INFORMATION */, false, thread.Id);
            if (h == IntPtr.Zero) continue; // exited meanwhile
            try
            {
                Assert.True(GetThreadPriorityBoost(h, out var threadDisabled));
                Assert.True(threadDisabled, $"thread {thread.Id} still boosts");
            }
            finally { CloseHandle(h); }
        }
    }

    [Fact]
    public void CpuSets_AreSetAndClearedOnARealProcess()
    {
        var sets = CpuSets.GetSystemCpuSets();
        Assert.NotEmpty(sets);
        Assert.Equal(sets.Count, sets.Select(s => s.Id).Distinct().Count());
        var first = sets[0].Id;
        var applier = new CpuSetsApplier(_native);

        Apply(applier, new ProcessRule { CpuSetIds = first.ToString() });
        Assert.True(_native.TryGetDefaultCpuSets(_process.Id, out var ids));
        Assert.Equal(new[] { first }, ids);

        Apply(applier, new ProcessRule { CpuSetIds = "" });
        Assert.True(_native.TryGetDefaultCpuSets(_process.Id, out ids));
        Assert.Empty(ids);
    }

    [Fact]
    public void Reapply_PutsBackAnIoPriorityChangedBehindItsBack()
    {
        var monitor = new Mock<IProcessMonitor>();
        monitor.Setup(m => m.GetAllProcesses()).Returns(new[] { new ProcessInfo { Pid = _process.Id, Name = "PING" } });
        var store = new Mock<IRuleStore>();
        store.Setup(s => s.GetProcessRule("PING")).Returns(new ProcessRule { IoPriority = 0, EfficiencyMode = true, ReapplyIfChanged = true });
        var service = new PersistentRuleService(NullLogger<PersistentRuleService>.Instance, _native, monitor.Object, store.Object);

        service.ReapplyChanged();
        Assert.Equal(0, service.ReapplyChanged()); // settled

        _native.SetIoPriority(_process.Id, 2);
        Assert.Equal(1, service.ReapplyChanged());
        Assert.True(_native.TryGetIoPriority(_process.Id, out var actual));
        Assert.Equal(0, actual);
    }

    [Fact]
    public void OwnerSid_AndImagePath_AreReadFromTheProcess()
    {
        using var me = WindowsIdentity.GetCurrent();
        Assert.Equal(me.User!.Value, _native.GetProcessOwnerSid(_process.Id));
        Assert.EndsWith(@"\ping.exe", _native.GetImagePath(_process.Id), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserGpuPreference_IsWrittenUnderHkuOfTheGivenSid()
    {
        // HKU\<my SID> is this user's HKCU, so this is safe to touch; the value name is a path that doesn't exist.
        using var me = WindowsIdentity.GetCurrent();
        var sid = me.User!.Value;
        var exe = $@"C:\ProGuardTest\{Guid.NewGuid():N}\fake.exe";
        try
        {
            _native.WriteUserGpuPreference(sid, exe, "GpuPreference=2;");

            Assert.Equal("GpuPreference=2;", _native.ReadUserGpuPreference(sid, exe));
            using var hkcu = Registry.CurrentUser.OpenSubKey(Win32ProcessTuning.UserGpuPreferencesKey);
            Assert.Equal("GpuPreference=2;", hkcu?.GetValue(exe));
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(Win32ProcessTuning.UserGpuPreferencesKey, writable: true);
            key?.DeleteValue(exe, throwOnMissingValue: false);
        }

        Assert.Throws<InvalidOperationException>(() => _native.WriteUserGpuPreference("S-1-5-21-0-0-0-424242", exe, "GpuPreference=1;"));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, int threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadPriorityBoost(IntPtr thread, [MarshalAs(UnmanagedType.Bool)] out bool disabled);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
