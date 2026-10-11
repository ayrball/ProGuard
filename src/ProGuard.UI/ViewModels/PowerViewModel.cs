using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Shared.Messages;
using ProGuard.UI.IPC;

namespace ProGuard.UI.ViewModels;

public partial class PowerViewModel : ViewModelBase
{
    private static readonly Guid UltimatePerformanceGuid = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");

    [ObservableProperty]
    private bool isPerformanceModeEnabled;

    [ObservableProperty]
    private bool isIdleSaverEnabled;

    [ObservableProperty]
    private int idleTimeout = 300;

    [ObservableProperty]
    private int selectedIdlePlanIndex = 2;

    [ObservableProperty]
    private string currentPowerPlanText = "Current Power Plan: Detecting...";

    [ObservableProperty]
    private int selectedPlanIndex = 0;

    public ObservableCollection<PowerPlanItem> PowerPlans { get; set; } = new();

    private readonly EngineProxy _engine;
    private readonly Action<bool> _applyPerformanceModeLocally;

    /// <summary>The most recent Performance Mode change; tests await it.</summary>
    public Task PerformanceModeChange { get; private set; } = Task.CompletedTask;

    public PowerViewModel() : this(new EngineProxy()) { }

    /// <param name="applyPerformanceModeLocally">
    /// What to do when the engine can't be reached; defaults to switching the power plan from this process.
    /// </param>
    public PowerViewModel(EngineProxy engine, Action<bool>? applyPerformanceModeLocally = null)
    {
        _engine = engine;
        _applyPerformanceModeLocally = applyPerformanceModeLocally ?? SetPerformancePlanLocally;
        InitializePowerPlans();
        LoadSettings();
    }

    private void InitializePowerPlans()
    {
        PowerPlans.Clear();
        PowerPlans.Add(new PowerPlanItem { Name = "Balanced (Recommended)", Guid = Powrprof.Balanced, Index = 0 });
        PowerPlans.Add(new PowerPlanItem { Name = "High Performance", Guid = Powrprof.HighPerformance, Index = 1 });
        PowerPlans.Add(new PowerPlanItem { Name = "Power Saver", Guid = Powrprof.PowerSaver, Index = 2 });
        PowerPlans.Add(new PowerPlanItem { Name = "ProGuard Ultimate Performance", Guid = UltimatePerformanceGuid, Index = 3 });

        RefreshActivePlan();
    }

    public void RefreshActivePlan()
    {
        try
        {
            if (Powrprof.PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) == 0 && ptr != IntPtr.Zero)
            {
                var activeGuid = Marshal.PtrToStructure<Guid>(ptr);
                Marshal.FreeHGlobal(ptr);

                var match = PowerPlans.FirstOrDefault(p => p.Guid == activeGuid);
                if (match != null)
                {
                    CurrentPowerPlanText = $"Current Power Plan: {match.Name}";
                    SelectedPlanIndex = match.Index;
                    foreach (var p in PowerPlans) p.IsChecked = (p.Guid == activeGuid);
                }
                else
                {
                    CurrentPowerPlanText = $"Current Power Plan: Custom ({activeGuid.ToString().Substring(0, 8)}...)";
                }
            }
            else
            {
                CurrentPowerPlanText = "Current Power Plan: Balanced";
            }
        }
        catch
        {
            CurrentPowerPlanText = "Current Power Plan: Balanced";
        }
    }

    [RelayCommand]
    public void SelectPlan(int index)
    {
        if (index < 0 || index >= PowerPlans.Count) return;

        var target = PowerPlans[index];
        var guid = target.Guid;
        try
        {
            Powrprof.PowerSetActiveScheme(IntPtr.Zero, ref guid);
            CurrentPowerPlanText = $"Current Power Plan: {target.Name}";
            SelectedPlanIndex = index;

            foreach (var p in PowerPlans)
            {
                p.IsChecked = (p.Index == index);
            }
        }
        catch { }
    }

    partial void OnIsPerformanceModeEnabledChanged(bool value)
    {
        PerformanceModeChange = ApplyPerformanceModeAsync(PerformanceModeChange, value);
    }

    private async Task ApplyPerformanceModeAsync(Task previous, bool enable)
    {
        // Rapid toggles must reach the engine in order.
        await previous;

        // The engine owns Performance Mode: it runs with the service's privileges and remembers the plan to restore.
        CommandResult? result = null;
        try
        {
            result = await _engine.EnablePerformanceModeAsync(enable);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Performance Mode command failed: {ex.Message}");
        }

        if (result == null)
        {
            try { _applyPerformanceModeLocally(enable); }
            catch (Exception ex) { Debug.WriteLine($"Local Performance Mode switch failed: {ex.Message}"); }
        }
        else if (!result.Success)
        {
            Debug.WriteLine($"Engine refused Performance Mode change: {result.Error}");
        }

        RefreshActivePlan();
    }

    private static void SetPerformancePlanLocally(bool enable)
    {
        var plan = enable ? Powrprof.HighPerformance : Powrprof.Balanced;
        Powrprof.PowerSetActiveScheme(IntPtr.Zero, ref plan);
    }

    partial void OnIsIdleSaverEnabledChanged(bool value) => SaveSettings();
    partial void OnIdleTimeoutChanged(int value) => SaveSettings();
    partial void OnSelectedIdlePlanIndexChanged(int value) => SaveSettings();

    private bool _loading;

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();
            var s = db.Settings.FirstOrDefault();
            if (s != null)
            {
                IsIdleSaverEnabled = s.IdleSaverEnabled;
                IdleTimeout = s.IdleTimeoutMinutes * 60;
                if (Guid.TryParse(s.IdlePowerPlanGuid, out var idlePlan))
                {
                    var match = PowerPlans.FirstOrDefault(p => p.Guid == idlePlan);
                    if (match != null) SelectedIdlePlanIndex = match.Index;
                }
            }
        }
        catch { }
        finally
        {
            _loading = false;
        }
    }

    private void SaveSettings()
    {
        if (_loading) return;

        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();
            var s = db.Settings.FirstOrDefault();
            if (s != null)
            {
                s.IdleSaverEnabled = IsIdleSaverEnabled;
                s.IdleTimeoutMinutes = Math.Max(1, IdleTimeout / 60);
                // The engine reads the plan from the database; without this the idle plan choice never reached it.
                if (SelectedIdlePlanIndex >= 0 && SelectedIdlePlanIndex < PowerPlans.Count)
                    s.IdlePowerPlanGuid = PowerPlans[SelectedIdlePlanIndex].Guid.ToString();
                db.SaveChanges();
            }
        }
        catch { }
    }
}

public class PowerPlanItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public Guid Guid { get; set; }
    public int Index { get; set; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}
