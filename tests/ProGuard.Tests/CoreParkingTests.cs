using System.IO;
using ProGuard.Engine.Services;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

/// <summary>Power plans' processor settings in memory.</summary>
internal sealed class FakeCoreParkingPower : ICoreParkingPower
{
    public static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public Guid Active { get; set; } = Balanced;
    public Dictionary<(Guid Scheme, Guid Setting), AcDc> Values { get; } = new();
    public int Writes { get; private set; }
    public List<Guid> Reapplied { get; } = new();

    public FakeCoreParkingPower()
    {
        // Balanced parks down to 10 % plugged in and 5 % on battery; a hybrid CPU's second class has its own value.
        Values[(Balanced, CoreParkingService.MinCores)] = new AcDc(10, 5);
        Values[(Balanced, CoreParkingService.MinCores1)] = new AcDc(20, 10);
        Values[(HighPerformance, CoreParkingService.MinCores)] = new AcDc(50, 50);
    }

    public Guid ActiveScheme() => Active;
    public AcDc? Read(Guid scheme, Guid setting) => Values.TryGetValue((scheme, setting), out var v) ? v : null;

    public bool Write(Guid scheme, Guid setting, AcDc values)
    {
        Writes++;
        Values[(scheme, setting)] = values;
        return true;
    }

    public void Reapply(Guid scheme) => Reapplied.Add(scheme);

    public AcDc this[Guid scheme, Guid setting] => Values[(scheme, setting)];
}

/// <summary>
/// ProBalance's OocDisableCoreParkingWhileIn: the active plan's core parking minimum goes to 100 % while anything is
/// restrained and comes back afterwards, also after an engine crash. Only administrators may switch it on.
/// </summary>
public class CoreParkingTests
{
    private static readonly AcDc All = new(100, 100);
    private static readonly Guid Balanced = FakeCoreParkingPower.Balanced;
    private static readonly Guid High = FakeCoreParkingPower.HighPerformance;
    private static readonly Guid MinCores = CoreParkingService.MinCores;
    private static readonly Guid MinCores1 = CoreParkingService.MinCores1;

    private static (CoreParkingService Service, FakeCoreParkingPower Power, MemoryCoreParkingRecoveryStore Store) New(bool enabled = true)
    {
        var power = new FakeCoreParkingPower();
        var store = new MemoryCoreParkingRecoveryStore();
        var service = new CoreParkingService(power: power, recovery: store);
        service.Configure(enabled);
        return (service, power, store);
    }

    [Fact]
    public void Off_ByDefault_AsInProcessLasso()
    {
        Assert.False(new ProGuard.Core.Models.AppSettings().ProBalanceDisableCoreParking);
        Assert.False(new EngineSettingsDto().ProBalanceDisableCoreParking);

        var (service, power, _) = New(enabled: false);
        service.Update(true);
        Assert.Equal(0, power.Writes);
        Assert.Null(service.HeldScheme);
    }

    [Fact]
    public void WhileAnythingIsRestrained_EveryCoreStaysUnparked_ThenThePlanGetsItsValuesBack()
    {
        var (service, power, store) = New();

        service.Update(true);
        Assert.Equal(All, power[Balanced, MinCores]);
        Assert.Equal(All, power[Balanced, MinCores1]);
        Assert.Equal(new[] { Balanced }, power.Reapplied);
        Assert.Equal(Balanced, store.Record!.Scheme);
        Assert.Equal(new AcDc(10, 5), store.Record.Originals[MinCores]);

        // Every pass reports again: nothing more is written while the state holds.
        int writes = power.Writes;
        service.Update(true);
        Assert.Equal(writes, power.Writes);

        service.Update(false);
        Assert.Equal(new AcDc(10, 5), power[Balanced, MinCores]);
        Assert.Equal(new AcDc(20, 10), power[Balanced, MinCores1]);
        Assert.Null(store.Record);
        Assert.Null(service.HeldScheme);
        Assert.Equal(2, power.Reapplied.Count);
    }

    [Fact]
    public void AValueChangedMeanwhile_IsLeftAsTheUserSetIt()
    {
        var (service, power, _) = New();
        service.Update(true);
        power.Values[(Balanced, MinCores)] = new AcDc(40, 100); // the user changed the plugged-in value only

        service.Update(false);
        Assert.Equal(new AcDc(40, 5), power[Balanced, MinCores]);
        Assert.Equal(new AcDc(20, 10), power[Balanced, MinCores1]);
    }

    [Fact]
    public void WhenThePlanChanges_TheOldPlanIsRestored_AndTheNewOneUnparked()
    {
        var (service, power, store) = New();
        service.Update(true);

        power.Active = High; // Performance Mode switched plans
        service.Update(true);

        Assert.Equal(new AcDc(10, 5), power[Balanced, MinCores]);
        Assert.Equal(All, power[High, MinCores]);
        Assert.Equal(High, store.Record!.Scheme);

        service.Update(false);
        Assert.Equal(new AcDc(50, 50), power[High, MinCores]);
    }

    [Fact]
    public void TurningItOff_OrStopping_PutsTheValuesBack()
    {
        var (service, power, store) = New();
        service.Update(true);
        service.Configure(false);
        Assert.Equal(new AcDc(10, 5), power[Balanced, MinCores]);
        Assert.Null(store.Record);

        // Back on while still restraining: applies at once.
        service.Configure(true);
        Assert.Equal(All, power[Balanced, MinCores]);

        service.Stop();
        Assert.Equal(new AcDc(10, 5), power[Balanced, MinCores]);
    }

