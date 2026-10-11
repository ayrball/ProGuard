using System;
using System.Diagnostics;

namespace ProGuard.Shared.Contracts;

public class ProcessSnapshot
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public string? Description { get; set; }
    public string? Company { get; set; }
    public double CpuPercent { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public int? PriorityClass { get; set; }
    public long AffinityMask { get; set; }
    public int? ParentPid { get; set; }
    public DateTime StartTime { get; set; }
    public string? UserName { get; set; }
    public bool IsForeground { get; set; }
    public string Status { get; set; } = "Unknown";
}
