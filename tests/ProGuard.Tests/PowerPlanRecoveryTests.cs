using System.IO;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

/// <summary>
/// PowerManager's crash recovery: whichever feature switched away from the user's plan (IdleSaver, Performance Mode or
/// the load switcher), the user's plan is recorded before the switch, so if the engine dies holding it the next start
/// restores it, unless a start-with plan is configured or the plan was changed since. A clean stop restores it and
/// leaves no record. Plans, idle time, CPU load and the clock are fakes; the record is kept in memory, except in the
/// tests of the file store itself.
/// </summary>
public class PowerPlanRecoveryTests
{
    private static readonly Guid Balanced = Powrprof.Balanced;
    private static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid LoadHigh = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid LoadLow = new("66666666-7777-8888-9999-000000000000");
    private const int IdleMinutes = 10;

    private sealed class FakeIdle : IUserIdleSource
    {
        public TimeSpan? Idle { get; set; } = TimeSpan.Zero;
        public TimeSpan? GetIdleTime() => Idle;
    }

    private sealed class FakePlans
    {
        public Guid Active { get; set; } = Balanced;
        public List<Guid> Writes { get; } = new();
        public bool Fail { get; set; }
        public Action<Guid>? BeforeWrite { get; set; }
        public bool Write(Guid plan)
        {
            BeforeWrite?.Invoke(plan);
            if (Fail) return false;
            Writes.Add(plan);
            Active = plan;
            return true;
        }
    }

    private readonly FakeIdle _idle = new();
    private readonly FakePlans _plans = new();
    private readonly MemoryPowerPlanRecoveryStore _recovery = new();
    private double _cpu;
    private TimeSpan _now = TimeSpan.FromHours(1);
    private readonly PowerManager _power;

    public PowerPlanRecoveryTests()
    {
        _power = NewEngineRun();
        _power.ConfigureIdleSaver(true, IdleMinutes, Powrprof.PowerSaver);
    }

    /// <summary>A PowerManager as a fresh engine process would create it: same machine, same recovery store.</summary>
    private PowerManager NewEngineRun(IPowerPlanRecoveryStore? store = null) =>
        new(null, _idle, () => _plans.Active, _plans.Write, () => _cpu, () => _now, recoveryStore: store ?? _recovery);

    /// <summary>Starts a new engine run after the previous one died without StopAsync, then stops it again.</summary>
    private async Task RestartAfterCrash(Guid startWithPlan = default, IPowerPlanRecoveryStore? store = null)
    {
        var next = NewEngineRun(store);
        next.ConfigureStartupPlan(startWithPlan);
        await next.StartAsync(CancellationToken.None);
        await next.StopAsync(CancellationToken.None);
    }

    private void Away() { _idle.Idle = TimeSpan.FromMinutes(IdleMinutes + 1); _power.CheckIdle(); }
    private void Back() { _idle.Idle = TimeSpan.FromSeconds(1); _power.CheckIdle(); }

    private void EnableLoadSwitcher() => _power.ConfigureLoadSwitcher(new LoadSwitcherOptions
    {
        Enabled = true,
        LowThreshold = 10,
        HighThreshold = 50,
        AveragingWindowSamples = 1,
        MinimumTimeInPlanMs = 0,
        LowPlan = LoadLow,
        HighPlan = LoadHigh,
        DeferLowPlan = false,
    });

    private void Load(double cpu)
    {
        _cpu = cpu;
        _now += TimeSpan.FromSeconds(1);
        _power.SampleLoad();
    }

    // ---------- a crash while each feature holds the plan ----------

    [Fact]
    public async Task CrashDuringIdleSaver_NextStartRestoresTheUsersPlan()
    {
        Away();
        Assert.Equal(new PendingPlanRestore(Powrprof.PowerSaver, Balanced), _recovery.Pending);

        await RestartAfterCrash();
        Assert.Equal(Balanced, _plans.Active);
        Assert.Null(_recovery.Pending);
    }

