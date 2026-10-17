using System.IO;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;
using ProGuard.Engine.Services;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

/// <summary>
/// powrprof in memory: plans, their processor settings (plugged in and on battery), the personalities' defaults, the
/// settings' ranges and attributes. Nothing here touches the machine's power plans.
/// </summary>
internal sealed class FakePowerSettings : IPowerSettingsApi
{
    public static readonly Guid Balanced = Powrprof.Balanced;
    public static readonly Guid High = Powrprof.HighPerformance;
    public static readonly Guid Saver = Powrprof.PowerSaver;
    /// <summary>A plan the user made from High performance.</summary>
    public static readonly Guid Custom = Guid.Parse("11111111-2222-3333-4444-555555555555");
    public static readonly Guid DistributeUtil = CpuTuningCatalog.Find("DISTRIBUTEUTIL")!.Id;

    public Guid Active { get; set; } = Balanced;
    public bool Hybrid { get; set; } = true;
    public Dictionary<(Guid Scheme, Guid Setting), AcDc> Values { get; } = new();
    public Dictionary<(Guid Personality, Guid Setting), AcDc> Defaults { get; } = new();
    public Dictionary<Guid, uint> Attributes { get; } = new();
    public Dictionary<Guid, Guid> Personalities { get; } = new() { [Balanced] = Balanced, [High] = High, [Saver] = Saver, [Custom] = High };
    public List<(Guid Scheme, Guid Setting, AcDc Values)> Writes { get; } = new();
    public List<(Guid Setting, uint Attributes)> AttributeWrites { get; } = new();
    public List<Guid> Reapplied { get; } = new();
    public HashSet<Guid> Refuse { get; } = new();

    public FakePowerSettings()
    {
        foreach (var s in CpuTuningCatalog.Settings)
        {
            if (s.Id == DistributeUtil) continue; // this "Windows" lacks one setting
            var balanced = s.Kind == CpuTuningKind.Choice ? new AcDc(1, 0) : s.Kind == CpuTuningKind.Count ? new AcDc(3, 7) : new AcDc(30, 20);
            var high = s.Kind == CpuTuningKind.Choice ? new AcDc(2, 2) : s.Kind == CpuTuningKind.Count ? new AcDc(1, 1) : new AcDc(100, 100);
            foreach (var plan in new[] { Balanced, Saver }) Values[(plan, s.Id)] = balanced;
            foreach (var plan in new[] { High, Custom }) Values[(plan, s.Id)] = high;
            Defaults[(Balanced, s.Id)] = balanced;
            Defaults[(Saver, s.Id)] = balanced;
            Defaults[(High, s.Id)] = high;
            // As on a real machine: most processor settings are hidden, the minimum and maximum state are not.
            Attributes[s.Id] = s.Key.StartsWith("PROCTHROTTLE", StringComparison.Ordinal) ? 2u : 1u;
        }
        // Balanced parks: 10 % of the cores plugged in, 5 % on battery.
        Values[(Balanced, CpuTuningCatalog.MinCores)] = new AcDc(10, 5);
        Values[(Balanced, CpuTuningCatalog.MinCores1)] = new AcDc(20, 10);
        Defaults[(Balanced, CpuTuningCatalog.MinCores)] = new AcDc(10, 5);
        Defaults[(Balanced, CpuTuningCatalog.MinCores1)] = new AcDc(20, 10);
    }

    public AcDc this[Guid scheme, string key] => Values[(scheme, CpuTuningCatalog.Find(key)!.Id)];

    public Guid ActiveScheme() => Active;
    public AcDc? Read(Guid scheme, Guid setting) => Values.TryGetValue((scheme, setting), out var v) ? v : null;

    public bool Write(Guid scheme, Guid setting, AcDc values)
    {
        if (Refuse.Contains(setting)) return false;
        Writes.Add((scheme, setting, values));
        Values[(scheme, setting)] = values;
        return true;
    }

    public void Reapply(Guid scheme) => Reapplied.Add(scheme);

