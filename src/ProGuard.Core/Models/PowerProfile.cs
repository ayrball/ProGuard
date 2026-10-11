using System.ComponentModel.DataAnnotations;

namespace ProGuard.Core.Models;

public class PowerProfile
{
    [Key]
    public int Id { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string PowerPlanGuid { get; set; } = string.Empty;
    public bool EnablePerformanceMode { get; set; }
}
