using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Limits CPU usage of specific processes using Windows Job Objects (hard CPU-rate cap).
/// </summary>
public class CpuLimiter : ICpuLimiter, IDisposable
{
    private const uint FullRate = 10000; // 100% of total CPU, in 1/100ths of a percent

    private readonly ILogger<CpuLimiter> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, IntPtr> _activeJobs = new();
    // The cap each job enforces, in percent of the whole machine (the job itself only holds it as a rate).
    private readonly Dictionary<int, int> _activePercents = new();

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
        ReleaseAll();
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
        uint rate = (uint)cpuLimitPercent * 100;

        lock (_gate)
        {
            try
            {
                // Already limited: just retune the existing job.
                if (_activeJobs.TryGetValue(pid, out var existing))
                {
                    if (SetCpuRate(existing, rate))
                    {
                        _activePercents[pid] = cpuLimitPercent;
                        _logger.LogInformation("Updated CPU limit for PID {Pid} to {Limit}%", pid, cpuLimitPercent);
                    }
                    else
                        _logger.LogError("Failed to update CPU limit for PID {Pid}: {Error}", pid, LastError());
                    return;
                }

                using var p = Process.GetProcessById(pid);

                // Unnamed job: a name would let any other process squat on / open it.
                var hJob = Kernel32.CreateJobObject(IntPtr.Zero, null);
                if (hJob == IntPtr.Zero)
                {
                    _logger.LogError("Failed to create job object for PID {Pid}: {Error}", pid, LastError());
                    return;
                }

                if (!SetCpuRate(hJob, rate))
                {
                    _logger.LogError("Failed to set job CPU rate for PID {Pid}: {Error}", pid, LastError());
                    Kernel32.CloseHandle(hJob);
                    return;
                }

                if (!Kernel32.AssignProcessToJobObject(hJob, p.Handle))
                {
                    _logger.LogError("Failed to assign process {Pid} to job object: {Error}", pid, LastError());
                    Kernel32.CloseHandle(hJob);
                    return;
                }

                _activeJobs[pid] = hJob;
                _activePercents[pid] = cpuLimitPercent;
                _logger.LogInformation("Limited PID {Pid} to {Limit}% CPU", pid, cpuLimitPercent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error limiting CPU for PID {Pid}", pid);
            }
        }
    }

    /// <inheritdoc/>
    public void RemoveLimit(int pid)
    {
        lock (_gate)
        {
            if (_activeJobs.Remove(pid, out var hJob))
            {
                _activePercents.Remove(pid);
                Release(hJob);
                _logger.LogInformation("Removed CPU limit for PID {Pid}", pid);
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, double> GetActiveLimits()
    {
        lock (_gate)
        {
            var limits = new Dictionary<int, double>(_activePercents.Count);
            foreach (var (pid, percent) in _activePercents) limits[pid] = percent;
            return limits;
        }
    }

    public void Dispose() => ReleaseAll();

    private void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var job in _activeJobs.Values) Release(job);
            _activeJobs.Clear();
            _activePercents.Clear();
        }
    }

    /// <summary>
    /// Closing the last handle to a job does NOT remove its limits while the process is still alive,
    /// so lift the cap first, then close.
    /// </summary>
    private static void Release(IntPtr hJob)
    {
        SetCpuRate(hJob, FullRate);
        Kernel32.CloseHandle(hJob);
    }

    private static bool SetCpuRate(IntPtr hJob, uint rate)
    {
        var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = Kernel32.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | Kernel32.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
            CpuRate = rate
        };

        int length = Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>();
        IntPtr pInfo = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, pInfo, false);
            return Kernel32.SetInformationJobObject(hJob, Kernel32.JobObjectCpuRateControlInformation, pInfo, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(pInfo);
        }
    }

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;
}