    public IReadOnlyList<PowerPlanInfo> Plans() => new[]
    {
        new PowerPlanInfo(Balanced, "Balanced"), new PowerPlanInfo(High, "High performance"),
        new PowerPlanInfo(Saver, "Power saver"), new PowerPlanInfo(Custom, "My plan"),
    };

    public Guid Personality(Guid scheme) => Personalities.GetValueOrDefault(scheme);
    public AcDc? ReadDefault(Guid personality, Guid setting) => Defaults.TryGetValue((personality, setting), out var v) ? v : null;

    public PowerSettingRange? ReadRange(Guid setting)
    {
        var s = CpuTuningCatalog.Settings.First(x => x.Id == setting);
        return s.Kind == CpuTuningKind.Choice ? null : new PowerSettingRange(s.Min, s.Max, 1);
    }

    public uint ReadAttributes(Guid setting) => Attributes.GetValueOrDefault(setting);

    public bool WriteAttributes(Guid setting, uint attributes)
    {
        AttributeWrites.Add((setting, attributes));
        Attributes[setting] = attributes;
        return true;
    }

    public bool IsHybrid() => Hybrid;
}

/// <summary>
/// CPU tuning: a power plan's core parking and frequency settings, read and written per plan for AC and DC, with
/// presets, a record of the values before the first change, and no fight with ProBalance's temporary unparking.
/// </summary>
public class CpuTuningTests
{
    private static readonly Guid Balanced = FakePowerSettings.Balanced;
    private static readonly Guid High = FakePowerSettings.High;
    private static readonly Guid Custom = FakePowerSettings.Custom;
    private static readonly AcDc All = new(100, 100);

    private static (CpuTuningService Service, FakePowerSettings Power, MemoryCpuTuningOriginalsStore Store) New(CoreParkingService? coreParking = null, FakePowerSettings? power = null)
    {
        power ??= new FakePowerSettings();
        var store = new MemoryCpuTuningOriginalsStore();
        return (new CpuTuningService(power: power, originals: store, coreParking: coreParking), power, store);
    }

    private static CpuTuningArgument Values(Guid plan, params (string Key, uint? Ac, uint? Dc)[] values) =>
        new() { PlanId = plan, Values = values.Select(v => new CpuTuningValueDto { Key = v.Key, Ac = v.Ac, Dc = v.Dc }).ToList() };

    private static CpuTuningArgument Preset(Guid plan, string preset) => new() { PlanId = plan, Preset = preset };

    private static CpuTuningSettingDto Setting(CpuTuningDto dto, string key) => dto.Settings.Single(s => s.Key == key);

    // ------------------------------------------------------------------ reading

    [Fact]
    public void Get_ReadsEverySettingOfThePlan_PluggedInAndOnBattery()
    {
        var (service, _, _) = New();

        var dto = service.Get(null);

        Assert.Equal(Balanced, dto.PlanId);
        Assert.Equal("Balanced", dto.PlanName);
        Assert.True(dto.IsActivePlan);
        Assert.Equal(4, dto.Plans.Count);
        Assert.True(dto.IsHybrid);
        Assert.False(dto.HasOriginals);
        Assert.Equal(CpuTuningCatalog.Settings.Select(s => s.Key), dto.Settings.Select(s => s.Key));

        var minCores = Setting(dto, "CPMINCORES");
        Assert.True(minCores.Available);
        Assert.Equal((10u, 5u), (minCores.Ac, minCores.Dc));
        Assert.Equal((10u, 5u), (minCores.DefaultAc!.Value, minCores.DefaultDc!.Value));
        Assert.Equal((0u, 100u), (minCores.Min, minCores.Max));
        Assert.True(minCores.Hidden);
        Assert.Null(minCores.LiveAc);
        Assert.Null(minCores.OriginalAc);
        Assert.False(string.IsNullOrWhiteSpace(minCores.Description));

        Assert.False(Setting(dto, "PROCTHROTTLEMAX").Hidden);
        var boost = Setting(dto, "PERFBOOSTMODE");
        Assert.Equal(CpuTuningKind.Choice, boost.Kind);
        Assert.Equal(7, boost.Choices.Count);
        Assert.Equal(5u, Setting(dto, "CPOVERUTIL").Min);
    }

