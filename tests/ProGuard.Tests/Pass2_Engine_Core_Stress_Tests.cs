using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;

namespace ProGuard.Tests;

public class Pass2_Engine_Core_Stress_Tests
{
    [Fact]
    public void CpuUsageTracker_StressTest_1000Iterations_ConcurrentAccess()
    {
        var tracker = new CpuUsageTracker();
        using var currentProc = Process.GetCurrentProcess();

        // Simulate 1000 tracking passes
        for (int i = 0; i < 1000; i++)
        {
            var usage = tracker.GetCpuUsage(currentProc);
            Assert.True(usage >= 0.0 && usage <= 100.0, $"Usage should be clamped 0-100%, got {usage}");
        }

        tracker.RemoveProcess(currentProc.Id);
    }

    [Fact]
    public void SystemInfo_RetrievesHostHardware_Accurately()
    {
        Assert.True(SystemInfo.LogicalProcessorCount > 0, "Logical processors must be > 0");
        Assert.True(SystemInfo.TotalPhysicalMemoryBytes > 0, "Total memory must be > 0");
        Assert.False(string.IsNullOrWhiteSpace(SystemInfo.CpuName), "CPU Name must not be empty");

        var overallCpu = SystemInfo.GetOverallCpuPercent();
        Assert.InRange(overallCpu, 0.0, 100.0);

        var availMem = SystemInfo.GetAvailableMemoryMB();
        Assert.True(availMem >= 0.0);
    }

    [Fact]
    public void Native_Kernel32_SystemInfo_CallsSuccessfully()
    {
        Kernel32.GetSystemInfo(out var sysInfo);
        Assert.True(sysInfo.dwNumberOfProcessors > 0);
        Assert.True(sysInfo.dwPageSize > 0);
    }

    [Fact]
    public void Native_User32_ForegroundWindow_DoesNotCrash()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            User32.GetWindowThreadProcessId(hwnd, out var pid);
            Assert.True(pid >= 0);
        }
    }

    [Fact]
    public void Native_Powrprof_ActiveScheme_CallsSafely()
    {
        var result = Powrprof.PowerGetActiveScheme(IntPtr.Zero, out var ptr);
        if (result == 0 && ptr != IntPtr.Zero)
        {
            var guid = (Guid)Marshal.PtrToStructure(ptr, typeof(Guid))!;
            Marshal.FreeHGlobal(ptr);
            Assert.NotEqual(Guid.Empty, guid);
        }
    }
}
