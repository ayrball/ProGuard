using System;
using System.Collections.Generic;

namespace ProGuard.Core.Services;

public interface IPowerManager
{
    IEnumerable<(Guid Id, string Name)> GetAvailablePlans();
    Guid GetActivePlan();
    void SetActivePlan(Guid planId);
    void EnablePerformanceMode(bool enable);
    void ConfigureIdleSaver(bool enable, int timeoutMinutes, Guid idlePlanId);
}
