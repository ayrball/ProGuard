using System;
using System.ComponentModel;
using System.Diagnostics;
using ProGuard.Engine;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;

namespace ProGuard.Tests;

/// <summary>
/// High I/O priority (and Realtime) need SeIncreaseBasePriorityPrivilege enabled on the engine's token. An elevated
/// token holds it disabled, so the engine enables it at startup; these check that doing so makes High work, and that
/// without it the refusal reads as a sentence rather than an NTSTATUS.
/// </summary>
public class PriorityPrivilegeTests
{
    [ElevationFact]
    public void WithThePrivilegeEnabled_HighIoPriority_IsApplied()
    {
        Assert.Null(new Win32MemoryOperations().EnablePrivilege(EngineWorker.IncreaseBasePriorityPrivilege));

        using var child = StartIdleChild();
        try
        {
            var tuning = new Win32ProcessTuning();
            tuning.SetIoPriority(child.Id, 3);
            Assert.True(tuning.TryGetIoPriority(child.Id, out var now));
            Assert.Equal(3, now);
        }
        finally
        {
            child.Kill();
        }
    }

    [ElevationFact(requiresElevation: false)]
    public void WithoutThePrivilege_HighIoPriority_IsRefusedInWords()
    {
        // A standard token doesn't hold the privilege at all, so enabling it fails and says why.
        Assert.Contains("not held", new Win32MemoryOperations().EnablePrivilege(EngineWorker.IncreaseBasePriorityPrivilege));

        using var child = StartIdleChild();
        try
        {
            var refused = Assert.Throws<Win32Exception>(() => new Win32ProcessTuning().SetIoPriority(child.Id, 3));
            Assert.Equal(Win32ProcessTuning.PrivilegeNotHeldMessage, refused.Message);
        }
        finally
        {
            child.Kill();
        }
    }

    private static Process StartIdleChild() =>
        Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
}
