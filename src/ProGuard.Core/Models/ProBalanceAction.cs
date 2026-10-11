using System;
using System.ComponentModel.DataAnnotations;

namespace ProGuard.Core.Models;

public class ProBalanceAction
{
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
