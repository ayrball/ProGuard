using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.UI.Converters;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

public class Pass1_UI_NavigationAndViewModels_Tests
{
    [Fact]
    public void DashboardViewModel_Initializes_WithExpectedDefaults()
    {
        var vm = new DashboardViewModel();

        Assert.NotNull(vm.CpuSeries);
        Assert.NotNull(vm.RamSeries);
        Assert.NotEmpty(vm.CpuSeries);
        Assert.NotEmpty(vm.RamSeries);
        Assert.NotNull(vm.TopConsumers);
        Assert.NotEmpty(vm.TopConsumers);
        Assert.NotNull(vm.CpuUsageText);
        Assert.NotNull(vm.RamUsageText);
    }

    [Fact]
    public void ProcessListViewModel_SearchFilter_FiltersCorrectly()
    {
        var vm = new ProcessListViewModel();

        // Add dummy process items
        vm.ClearProcesses();
        vm.AddProcess(new ProcessDisplayItem { Name = "chrome.exe", Pid = 100, CpuPercent = "1.5%" });
        vm.AddProcess(new ProcessDisplayItem { Name = "steam.exe", Pid = 200, CpuPercent = "0.5%" });
        vm.AddProcess(new ProcessDisplayItem { Name = "discord.exe", Pid = 300, CpuPercent = "2.0%" });

        // Filter for "chrome"
        vm.SearchText = "chrome";

        Assert.Single(vm.Processes);
        Assert.Equal("chrome.exe", vm.Processes[0].Name);

        // Reset filter
        vm.SearchText = "";
        Assert.Equal(3, vm.Processes.Count);
    }

    [Fact]
    public void RulesViewModel_CrudOperations_Work()
    {
        var vm = new RulesViewModel();
        Assert.NotNull(vm.Rules);

        var initialCount = vm.Rules.Count;
        var rule = new RuleDisplayItem
        {
            ProcessName = "test_game.exe",
            Priority = "High",
            Affinity = "All",
            CpuLimit = "80%"
        };

        vm.Rules.Add(rule);
        Assert.Equal(initialCount + 1, vm.Rules.Count);

        vm.Rules.Remove(rule);
        Assert.Equal(initialCount, vm.Rules.Count);
    }

    [Fact]
    public void WatchdogViewModel_Toggle_UpdatesRuleState()
    {
        var vm = new WatchdogViewModel();
        Assert.NotNull(vm.WatchdogRules);

        if (vm.WatchdogRules.Count > 0)
        {
            var firstRule = vm.WatchdogRules[0];
            bool origState = firstRule.IsEnabled;
            firstRule.IsEnabled = !origState;
            Assert.Equal(!origState, firstRule.IsEnabled);
        }
    }

    [Fact]
    public void PowerViewModel_PerformanceMode_TogglesCorrectly()
    {
        var vm = new PowerViewModel();
        bool initialState = vm.IsPerformanceModeEnabled;

        vm.IsPerformanceModeEnabled = !initialState;
        Assert.Equal(!initialState, vm.IsPerformanceModeEnabled);
    }

    [Fact]
    public void Converters_RenderExpectedOutput()
    {
        // Bytes to readable
        var bytesConverter = new BytesToReadableConverter();
        var readable1 = bytesConverter.Convert(1024L * 1024L * 500L, typeof(string), null!, null!);
        Assert.Contains("500", readable1?.ToString() ?? "");

        var readable2 = bytesConverter.Convert(1024L * 1024L * 1024L * 2L, typeof(string), null!, null!);
        Assert.Contains("2", readable2?.ToString() ?? "");

        // Priority to Color
        var priorityConverter = new PriorityToColorConverter();
        var highBrush = priorityConverter.Convert(ProcessPriorityClass.High, typeof(Brush), null!, null!);
        Assert.NotNull(highBrush);

        var idleBrush = priorityConverter.Convert(ProcessPriorityClass.Idle, typeof(Brush), null!, null!);
        Assert.NotNull(idleBrush);

        // Bool to Visibility
        var visConverter = new BoolToVisibilityConverter();
        Assert.Equal(Visibility.Visible, visConverter.Convert(true, typeof(Visibility), null!, null!));
        Assert.Equal(Visibility.Collapsed, visConverter.Convert(false, typeof(Visibility), null!, null!));

        // CPU % to Color
        var cpuColorConverter = new CpuPercentToColorConverter();
        var lowCpu = cpuColorConverter.Convert(5.0, typeof(Brush), null!, null!);
        var highCpu = cpuColorConverter.Convert(95.0, typeof(Brush), null!, null!);
        Assert.NotNull(lowCpu);
        Assert.NotNull(highCpu);
    }