    [Fact]
    public void APlanThatNeverParks_OrHasNoSuchSetting_IsLeftAlone()
    {
        var (service, power, store) = New();
        power.Values[(Balanced, MinCores)] = All;
        power.Values[(Balanced, MinCores1)] = All;
        service.Update(true);
        Assert.Null(store.Record);

        power.Active = Guid.Parse("11111111-2222-3333-4444-555555555555");
        service.Update(true);
        Assert.Equal(0, power.Writes);
        Assert.Null(service.HeldScheme);
    }

    [Fact]
    public void AfterACrash_TheNextStartPutsTheValuesBack_UnlessChangedSince()
    {
        // The last run died holding Balanced unparked.
        var power = new FakeCoreParkingPower();
        power.Values[(Balanced, MinCores)] = All;
        power.Values[(Balanced, MinCores1)] = new AcDc(30, 100); // the user changed one since
        var store = new MemoryCoreParkingRecoveryStore
        {
            Record = new CoreParkingRecord(Balanced, new() { [MinCores] = new AcDc(10, 5), [MinCores1] = new AcDc(20, 10) }),
        };

        new CoreParkingService(power: power, recovery: store).RecoverFromLastRun();

        Assert.Equal(new AcDc(10, 5), power[Balanced, MinCores]);
        Assert.Equal(new AcDc(30, 10), power[Balanced, MinCores1]);
        Assert.Contains(Balanced, power.Reapplied);
        Assert.Null(store.Record);
    }

    [Fact]
    public void TheRecoveryFile_RoundTrips_AndIgnoresJunk()
    {
        var path = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", Guid.NewGuid().ToString("N"), "core-parking-recovery.json");
        try
        {
            var store = new FileCoreParkingRecoveryStore(path);
            Assert.Null(store.Load());

            store.Save(new CoreParkingRecord(Balanced, new() { [MinCores] = new AcDc(10, 5) }));
            var loaded = store.Load()!;
            Assert.Equal(Balanced, loaded.Scheme);
            Assert.Equal(new AcDc(10, 5), loaded.Originals[MinCores]);

            File.WriteAllText(path, "{ not json");
            Assert.Null(store.Load());

            store.Clear();
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Windows_ReportsTheActivePlansCoreParkingMinimum()
    {
        var power = new Win32CoreParkingPower();
        var active = power.ActiveScheme();
        Assert.NotEqual(Guid.Empty, active);
        // Reading needs no rights; every plan inherits the setting's default if it doesn't set it.
        var values = power.Read(active, MinCores);
        Assert.NotNull(values);
        Assert.InRange(values!.Value.Ac, 0u, 100u);
        Assert.InRange(values.Value.Dc, 0u, 100u);
    }

    // ------------------------------------------------------------------ ProBalance drives it

    [Fact]
    public void ProBalance_ReportsAfterEachPass_WhetherAnythingIsRestrained()
    {
        using var h = new ProBalanceHarness();
        var reports = new List<bool>();
        h.Engine.RestraintsUpdated += reports.Add;
        h.Engine.TimeOverQuotaBeforeRestraint = 0;
        var hog = h.Add(100, cpu: 50);

        h.At(0);
        h.At(1000);
        Assert.True(h.Engine.IsEngaged);
        Assert.True(reports[^1]);

        hog.CpuPercent = 0;
        h.At(10_000);
        Assert.False(h.Engine.IsEngaged);
        Assert.False(reports[^1]);

        // Lifting restraints outside a pass reports too.
        hog.CpuPercent = 50;
        h.At(11_000);
        h.At(12_000);
        Assert.True(reports[^1]);
        h.Engine.SetEnabled(false);
        Assert.False(reports[^1]);
    }

    [Fact]
    public void ProBalanceAndCoreParking_TogetherUnparkAndRestore()
    {
        using var h = new ProBalanceHarness();
        var (service, power, _) = New();
        h.Engine.RestraintsUpdated += service.Update;
        h.Engine.TimeOverQuotaBeforeRestraint = 0;
        var hog = h.Add(100, cpu: 50);

        h.At(0);
        h.At(1000);
        Assert.Equal(All, power[Balanced, MinCores]);

        hog.CpuPercent = 0;
        h.At(10_000);
        Assert.Equal(new AcDc(10, 5), power[Balanced, MinCores]);
    }

    // ------------------------------------------------------------------ who may switch it on

    [Fact]
    public void OnlyAnAdministrator_MaySwitchItOn()
    {
        var h = new ThinHarness();
        var settings = h.Data<EngineSettingsDto>(h.SendAsAdmin(CommandTypes.GetSettings));
        settings.ProBalanceDisableCoreParking = true;

        var refused = h.Send(new ThinCaller(), CommandTypes.SaveSettings, argument: settings);
        Assert.Contains("Access denied", refused.Error);
        Assert.Contains("unparks cores", refused.Error);
        Assert.False(h.Stored.ProBalanceDisableCoreParking);

        Assert.True(h.SendAsAdmin(CommandTypes.SaveSettings, argument: settings).Success);
        Assert.True(h.Stored.ProBalanceDisableCoreParking);

        // Once on, a standard user can still save other settings, sending it back as it is.
        settings.SmartTrimEnabled = true;
        Assert.True(h.Send(new ThinCaller(), CommandTypes.SaveSettings, argument: settings).Success);
    }

    [Fact]
    public void TheSettingReachesTheService()
    {
        var (service, _, _) = New(enabled: false);
        var applier = new SettingsApplier(Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsApplier>.Instance, new FakeProcessMonitor(),
            new Moq.Mock<ProGuard.Core.Services.ISmartTrimService>().Object, new Moq.Mock<ProGuard.Core.Services.IPowerManager>().Object,
            new Moq.Mock<ProGuard.Core.Services.IProBalanceEngine>().Object, coreParking: service);

        applier.Apply(new ProGuard.Core.Models.AppSettings { ProBalanceDisableCoreParking = true });
        Assert.True(service.IsEnabled);
    }
}
