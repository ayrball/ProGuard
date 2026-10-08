using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Limits CPU usage of specific processes using Windows Job Objects.
/// </summary>
public class CpuLimiter : ICpuLimiter, IDisposable
{
    private readonly ILogger<CpuLimiter> _logger;
    private readonly ConcurrentDictionary<int, IntPtr> _activeJobs = new();

    public CpuLimiter(ILogger<CpuLimiter> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting CPU Limiter");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping CPU Limiter");
        foreach (var job in _activeJobs.Values)
        {
            Kernel32.CloseHandle(job);
        }
        _activeJobs.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void AddLimit(int pid, double percent)
    {
        LimitProcessCpu(pid, (int)Math.Clamp(percent, 1, 99));
    }

    public void LimitProcessCpu(int pid, int cpuLimitPercent)
    {
        if (cpuLimitPercent <= 0 || cpuLimitPercent >= 100) return;

        try
        {
            using var p = Process.GetProcessById(pid);
            
            // Create a job object
            var hJob = Kernel32.CreateJobObject(IntPtr.Zero, "ProGuardLimit_" + pid);
            if (hJob == IntPtr.Zero)
            {
                _logger.LogError("Failed to create job object for PID {Pid}", pid);
                return;
            }

            var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = 0x1 | 0x2, // JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP
                CpuRate = (uint)(cpuLimitPercent * 100)
            };

            int length = Marshal.SizeOf(typeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION));
            IntPtr pInfo = Marshal.AllocHGlobal(length);
            Marshal.StructureToPtr(info, pInfo, false);

            if (!Kernel32.SetInformationJobObject(hJob, 8, pInfo, (uint)length))
            {
                _logger.LogError("Failed to set job object information for PID {Pid}", pid);
                Kernel32.CloseHandle(hJob);
                Marshal.FreeHGlobal(pInfo);
                return;
            }
            Marshal.FreeHGlobal(pInfo);

            if (!Kernel32.AssignProcessToJobObject(hJob, p.Handle))
            {
                _logger.LogError("Failed to assign process {Pid} to job object", pid);
                Kernel32.CloseHandle(hJob);
                return;
            }

            _activeJobs[pid] = hJob;
            _logger.LogInformation("Limited PID {Pid} to {Limit}% CPU", pid, cpuLimitPercent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error limiting CPU for PID {Pid}", pid);
        }
    }

    /// <inheritdoc/>
    public void RemoveLimit(int pid)
    {
        if (_activeJobs.TryRemove(pid, out var hJob))
        {
            Kernel32.CloseHandle(hJob);
            _logger.LogInformation("Removed CPU limit for PID {Pid}", pid);
        }
    }

    public void Dispose()
    {
        foreach (var job in _activeJobs.Values)
        {
            Kernel32.CloseHandle(job);
        }
        _activeJobs.Clear();
    }
}