    [Fact]
    public void SystemInfo_Provides_Real_Hardware_Metrics()
    {
        Assert.True(ProGuard.Core.Helpers.SystemInfo.LogicalProcessorCount > 0);
        Assert.True(ProGuard.Core.Helpers.SystemInfo.TotalPhysicalMemoryBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(ProGuard.Core.Helpers.SystemInfo.CpuName));

        var ramLoad = ProGuard.Core.Helpers.SystemInfo.GetMemoryLoadPercent();
        Assert.InRange(ramLoad, 0.0, 100.0);

        var cpuLoad = ProGuard.Core.Helpers.SystemInfo.GetOverallCpuPercent();
        Assert.InRange(cpuLoad, 0.0, 100.0);
    }

    [Fact]
    public void ProcessListViewModel_Filters_User_And_System_Processes()
    {
        using var vm = new ProcessListViewModel();
        vm.ClearProcesses();

        vm.AddProcess(new ProcessDisplayItem { Name = "notepad.exe", Pid = 101, IsSystem = false });
        vm.AddProcess(new ProcessDisplayItem { Name = "svchost.exe", Pid = 102, IsSystem = true });
        vm.AddProcess(new ProcessDisplayItem { Name = "game.exe", Pid = 103, IsSystem = false });

        // Filter 1 = User Processes
        vm.SelectedFilterIndex = 1;
        Assert.Equal(2, vm.Processes.Count);
        Assert.All(vm.Processes, p => Assert.False(p.IsSystem));

        // Filter 2 = System Processes
        vm.SelectedFilterIndex = 2;
        Assert.Single(vm.Processes);
        Assert.True(vm.Processes[0].IsSystem);

        // Filter 0 = All
        vm.SelectedFilterIndex = 0;
        Assert.Equal(3, vm.Processes.Count);
    }

    [Fact]
    public void PowerViewModel_Initializes_With_Available_Plans()
    {
        var vm = new PowerViewModel();
        Assert.NotNull(vm.PowerPlans);
        Assert.True(vm.PowerPlans.Count >= 3);
        Assert.Contains(vm.PowerPlans, p => p.Name.Contains("Balanced"));
        Assert.Contains(vm.PowerPlans, p => p.Name.Contains("High Performance"));
    }

    [Fact]
    public void SettingsViewModel_Initializes_And_Loads_Correctly()
    {
        var vm = new SettingsViewModel();
        Assert.True(vm.CpuThreshold >= 10 && vm.CpuThreshold <= 95);
        Assert.True(vm.Sensitivity >= 1 && vm.Sensitivity <= 10);
    }