    [Fact]
    public async Task CrashDuringPerformanceMode_NextStartRestoresTheUsersPlan()
    {
        _power.EnablePerformanceMode(true);
        Assert.Equal(new PendingPlanRestore(Powrprof.HighPerformance, Balanced), _recovery.Pending);

        await RestartAfterCrash();
        Assert.Equal(Balanced, _plans.Active);
        Assert.Null(_recovery.Pending);
    }

    [Fact]
    public async Task CrashDuringTheLoadSwitcher_NextStartRestoresTheUsersPlan()
    {
        EnableLoadSwitcher();
        Load(80);
        Assert.Equal(new PendingPlanRestore(LoadHigh, Balanced), _recovery.Pending);
        Load(2);
        Assert.Equal(new PendingPlanRestore(LoadLow, Balanced), _recovery.Pending);

        await RestartAfterCrash();
        Assert.Equal(Balanced, _plans.Active);
        Assert.Null(_recovery.Pending);
    }

    [Fact]
    public async Task CrashAfterAHandover_NextStartRestoresTheUsersOwnPlan_NotTheEarlierFeatures()
    {
        _plans.Active = Ultimate;
        EnableLoadSwitcher();
        Load(80);
        Away();
        _power.EnablePerformanceMode(true);
        Assert.Equal(new PendingPlanRestore(Powrprof.HighPerformance, Ultimate), _recovery.Pending);

        await RestartAfterCrash();
        Assert.Equal(Ultimate, _plans.Active);
    }

    [Fact]
    public void TheUsersPlan_IsRecordedBeforeTheSwitch()
    {
        PendingPlanRestore? recordedAtSwitch = null;
        _plans.BeforeWrite = _ => recordedAtSwitch = _recovery.Pending;
        Away();
        Assert.Equal(new PendingPlanRestore(Powrprof.PowerSaver, Balanced), recordedAtSwitch);
    }

    [Fact]
    public void PeriodicChecks_DoNotRewriteTheRecord()
    {
        Away();
        for (var i = 0; i < 10; i++) _power.CheckIdle();
        Assert.Equal(1, _recovery.Saves);
    }

    // ---------- a clean exit, and the record going away ----------

    [Fact]
    public async Task CleanStop_RestoresTheUsersPlan_AndLeavesNothingToRecover()
    {
        await _power.StartAsync(CancellationToken.None);
        Away();
        await _power.StopAsync(CancellationToken.None);
        Assert.Equal(Balanced, _plans.Active);
        Assert.Null(_recovery.Pending);

        _plans.Active = Ultimate;
        await RestartAfterCrash();
        Assert.Equal(Ultimate, _plans.Active);
    }

    [Fact]
    public void RestoringOnReturn_ClearsTheRecord()
    {
        Away();
        Back();
        Assert.Equal(Balanced, _plans.Active);
        Assert.Null(_recovery.Pending);
    }

    [Fact]
    public void AManualChange_ClearsTheRecord_SinceNothingIsOwedAnyMore()
    {
        Away();
        _plans.Active = Ultimate;       // changed in Windows' power options
        _power.CheckIdle();
        Assert.Null(_recovery.Pending);

        Back();
        Away();
        _power.SetActivePlan(Balanced); // changed from the UI
        Assert.Null(_recovery.Pending);
    }

    // ---------- the next start ----------

    [Fact]
    public async Task AStartWithPlan_TakesPriorityOverTheRecord()
    {
        Away();
        await RestartAfterCrash(startWithPlan: Ultimate);
        Assert.Equal(Ultimate, _plans.Active);
        Assert.Equal(new[] { Powrprof.PowerSaver, Ultimate }, _plans.Writes);
        Assert.Null(_recovery.Pending);
    }

