using System;
using System.ComponentModel.DataAnnotations;

namespace ProGuard.Core.Models;

public class ProBalanceAction
{
    /// <summary><see cref="ProcessName"/> of the entries that record a power plan change (Pid 0, no priorities).</summary>
    public const string PowerPlanEntryName = "Power plan";

    [Key]
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public int Pid { get; set; }
    public int OriginalPriority { get; set; }
    public int NewPriority { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool IsRestored { get; set; }
}
