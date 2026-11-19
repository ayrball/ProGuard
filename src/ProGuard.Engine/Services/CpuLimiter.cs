using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Limits CPU usage of specific processes using Windows Job Objects (hard CPU-rate cap).
/// <para>
/// A cap outlives the engine: the process stays in its job, capped, after the engine's handle is gone. So every cap is
/// recorded (<see cref="ICpuCapRecoveryStore"/>) with what it takes to find its job again (see <see cref="CpuCapNative"/>),
/// and <see cref="RecoverFromLastRun"/> lifts, at the next start, any cap a crash or kill left in force on a process
/// that is still running, after checking the process's creation time so a pid that was reused is left alone.
/// </para>
/// </summary>
public class CpuLimiter : ICpuLimiter, IDisposable
{
    private const uint FullRate = 10000; // 100% of total CPU, in 1/100ths of a percent

    private readonly ILogger<CpuLimiter> _logger;
    private readonly ICpuCapRecoveryStore? _recovery;
    private readonly object _gate = new();
    private readonly Dictionary<int, Cap> _activeJobs = new();

    private sealed record Cap(IntPtr Job, int Percent, CpuCapRecord Record);

    /// <summary>A limiter that keeps no recovery record (tests, and tools that cap something for a moment).</summary>
    public CpuLimiter(ILogger<CpuLimiter> logger) : this(logger, null) { }

    internal CpuLimiter(ILogger<CpuLimiter> logger, ICpuCapRecoveryStore? recovery)
    {
        _logger = logger;
        _recovery = recovery;
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

    /// <summary>
    /// Lifts the caps an earlier run recorded and never lifted (it crashed, or was killed), on the processes that are
    /// still running. Call once at start, before any new cap. Returns how many were lifted.
    /// </summary>
    public int RecoverFromLastRun()
    {
        if (_recovery == null) return 0;
        var lifted = 0;
        lock (_gate)
        {
            foreach (var record in _recovery.Load())
            {
                try
                {
                    if (record.JobName == null || !record.JobName.StartsWith(CpuCapNative.JobNamePrefix, StringComparison.Ordinal)) continue;
                    // The same process, not a later one that was given its pid.
                    if (CpuCapNative.CreationTimeOf(record.Pid) != record.CreationTime)
                    {
                        _logger.LogDebug("Process {Pid} is gone, or another process has its id: no CPU limit to lift", record.Pid);
                        continue;
                    }
                    var job = CpuCapNative.OpenJob(record.JobName);
                    if (job == IntPtr.Zero)
                    {
                        _logger.LogDebug("The CPU limit job of process {Pid} no longer exists: {Error}", record.Pid, LastError());
                        continue;
                    }
                    try
                    {
                        if (SetCpuRate(job, FullRate))
                        {
                            lifted++;
                            _logger.LogWarning("Lifted the CPU limit an earlier run of the engine left on process {Pid}", record.Pid);
                        }
                        else _logger.LogWarning("Could not lift the CPU limit an earlier run left on process {Pid}: {Error}", record.Pid, LastError());
                        CpuCapNative.RemoveHandleFrom(record.Pid, record.RemoteHandle, job);
                    }
                    finally { Kernel32.CloseHandle(job); }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not lift the CPU limit an earlier run left on process {Pid}", record.Pid);
                }
            }
            Persist();
        }
        return lifted;
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
                    if (SetCpuRate(existing.Job, rate))
                    {
                        _activeJobs[pid] = existing with { Percent = cpuLimitPercent };
                        _logger.LogInformation("Updated CPU limit for PID {Pid} to {Limit}%", pid, cpuLimitPercent);
                    }
                    else
                        _logger.LogError("Failed to update CPU limit for PID {Pid}: {Error}", pid, LastError());
                    return;
                }

                using var p = Process.GetProcessById(pid);
                if (CpuCapNative.CreationTimeOf(pid) is not ulong created)
                {
                    _logger.LogError("Failed to limit PID {Pid}: it can't be opened ({Error})", pid, LastError());
                    return;
                }

                // A random name, so the job can be found again after a crash; nobody else's object may answer to it.
                var name = CpuCapNative.NewJobName();
                var hJob = CpuCapNative.CreateNamedJob(name);
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

                // Keeps the job's name alive for as long as the process runs, whatever happens to the engine.
                var remote = CpuCapNative.PlaceHandleIn(pid, hJob);
                if (remote == 0)
                    _logger.LogWarning("If the engine stops without lifting it, the CPU limit on PID {Pid} can't be lifted by a later run: {Error}", pid, LastError());

                _activeJobs[pid] = new Cap(hJob, cpuLimitPercent, new CpuCapRecord(pid, created, name, remote));
                Persist();
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
            if (_activeJobs.Remove(pid, out var cap))
            {
                Release(cap);
                Persist();
                _logger.LogInformation("Removed CPU limit for PID {Pid}", pid);
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, double> GetActiveLimits()
    {
        lock (_gate)
        {
            var limits = new Dictionary<int, double>(_activeJobs.Count);
            foreach (var (pid, cap) in _activeJobs) limits[pid] = cap.Percent;
            return limits;
        }
    }

    /// <summary>The job name of the cap on <paramref name="pid"/>, for tests.</summary>
    internal string? JobNameOf(int pid)
    {
        lock (_gate) return _activeJobs.TryGetValue(pid, out var cap) ? cap.Record.JobName : null;
    }

    /// <summary>What a crash does: closes the job handles without lifting anything, and leaves the record. For tests.</summary>
    internal void AbandonForTest()
    {
        lock (_gate)
        {
            foreach (var cap in _activeJobs.Values) Kernel32.CloseHandle(cap.Job);
            _activeJobs.Clear();
        }
    }

    public void Dispose() => ReleaseAll();

    private void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var cap in _activeJobs.Values) Release(cap);
            _activeJobs.Clear();
            Persist();
        }
    }

    private void Persist() => _recovery?.Save(_activeJobs.Values.Select(c => c.Record).ToList());

    /// <summary>
    /// Closing the last handle to a job does NOT remove its limits while the process is still alive,
    /// so lift the cap first, then close.
    /// </summary>
    private static void Release(Cap cap)
    {
        SetCpuRate(cap.Job, FullRate);
        try { CpuCapNative.RemoveHandleFrom(cap.Record.Pid, cap.Record.RemoteHandle, cap.Job); }
        catch (Exception) { /* the process is gone, or won't let us in: its handle goes when it does */ }
        Kernel32.CloseHandle(cap.Job);
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
