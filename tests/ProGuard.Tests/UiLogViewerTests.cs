using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ProGuard.Preview;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;
using ProGuard.UI.Views;

namespace ProGuard.Tests;

/// <summary>
/// The ADVANCED page and its LOGS section, driven against the stand-in engine on a private pipe: loading, paging, the
/// filters, the live tail, copy, export and clearing the action log.
/// </summary>
public class UiLogViewerTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public StandInEngine Engine { get; }
        public ShellViewModel Shell { get; }
        public LogsViewModel Logs => Shell.Advanced.Logs;

        public Rig(bool administrator = true, bool readOnly = true)
        {
            Engine = new StandInEngine("ProGuard.Tests.Logs." + Guid.NewGuid().ToString("N"), administrator, readOnly).Start();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var toaster = new Toaster(dispatcher);
            var hub = new EngineHub(new EngineClient(Engine.PipeName), dispatcher, toaster);
            Shell = new ShellViewModel(hub, toaster, new IconCache(dispatcher));
        }

        public async ValueTask DisposeAsync()
        {
            await Shell.Hub.DisposeAsync();
            await Engine.DisposeAsync();
        }
    }

    /// <summary>A shell whose engine never answers, for tests that don't talk to it.</summary>
    private static ShellViewModel NoEngineShell()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var toaster = new Toaster(dispatcher);
        var hub = new EngineHub(new EngineClient("ProGuard.Tests.NoEngine." + Guid.NewGuid().ToString("N")), dispatcher, toaster);
        return new ShellViewModel(hub, toaster, new IconCache(dispatcher));
    }

    [Fact]
    public void Shell_HasAnAdvancedPage_WithLogsAsItsFirstSection()
    {
        var shell = NoEngineShell();

        var page = shell.Pages.Last();
        Assert.Equal(("07", "ADVANCED"), (page.Number, page.Title));
        Assert.Same(shell.Advanced, page.Page);
        Assert.Equal("LOGS", shell.Advanced.Sections[0].Label);
        Assert.Same(shell.Advanced.Logs, shell.Advanced.CurrentSection.Page);
    }

    [Fact]
    public async Task Reload_ShowsTheNewestPage_AndScrollingPagesOlderEntriesIn()
    {
        await using var rig = new Rig();
        var vm = rig.Logs;

        await vm.ReloadAsync();
        Assert.True(vm.EngineLogIncluded);
        Assert.False(vm.IsEmpty);
        var first = vm.Entries.Count;

        while (vm.HasOlder) await vm.LoadOlderAsync();

        Assert.True(vm.Entries.Count >= first);
        Assert.Equal(vm.Entries.Count, vm.Entries.Select(r => r.Id).Distinct().Count());
        Assert.Equal(vm.Entries.OrderByDescending(r => r.Entry.TimestampUtc).Select(r => r.Id), vm.Entries.Select(r => r.Id));
        Assert.Contains(vm.Entries, r => r.Source == "ENGINE");
        Assert.Contains(vm.Entries, r => r.Source == "ACTIONS");
    }

    [Fact]
    public void Filters_BecomeTheQuery()
    {
        var vm = NoEngineShell().Advanced.Logs;
        Assert.Null(vm.BuildQuery().Levels); // every level on: no level filter at all

        vm.Source = vm.Sources.Single(s => s.Value == LogSource.Engine);
        foreach (var level in vm.Levels) level.IsOn = level.Level >= LogLevelDto.Warning;
        vm.Range = vm.Ranges.Single(r => r.Label == "1 HOUR");
        vm.Search = "  locked ";
        vm.Process = " 7220";
        vm.Category = "RuleStore";

        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var query = vm.BuildQuery(now);

        Assert.Equal(new[] { LogSource.Engine }, query.Sources);
        Assert.Equal(new[] { LogLevelDto.Warning, LogLevelDto.Error, LogLevelDto.Critical }, query.Levels);
        Assert.Equal(now.AddHours(-1), query.FromUtc);
        Assert.Equal(("locked", "7220", "RuleStore"), (query.Text, query.Process, query.Category));
        Assert.Null(query.Before);
        Assert.Null(query.After);

        vm.ClearFiltersCommand.Execute(null);
        var cleared = vm.BuildQuery(now);
        Assert.Null(cleared.Sources);
        Assert.Null(cleared.Levels);
        Assert.Null(cleared.FromUtc);
        Assert.Null(cleared.Text);
    }

    [Fact]
    public async Task Filters_AreAppliedByTheEngine()
    {
        await using var rig = new Rig();
        var vm = rig.Logs;
        foreach (var level in vm.Levels) level.IsOn = level.Level >= LogLevelDto.Error;

        await vm.ReloadAsync();

        Assert.NotEmpty(vm.Entries);
        Assert.All(vm.Entries, r => Assert.True(r.IsError));
    }

    [Fact]
    public async Task EveryLevelOff_ShowsNothing_InsteadOfEverything()
    {
        await using var rig = new Rig();
        var vm = rig.Logs;
        foreach (var level in vm.Levels) level.IsOn = false;

        await vm.ReloadAsync();

        Assert.Empty(vm.Entries);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task LiveTail_PutsNewEntriesOnTop_AndPausingHoldsTheTableStill()
    {
        await using var rig = new Rig(readOnly: false); // the stand-in writes a line every third tail request
        var vm = rig.Logs;
        await vm.ReloadAsync();
        var top = vm.Entries[0].Id;

        for (int i = 0; i < 3; i++) await vm.TailAsync();
        Assert.Equal(1, vm.NewSinceResume);
        Assert.NotEqual(top, vm.Entries[0].Id);
        Assert.Equal(top, vm.Entries[1].Id);

        await vm.ToggleLiveCommand.ExecuteAsync(null);
        Assert.False(vm.IsLive);
        var count = vm.Entries.Count;
        for (int i = 0; i < 3; i++) await vm.TailAsync();
        Assert.Equal(count, vm.Entries.Count);
    }

    [Fact]
    public async Task Selection_SurvivesAReload()
    {
        await using var rig = new Rig();
        var vm = rig.Logs;
        await vm.ReloadAsync();
        vm.Selected = vm.Entries[3];
        var id = vm.Selected.Id;

        await vm.ReloadAsync();

        Assert.Equal(id, vm.Selected?.Id);
    }

    [Fact]
    public async Task StandardUser_IsToldTheEngineLogIsLeftOut()
    {
        await using var rig = new Rig(administrator: false);
        var vm = rig.Logs;

        await vm.ReloadAsync();

        Assert.False(vm.EngineLogIncluded);
        Assert.All(vm.Entries, r => Assert.Equal("ACTIONS", r.Source));
    }

    [Fact]
    public async Task CopyRow_PutsTheWholeEntryOnTheClipboard()
    {
        await using var rig = new Rig();
        var vm = rig.Logs;
        string? copied = null;
        vm.CopyText = text => copied = text;
        await vm.ReloadAsync();
        Assert.False(vm.CopySelectedCommand.CanExecute(null));

        while (vm.HasOlder) await vm.LoadOlderAsync();
        vm.Selected = vm.Entries.First(r => r.IsError && r.HasDetail);
        vm.CopySelectedCommand.Execute(null);

        Assert.NotNull(copied);
        var lines = copied!.Split(Environment.NewLine);
        Assert.Equal(vm.Selected.FullTime, lines[0].Split('\t')[0]);
        Assert.Contains("\tERROR\tENGINE\tRuleStore\t", lines[0]);
        Assert.Contains("database is locked", copied);
    }

    [Fact]
    public async Task Export_SavesTheEnginesCsv_WhereTheUserPicks()
    {
        await using var rig = new Rig();
        var vm = rig.Logs;
        var dir = Path.Combine(Path.GetTempPath(), "ProGuard.Tests.Export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string? suggested = null;
            vm.PickExportPath = name => { suggested = name; return Path.Combine(dir, "out.csv"); };

            await vm.ExportCommand.ExecuteAsync(null);

            Assert.StartsWith("proguard-log-", suggested);
            Assert.EndsWith(".csv", suggested);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(dir, "out.csv"));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // so Excel reads UTF-8
            Assert.StartsWith("TimestampUtc,LocalTime,Source,Level,Category,Process,PID,Message,Detail\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

            // Cancelling the save dialog writes nothing.
            vm.PickExportPath = _ => null;
            File.Delete(Path.Combine(dir, "out.csv"));
            await vm.ExportCommand.ExecuteAsync(null);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ClearActionLog_AsksFirst_ThenEmptiesTheActionLog()
    {
        await using var rig = new Rig(readOnly: false);
        var vm = rig.Logs;
        await vm.ReloadAsync();
        Assert.Contains(vm.Entries, r => r.Source == "ACTIONS");

        vm.ClearActionLogCommand.Execute(null);
        var confirm = Assert.IsType<ConfirmDialogViewModel>(rig.Shell.Dialog);
        await confirm.ConfirmCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.Entries, r => r.Source == "ACTIONS");
        Assert.Contains(vm.Entries, r => r.Source == "ENGINE");
    }

    [Fact]
    public void ClearActionLog_IsForAdministratorsOnly()
    {
        var vm = NoEngineShell().Advanced.Logs;
        vm.IsAdministrator = false;
        Assert.False(vm.ClearActionLogCommand.CanExecute(null));
    }

    [Fact]
    public void Rows_ReadWell()
    {
        var today = new DateTime(2026, 10, 16);
        var entry = new LogEntryDto
        {
            Id = "f:1", TimestampUtc = new DateTime(2026, 10, 16, 9, 30, 5, 120, DateTimeKind.Local).ToUniversalTime(), Source = LogSource.Engine,
            Level = LogLevelDto.Warning, Category = "Cat", ProcessName = "chrome", Pid = 7220, Message = "two\r\nlines",
        };

        var row = new LogRow(entry, today);
        Assert.Equal("09:30:05.120", row.Time);
        Assert.Equal("2026-10-16 09:30:05.120", row.FullTime);
        Assert.Equal(("WRN", "WARNING", "ENGINE"), (row.Level, row.LevelName, row.Source));
        Assert.Equal("chrome · 7220", row.Process);
        Assert.Equal("two lines", row.Message);
        Assert.True(row.IsWarning);
        Assert.False(row.IsError || row.IsQuiet);

        Assert.Equal("10-16 09:30:05", new LogRow(entry, today.AddDays(1)).Time);
        Assert.Equal("7220", new LogRow(new LogEntryDto { Pid = 7220, Message = "" }).Process);
        Assert.Equal("—", new LogRow(new LogEntryDto { Message = "" }).Process);
        Assert.True(new LogRow(new LogEntryDto { Level = LogLevelDto.Debug, Message = "" }).IsQuiet);
        Assert.True(new LogRow(new LogEntryDto { Level = LogLevelDto.Critical, Message = "" }).IsError);
    }

    [Fact]
    public void Views_Load_WithTheirBindingsInPlace()
    {
        Exception? failure = null;
        UiTestApp.Run(() =>
        {
            try
            {
                var shell = NoEngineShell();
                var logs = shell.Advanced.Logs;
                logs.Entries.Add(new LogRow(new LogEntryDto
                {
                    Id = "f:1", TimestampUtc = DateTime.UtcNow, Level = LogLevelDto.Error, Category = "RuleStore", Message = "boom", Detail = "at X()",
                }));
                logs.Selected = logs.Entries[0];

                foreach (var view in new FrameworkElement[] { new AdvancedView { DataContext = shell.Advanced }, new LogsView { DataContext = logs } })
                {
                    view.Measure(new Size(1100, 760));
                    view.Arrange(new Rect(0, 0, 1100, 760));
                    view.UpdateLayout();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        Assert.Null(failure);
    }
}