    [Fact]
    public void LogsViewModel_Loads_And_Filters_Correctly()
    {
        // The log view shows what is in the database and nothing else (it no longer invents placeholder
        // entries), so give it a real entry to load. TestEnvironment points this at a throw-away database.
        using (var db = new ProGuardDbContext())
        {
            db.Database.EnsureCreated();
            db.ProBalanceActions.Add(new ProBalanceAction
            {
                Timestamp = DateTime.UtcNow,
                ProcessName = "System",
                Pid = 4,
                OriginalPriority = (int)ProcessPriorityClass.Normal,
                NewPriority = (int)ProcessPriorityClass.BelowNormal,
                Reason = "High CPU usage (50.0%)"
            });
            db.SaveChanges();
        }

        var vm = new LogsViewModel();
        Assert.NotNull(vm.LogEntries);
        Assert.NotEmpty(vm.LogEntries);

        vm.SearchText = "System";
        Assert.All(vm.LogEntries, l => Assert.True(l.Message.Contains("System", StringComparison.OrdinalIgnoreCase) || l.ProcessName.Contains("System", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ProcessListViewModel_StressUpdates_MaintainsIntegrity()
    {
        using var vm = new ProcessListViewModel();
        vm.ClearProcesses();

        for (int i = 1; i <= 500; i++)
        {
            vm.AddProcess(new ProcessDisplayItem
            {
                Name = $"proc_{i}.exe",
                Pid = i,
                CpuPercent = $"{i % 10}.0%",
                MemoryText = $"{i * 2} MB",
                Priority = "Normal",
                AffinityText = "All Cores",
                Status = "Running"
            });
        }

        Assert.Equal(500, vm.Processes.Count);

        vm.SearchText = "proc_1";
        Assert.True(vm.Processes.Count > 0);
        Assert.All(vm.Processes, p => Assert.Contains("proc_1", p.Name));

        vm.SearchText = string.Empty;
        Assert.Equal(500, vm.Processes.Count);
    }

    [Fact]
    public void SystemInfo_CpuTopology_DetectsPhysicalAndSmtCores()
    {
        var topology = ProGuard.Core.Helpers.SystemInfo.GetCpuTopology();
        Assert.NotNull(topology);
        Assert.True(topology.PhysicalCoreCount > 0);
        Assert.True(topology.LogicalProcessorCount >= topology.PhysicalCoreCount);
        Assert.NotEqual(0L, topology.PhysicalCoresMask);
        Assert.NotEqual(0L, topology.AllCoresMask);

        // Verify physical and SMT masks are disjoint
        if (topology.IsSmtSupported)
        {
            Assert.NotEqual(0L, topology.SmtSiblingsMask);
            Assert.Equal(0L, topology.PhysicalCoresMask & topology.SmtSiblingsMask);
            Assert.Equal(topology.AllCoresMask, topology.PhysicalCoresMask | topology.SmtSiblingsMask);
        }

        Assert.Equal(topology.LogicalProcessorCount, topology.Cores.Count);
    }

    [Fact]
    public void ProcessListViewModel_FormatAffinityText_FormatsPhysicalAndSmtCorrectly()
    {
        var topology = ProGuard.Core.Helpers.SystemInfo.GetCpuTopology();

        Assert.Equal("All Cores", ProcessListViewModel.FormatAffinityText(topology.AllCoresMask));
        Assert.Equal("All Cores", ProcessListViewModel.FormatAffinityText(-1L));
        Assert.Equal("Physical (No SMT)", ProcessListViewModel.FormatAffinityText(topology.PhysicalCoresMask));

        if (topology.IsSmtSupported)
        {
            Assert.Equal("SMT Siblings", ProcessListViewModel.FormatAffinityText(topology.SmtSiblingsMask));
        }
    }

    [Fact]
    public void ProcessListViewModel_TreeView_OrdersHierarchicallyAndIndents()
    {
        using var vm = new ProcessListViewModel();
        vm.ClearProcesses();

        var root = new ProcessDisplayItem { Name = "main_host.exe", Pid = 1000, ParentPid = 0 };
        var child1 = new ProcessDisplayItem { Name = "worker_a.exe", Pid = 1001, ParentPid = 1000 };
        var child2 = new ProcessDisplayItem { Name = "subworker.exe", Pid = 1002, ParentPid = 1001 };
        var other = new ProcessDisplayItem { Name = "independent.exe", Pid = 2000, ParentPid = 0 };

        vm.AddProcess(other);
        vm.AddProcess(child2);
        vm.AddProcess(root);
        vm.AddProcess(child1);

        vm.IsTreeView = true;

        Assert.Equal(4, vm.Processes.Count);

        // In tree view, worker_a should be child of main_host
        var hostIndex = vm.Processes.ToList().FindIndex(p => p.Pid == 1000);
        var child1Index = vm.Processes.ToList().FindIndex(p => p.Pid == 1001);
        var child2Index = vm.Processes.ToList().FindIndex(p => p.Pid == 1002);

        Assert.True(hostIndex < child1Index);
        Assert.True(child1Index < child2Index);

        var hostItem = vm.Processes[hostIndex];
        var child1Item = vm.Processes[child1Index];
        var child2Item = vm.Processes[child2Index];

        Assert.Equal("main_host.exe", hostItem.DisplayName);
        Assert.Contains("↳", child1Item.DisplayName);
        Assert.Contains("↳", child2Item.DisplayName);
        Assert.True(child1Item.TreeLevel == 1);
        Assert.True(child2Item.TreeLevel == 2);
    }

    [Fact]
    public void ProcessListViewModel_FilterPresets_ActiveCpuAndHeavyRam()
    {
        using var vm = new ProcessListViewModel();
        vm.ClearProcesses();

        vm.AddProcess(new ProcessDisplayItem { Name = "idle.exe", Pid = 1, CpuPercent = "0.0%", WorkingSetBytes = 10L * 1024 * 1024 });
        vm.AddProcess(new ProcessDisplayItem { Name = "active.exe", Pid = 2, CpuPercent = "4.2%", WorkingSetBytes = 50L * 1024 * 1024 });
        vm.AddProcess(new ProcessDisplayItem { Name = "heavy.exe", Pid = 3, CpuPercent = "0.0%", WorkingSetBytes = 800L * 1024 * 1024 });

        // Filter index 3 = Active (CPU > 0%)
        vm.SelectedFilterIndex = 3;
        Assert.Single(vm.Processes);
        Assert.Equal(2, vm.Processes[0].Pid);

        // Filter index 4 = Heavy RAM (> 500 MB)
        vm.SelectedFilterIndex = 4;
        Assert.Single(vm.Processes);
        Assert.Equal(3, vm.Processes[0].Pid);

        // Clear search command works
        vm.SearchText = "something";
        Assert.Empty(vm.Processes);
        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Single(vm.Processes); // still under filter 4
    }

    [Fact]
    public void ProcessListViewModel_InspectorCard_UpdatesOnSelection()
    {
        using var vm = new ProcessListViewModel();
        vm.ClearProcesses();

        var item = new ProcessDisplayItem
        {
            Name = "service.exe",
            Pid = 999,
            ParentPid = 100,
            ThreadCount = 14,
            HandleCount = 250,
            StartTimeText = "10:30:00",
            IoPriority = "High",
            MemoryPriority = 4
        };

        vm.AddProcess(item);
        Assert.False(vm.HasSelectedProcess);

        vm.SelectedProcess = item;
        Assert.True(vm.HasSelectedProcess);
        Assert.Equal(999, vm.SelectedProcess.Pid);
        Assert.Equal(14, vm.SelectedProcess.ThreadCount);
        Assert.Equal(250, vm.SelectedProcess.HandleCount);
        Assert.Equal("High", vm.SelectedProcess.IoPriority);
        Assert.Equal(4, vm.SelectedProcess.MemoryPriority);
    }

    [Fact]
    public void ProcessIconCache_ReturnsValidFrozenIcon()
    {
        var icon = ProGuard.UI.Services.ProcessIconCache.GetIcon(null, "explorer.exe");
        Assert.NotNull(icon);
        Assert.True(icon.IsFrozen);

        var cached = ProGuard.UI.Services.ProcessIconCache.GetIcon(null, "explorer.exe");
        Assert.Same(icon, cached);
    }

    [Fact]
    public void ProcessListViewModel_IsActive_TogglesCorrectly()
    {
        using var vm = new ProcessListViewModel();
        Assert.True(vm.IsActive);

        vm.IsActive = false;
        Assert.False(vm.IsActive);

        vm.IsActive = true;
        Assert.True(vm.IsActive);
    }

    [Fact]
    public void DashboardViewModel_IsActive_TogglesCorrectly()
    {
        using var vm = new DashboardViewModel();
        Assert.True(vm.IsActive);

        vm.IsActive = false;
        Assert.False(vm.IsActive);

        vm.IsActive = true;
        Assert.True(vm.IsActive);
    }
}