    [Fact]
    public async Task APlanChangedSinceTheCrash_IsLeftAlone()
    {
        Away();
        _plans.Active = Ultimate;

        await RestartAfterCrash();
        Assert.Equal(Ultimate, _plans.Active);
        Assert.Equal(new[] { Powrprof.PowerSaver }, _plans.Writes);
        Assert.Null(_recovery.Pending);     // given up on, not retried on every start
    }

    [Fact]
    public async Task AFailedRecovery_KeepsTheRecord_ForTheNextStart()
    {
        Away();
        _plans.Fail = true;
        await RestartAfterCrash();
        Assert.Equal(Powrprof.PowerSaver, _plans.Active);
        Assert.NotNull(_recovery.Pending);

        _plans.Fail = false;
        await RestartAfterCrash();
        Assert.Equal(Balanced, _plans.Active);
        Assert.Null(_recovery.Pending);
    }

    [Fact]
    public async Task AFailedRecovery_StillRestoresTheUsersPlan_AfterTheNextFeatureLetsGo()
    {
        Away();
        _plans.Fail = true;
        var next = NewEngineRun();
        await next.StartAsync(CancellationToken.None);
        _plans.Fail = false;

        // Performance Mode takes over from the plan the last run left on, so the plan to return to is the user's.
        next.EnablePerformanceMode(true);
        Assert.Equal(new PendingPlanRestore(Powrprof.HighPerformance, Balanced), _recovery.Pending);
        next.EnablePerformanceMode(false);
        Assert.Equal(Balanced, _plans.Active);
        Assert.Null(_recovery.Pending);
        await next.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFailedRestoreAtStop_KeepsTheRecord_ForTheNextStart()
    {
        await _power.StartAsync(CancellationToken.None);
        Away();
        _plans.Fail = true;
        await _power.StopAsync(CancellationToken.None);
        Assert.Equal(new PendingPlanRestore(Powrprof.PowerSaver, Balanced), _recovery.Pending);

        _plans.Fail = false;
        await RestartAfterCrash();
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public async Task ACorruptRecordFile_CountsAsNoRecord()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "power-recovery.json");
            File.WriteAllText(path, "{ \"Applied\": \"e9a42b02-d5df");
            _plans.Active = Powrprof.PowerSaver;

            await RestartAfterCrash(store: new FilePowerPlanRecoveryStore(path));
            Assert.Equal(Powrprof.PowerSaver, _plans.Active);
            Assert.Empty(_plans.Writes);
        }
        finally { DeleteDir(dir); }
    }

    // ---------- the file store ----------

    [Fact]
    public void FileStore_RoundTrips_AndLeavesNoTemporaryFile()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "sub", "power-recovery.json");
            var store = new FilePowerPlanRecoveryStore(path);
            Assert.Null(store.Load());

            store.Save(new PendingPlanRestore(Powrprof.PowerSaver, Balanced));
            store.Save(new PendingPlanRestore(Powrprof.HighPerformance, Balanced));
            Assert.Equal(new PendingPlanRestore(Powrprof.HighPerformance, Balanced), new FilePowerPlanRecoveryStore(path).Load());
            Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));

            store.Clear();
            Assert.Null(store.Load());
            store.Clear();      // nothing there: still fine
        }
        finally { DeleteDir(dir); }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("{}")]
    [InlineData("{ \"Applied\": \"00000000-0000-0000-0000-000000000000\", \"Restore\": \"381b4222-f694-41f0-9685-ff5bb260df2e\" }")]
    [InlineData("{ \"Applied\": \"a1841308-3541-4fab-bc81-f71556f20b4a\", \"Restore\": \"not a guid\" }")]
    public void FileStore_TreatsAnUnusableFileAsNoRecord(string content)
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "power-recovery.json");
            File.WriteAllText(path, content);
            Assert.Null(new FilePowerPlanRecoveryStore(path).Load());
        }
        finally { DeleteDir(dir); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort cleanup of a temp folder */ }
    }
}