    [Fact]
    public void AnotherPlan_IsReadWithItsPersonalitysDefaults()
    {
        var (service, _, _) = New();

        var dto = service.Get(Custom);

        Assert.Equal("My plan", dto.PlanName);
        Assert.False(dto.IsActivePlan);
        Assert.Equal(100u, Setting(dto, "CPMINCORES").DefaultAc); // made from High performance
    }

    [Fact]
    public void SettingsTheMachineLacks_AreUnavailable_WithTheReason()
    {
        var (service, power, _) = New();
        power.Hybrid = false;

        var dto = service.Get(null);

        var missing = Setting(dto, "DISTRIBUTEUTIL");
        Assert.False(missing.Available);
        Assert.Contains("doesn't have", missing.UnavailableReason);

        // Class 1 twins mean nothing on a CPU with one kind of core.
        Assert.All(dto.Settings.Where(s => s.EfficiencyClass == 1), s =>
        {
            Assert.False(s.Available);
            Assert.Contains("two kinds of cores", s.UnavailableReason);
        });
        Assert.True(Setting(dto, "CPMINCORES").Available);
    }

    [Fact]
    public void APlanThatIsNotInstalled_IsRefused()
    {
        var (service, _, _) = New();
        var ex = Assert.Throws<ArgumentException>(() => service.Get(Guid.NewGuid()));
        Assert.Contains("isn't installed", ex.Message);
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public void Set_WritesPluggedInAndOnBatterySeparately_AndReappliesTheActivePlan()
    {
        var (service, power, _) = New();

        var change = service.Set(Values(Balanced, ("CPMINCORES", 50, null), ("PROCTHROTTLEMAX", null, 80)));

        Assert.Null(change.Error);
        Assert.Equal(new AcDc(50, 5), power[Balanced, "CPMINCORES"]);
        Assert.Equal(new AcDc(30, 80), power[Balanced, "PROCTHROTTLEMAX"]);
        Assert.Equal(new[] { Balanced }, power.Reapplied);
        Assert.Equal(Balanced, power.Active);
        Assert.Equal(50u, Setting(change.Tuning, "CPMINCORES").Ac);
        Assert.Contains("CPMINCORES", change.Summary);
    }

    [Fact]
    public void ChangingAnotherPlan_NeitherReappliesNorSwitchesToIt()
    {
        var (service, power, _) = New();

        service.Set(Values(High, ("CPMINCORES", 40, 40)));

        Assert.Equal(new AcDc(40, 40), power[High, "CPMINCORES"]);
        Assert.Empty(power.Reapplied);
        Assert.Equal(Balanced, power.Active);
    }

    [Theory]
    [InlineData("CPMINCORES", 101u, "between 0 and 100")]
    [InlineData("CPOVERUTIL", 4u, "between 5 and 100")]
    [InlineData("PERFBOOSTMODE", 7u, "choices")]
    [InlineData("NOSUCHSETTING", 1u, "Unknown CPU setting")]
    [InlineData("DISTRIBUTEUTIL", 1u, "doesn't have")]
    public void InvalidValues_AreRefused_AndNothingIsWritten(string key, uint value, string message)
    {
        var (service, power, store) = New();

        var ex = Assert.Throws<ArgumentException>(() => service.Set(Values(Balanced, ("CPMINCORES", 50, 50), (key, value, null))));

        Assert.Contains(message, ex.Message);
        Assert.Empty(power.Writes);
        Assert.Empty(store.Originals.Plans);
    }

    [Fact]
    public void AClass1Setting_CannotBeChangedOnACpuWithOneKindOfCore()
    {
        var (service, power, _) = New();
        power.Hybrid = false;
        var ex = Assert.Throws<ArgumentException>(() => service.Set(Values(Balanced, ("CPMINCORES1", 100, 100))));
        Assert.Contains("two kinds of cores", ex.Message);
    }

    [Fact]
    public void ASettingWindowsRefuses_IsReported_WhileTheRestAreWritten()
    {
        var (service, power, _) = New();
        power.Refuse.Add(CpuTuningCatalog.MinCores);

        var change = service.Set(Values(Balanced, ("CPMINCORES", 50, 50), ("CPMAXCORES", 90, 90)));

        Assert.Contains("CPMINCORES", change.Error);
        Assert.Equal(new AcDc(90, 90), power[Balanced, "CPMAXCORES"]);
    }

    // ------------------------------------------------------------------ the values before the first change

    [Fact]
    public void TheFirstChange_RecordsThePlansValues_AndRevertPutsThemBack()
    {
        var (service, power, store) = New();
        var before = CpuTuningCatalog.Settings.Where(s => power.Values.ContainsKey((Balanced, s.Id)))
            .ToDictionary(s => s.Id, s => power.Values[(Balanced, s.Id)]);

        service.Set(Values(Balanced, ("CPMINCORES", 50, 50)));
        service.Set(Values(Balanced, ("CPMINCORES", 70, 70), ("PERFEPP", 0, 0)));

        // Recorded once, before the first change: the second change didn't overwrite it.
        var record = store.Originals.Plans[Balanced];
        Assert.Equal(new AcDc(10, 5), record.Values[CpuTuningCatalog.MinCores]);
        Assert.Equal(before.Count, record.Values.Count);
        var dto = service.Get(Balanced);
        Assert.True(dto.HasOriginals);
        Assert.Equal(10u, Setting(dto, "CPMINCORES").OriginalAc);

        var reverted = service.Set(Preset(Balanced, CpuTuningPresets.Revert));

        Assert.Null(reverted.Error);
        Assert.All(before, b => Assert.Equal(b.Value, power.Values[(Balanced, b.Key)]));
        Assert.False(reverted.Tuning.HasOriginals);
        Assert.Empty(store.Originals.Plans);
    }

    [Fact]
    public void Revert_WithNothingRecorded_IsRefused()
    {
        var (service, _, _) = New();
        var ex = Assert.Throws<ArgumentException>(() => service.Set(Preset(Balanced, CpuTuningPresets.Revert)));
        Assert.Contains("nothing to revert", ex.Message);
    }

    [Fact]
    public void TheRecordFile_RoundTrips_AndIgnoresJunk()
    {
        var path = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", Guid.NewGuid().ToString("N"), "cpu-tuning-originals.json");
        try
        {
            var store = new FileCpuTuningOriginalsStore(path);
            Assert.Empty(store.Load().Plans);

            var originals = new CpuTuningOriginals();
            originals.Plans[Balanced] = new CpuTuningPlanOriginals { TakenUtc = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc), Values = { [CpuTuningCatalog.MinCores] = new AcDc(10, 5) } };
            originals.Unhidden[CpuTuningCatalog.MinCores] = 1;
            store.Save(originals);

            var loaded = store.Load();
            Assert.Equal(new AcDc(10, 5), loaded.Plans[Balanced].Values[CpuTuningCatalog.MinCores]);
            Assert.Equal(1u, loaded.Unhidden[CpuTuningCatalog.MinCores]);

            File.WriteAllText(path, "{ not json");
            Assert.Empty(store.Load().Plans);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // ------------------------------------------------------------------ hidden settings

    [Fact]
    public void AHiddenSettingThatIsChanged_IsShownInPowerOptions_UntilEveryPlanIsReverted()
    {
        var (service, power, store) = New();

        service.Set(Values(Balanced, ("CPMINCORES", 50, 50), ("PROCTHROTTLEMIN", 5, 5)));
        service.Set(Values(High, ("CPMINCORES", 50, 50)));

        Assert.Equal(0u, power.Attributes[CpuTuningCatalog.MinCores]);
        Assert.Equal(new[] { (CpuTuningCatalog.MinCores, 0u) }, power.AttributeWrites); // the visible one was left alone
        Assert.Equal(1u, store.Originals.Unhidden[CpuTuningCatalog.MinCores]);

        service.Set(Preset(Balanced, CpuTuningPresets.Revert));
        Assert.Equal(0u, power.Attributes[CpuTuningCatalog.MinCores]); // High performance is still tuned

        service.Set(Preset(High, CpuTuningPresets.Revert));
        Assert.Equal(1u, power.Attributes[CpuTuningCatalog.MinCores]);
        Assert.Empty(store.Originals.Unhidden);
    }

    // ------------------------------------------------------------------ presets

    [Fact]
    public void DisableParking_HoldsEveryCoreUnparked()
    {
        var (service, power, _) = New();

        var change = service.Set(Preset(Balanced, CpuTuningPresets.DisableParking));

        foreach (var key in new[] { "CPMINCORES", "CPMINCORES1", "CPMAXCORES", "CPMAXCORES1" })
            Assert.Equal(All, power[Balanced, key]);
        Assert.Equal(new AcDc(30, 20), power[Balanced, "PROCTHROTTLEMAX"]); // frequency untouched
        Assert.Contains("disable core parking", change.Summary);
    }

    [Fact]
    public void DisableParking_SkipsTheClass1Twins_OnACpuWithOneKindOfCore()
    {
        var (service, power, _) = New();
        power.Hybrid = false;
        service.Set(Preset(Balanced, CpuTuningPresets.DisableParking));
        Assert.Equal(All, power[Balanced, "CPMINCORES"]);
        Assert.Equal(new AcDc(20, 10), power[Balanced, "CPMINCORES1"]);
    }

    [Fact]
    public void BalancedParking_SetsOnlyTheParkingSettings_ToWindowsBalancedDefaults()
    {
        var (service, power, _) = New();

        service.Set(Preset(High, CpuTuningPresets.BalancedParking));

        Assert.Equal(new AcDc(10, 5), power[High, "CPMINCORES"]);
        Assert.Equal(new AcDc(30, 20), power[High, "CPCONCURRENCY"]);
        Assert.Equal(new AcDc(1, 0), power[High, "CPINCREASEPOL"]);
        Assert.Equal(All, power[High, "PROCTHROTTLEMIN"]); // frequency untouched
    }

    [Fact]
    public void WindowsDefaults_UsesThePlansPersonality()
    {
        var (service, power, _) = New();
        power.Values[(Custom, CpuTuningCatalog.MinCores)] = new AcDc(5, 5);
        power.Values[(Custom, CpuTuningCatalog.Find("PERFBOOSTMODE")!.Id)] = new AcDc(0, 0);

        service.Set(Preset(Custom, CpuTuningPresets.WindowsDefaults));

        Assert.Equal(All, power[Custom, "CPMINCORES"]);
        Assert.Equal(new AcDc(2, 2), power[Custom, "PERFBOOSTMODE"]);
    }

    [Fact]
    public void WindowsDefaults_ForAPlanOfUnknownOrigin_IsRefused()
    {
        var (service, power, _) = New();
        power.Personalities.Remove(Custom);
        var ex = Assert.Throws<ArgumentException>(() => service.Set(Preset(Custom, CpuTuningPresets.WindowsDefaults)));
        Assert.Contains("no defaults", ex.Message);
        Assert.Empty(power.Writes);
    }

    // ------------------------------------------------------------------ ProBalance's temporary unparking

    private static (CpuTuningService Tuning, CoreParkingService Parking, FakePowerSettings Power, MemoryCoreParkingRecoveryStore Recovery, MemoryCpuTuningOriginalsStore Store) WithParking()
    {
        var power = new FakePowerSettings();
        var recovery = new MemoryCoreParkingRecoveryStore();
        var parking = new CoreParkingService(power: power, recovery: recovery);
        parking.Configure(true);
        var (tuning, _, store) = New(parking, power);
        return (tuning, parking, power, recovery, store);
    }

    [Fact]
    public void WhileProBalanceHoldsCoresUnparked_TheUsersValuesAreShown_WithTheLiveOnes()
    {
        var (tuning, parking, _, _, _) = WithParking();
        parking.Update(true);

        var dto = tuning.Get(Balanced);

        Assert.True(dto.ParkingOverrideActive);
        var minCores = Setting(dto, "CPMINCORES");
        Assert.Equal((10u, 5u), (minCores.Ac, minCores.Dc));
        Assert.Equal((100u, 100u), (minCores.LiveAc!.Value, minCores.LiveDc!.Value));
        Assert.Null(Setting(dto, "CPMAXCORES").LiveAc);
        Assert.False(tuning.Get(High).ParkingOverrideActive);
    }

    [Fact]
    public void ANewMinimumDuringTheHold_DoesNotFightIt_AndIsWhatComesBack()
    {
        var (tuning, parking, power, recovery, _) = WithParking();
        parking.Update(true);
        power.Reapplied.Clear();

        var change = tuning.Set(Values(Balanced, ("CPMINCORES", 30, 25), ("CPMAXCORES", 90, 90)));

        Assert.Null(change.Error);
        Assert.Equal(All, power[Balanced, "CPMINCORES"]);              // still held
        Assert.Equal(new AcDc(90, 90), power[Balanced, "CPMAXCORES"]); // not part of the hold: written
        Assert.Equal(new AcDc(30, 25), recovery.Record!.Originals[CpuTuningCatalog.MinCores]); // survives a crash too
        Assert.Equal(30u, Setting(change.Tuning, "CPMINCORES").Ac);
        Assert.Equal(new[] { Balanced }, power.Reapplied);

        parking.Update(false);

        Assert.Equal(new AcDc(30, 25), power[Balanced, "CPMINCORES"]); // the tuned value, not the one the hold found
        Assert.Equal(new AcDc(20, 10), power[Balanced, "CPMINCORES1"]);
    }

    [Fact]
    public void ADeferredChangeOnly_DoesNotReapplyThePlan()
    {
        var (tuning, parking, power, _, _) = WithParking();
        parking.Update(true);
        power.Reapplied.Clear();

        tuning.Set(Values(Balanced, ("CPMINCORES", 30, 30)));

        Assert.Empty(power.Reapplied);
    }

    [Fact]
    public void TheRecordTakenDuringAHold_HoldsThePlansOwnValues_NotTheOverride()
    {
        var (tuning, parking, power, _, store) = WithParking();
        parking.Update(true);

        tuning.Set(Values(Balanced, ("PERFEPP", 10, 10)));
        Assert.Equal(new AcDc(10, 5), store.Originals.Plans[Balanced].Values[CpuTuningCatalog.MinCores]);

        tuning.Set(Values(Balanced, ("CPMINCORES", 60, 60)));
        parking.Update(false);
        tuning.Set(Preset(Balanced, CpuTuningPresets.Revert));

        Assert.Equal(new AcDc(10, 5), power[Balanced, "CPMINCORES"]);
        Assert.Equal(new AcDc(30, 20), power[Balanced, "PERFEPP"]);
    }

    [Fact]
    public void TuningBeforeAHold_IsWhatTheHoldPutsBack()
    {
        var (tuning, parking, power, _, _) = WithParking();
        tuning.Set(Values(Balanced, ("CPMINCORES", 40, 40)));

        parking.Update(true);
        Assert.Equal(All, power[Balanced, "CPMINCORES"]);
        parking.Update(false);

        Assert.Equal(new AcDc(40, 40), power[Balanced, "CPMINCORES"]);
    }

    // ------------------------------------------------------------------ over the pipe

    [Fact]
    public void AnyoneMayRead_ButOnlyAnAdministratorMayChange()
    {
        var h = new ThinHarness();

        var read = h.Data<CpuTuningDto>(h.Send(new ThinCaller(), CommandTypes.GetCpuTuning, argument: Balanced.ToString()));
        Assert.Equal(Balanced, read.PlanId);

        var argument = Values(Balanced, ("CPMINCORES", 100, 100));
        var refused = h.Send(new ThinCaller(), CommandTypes.SetCpuTuning, argument: argument);
        Assert.False(refused.Success);
        Assert.StartsWith("Access denied", refused.Error);
        Assert.Contains("core parking", refused.Error);
        Assert.Empty(h.PowerSettings.Writes);

        var done = h.Data<CpuTuningDto>(h.SendAsAdmin(CommandTypes.SetCpuTuning, argument: argument));
        Assert.Equal(100u, Setting(done, "CPMINCORES").Ac);
        Assert.Equal(All, h.PowerSettings[Balanced, "CPMINCORES"]);
        Assert.Contains(h.Manual.GetRecent(10), e => e.Message.Contains("CPMINCORES"));
    }

    [Fact]
    public void OverThePipe_BadRequestsAreRefusedWithTheReason()
    {
        var h = new ThinHarness();
        Assert.Contains("Invalid power plan", h.SendAsAdmin(CommandTypes.GetCpuTuning, argument: "not a guid").Error);
        Assert.Contains("isn't installed", h.SendAsAdmin(CommandTypes.GetCpuTuning, argument: Guid.NewGuid().ToString()).Error);
        Assert.Contains("between 0 and 100", h.SendAsAdmin(CommandTypes.SetCpuTuning, argument: Values(Balanced, ("CPMINCORES", 200, 0))).Error);
        Assert.Equal(Balanced, h.Data<CpuTuningDto>(h.SendAsAdmin(CommandTypes.GetCpuTuning)).PlanId); // no plan = the active one
    }

    // ------------------------------------------------------------------ the real machine, read only

    /// <summary>The real API, with every call that could change something made to fail the test.</summary>
    private sealed class ReadOnlyPowerSettings : IPowerSettingsApi
    {
        private readonly Win32PowerSettings _real = new();
        public Guid ActiveScheme() => _real.ActiveScheme();
        public AcDc? Read(Guid scheme, Guid setting) => _real.Read(scheme, setting);
        public IReadOnlyList<PowerPlanInfo> Plans() => _real.Plans();
        public Guid Personality(Guid scheme) => _real.Personality(scheme);
        public AcDc? ReadDefault(Guid personality, Guid setting) => _real.ReadDefault(personality, setting);
        public PowerSettingRange? ReadRange(Guid setting) => _real.ReadRange(setting);
        public uint ReadAttributes(Guid setting) => _real.ReadAttributes(setting);
        public bool IsHybrid() => _real.IsHybrid();
        public bool Write(Guid scheme, Guid setting, AcDc values) => throw new InvalidOperationException("A test must not change a real power plan");
        public void Reapply(Guid scheme) => throw new InvalidOperationException("A test must not re-apply a real power plan");
        public bool WriteAttributes(Guid setting, uint attributes) => throw new InvalidOperationException("A test must not change a real power setting's attributes");
    }

    [Fact]
    public void Windows_ReportsTheActivePlansCpuSettings()
    {
        var power = new ReadOnlyPowerSettings();
        var service = new CpuTuningService(power: power, originals: new MemoryCpuTuningOriginalsStore());

        var dto = service.Get(null);

        Assert.Equal(power.ActiveScheme(), dto.PlanId);
        Assert.True(dto.IsActivePlan);
        Assert.Contains(dto.Plans, p => p.Id == dto.PlanId);
        Assert.False(string.IsNullOrWhiteSpace(dto.PlanName));

        // Every plan has these (inheriting the defaults if it doesn't set them), and reading needs no rights.
        foreach (var key in new[] { "CPMINCORES", "PROCTHROTTLEMIN", "PROCTHROTTLEMAX" })
            Assert.True(Setting(dto, key).Available, $"{key}: {Setting(dto, key).UnavailableReason}");
        Assert.All(dto.Settings.Where(s => s.Available && s.Kind != CpuTuningKind.Choice), s =>
        {
            Assert.InRange(s.Ac, s.Min, s.Max);
            Assert.InRange(s.Dc, s.Min, s.Max);
        });
        Assert.All(dto.Settings.Where(s => !s.Available), s => Assert.False(string.IsNullOrWhiteSpace(s.UnavailableReason)));
    }
}
