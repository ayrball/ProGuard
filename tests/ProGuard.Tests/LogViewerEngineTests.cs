using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ProGuard.Core.Data;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Logging;
using ProGuard.Engine.Services;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

/// <summary>
/// The log viewer's engine side: reading the engine's text log, merging it with the action log, paging and tailing with
/// cursors (across the log rolling over), the filters, the CSV export and who may read what.
/// </summary>
public sealed class LogViewerEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ProGuard.Tests.Logs", Guid.NewGuid().ToString("N"));
    private readonly List<ProBalanceAction> _actions = new();
    private readonly Mock<IRuleStore> _store = new();
    private readonly DateTime _t0 = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Local);

    public LogViewerEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _store.Setup(s => s.GetActions(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int>()))
            .Returns((int? before, int? after, int count) => _actions
                .Where(a => (before == null || a.Id < before) && (after == null || a.Id > after))
                .OrderByDescending(a => a.Id).Take(count).ToList());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string LogPath => Path.Combine(_dir, "engine.log");
    private string RolledPath => Path.Combine(_dir, "engine.1.log");

    private static string Line(DateTime local, string level, string category, string message) =>
        $"{local.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {category}: {message}\n";

    private void Append(string text, string? path = null) => File.AppendAllText(path ?? LogPath, text, new UTF8Encoding(false));

    /// <summary>Writes <paramref name="count"/> numbered INF lines, a second apart from <paramref name="start"/>.</summary>
    private void AppendNumbered(int from, int count, DateTime? start = null, string? path = null)
    {
        var text = new StringBuilder();
        for (int i = from; i < from + count; i++)
            text.Append(Line((start ?? _t0).AddSeconds(i), "INF", "Numbered", $"line {i}"));
        Append(text.ToString(), path);
    }

    private void AddAction(int id, DateTime local, string name, int pid, string reason, bool restored = false) =>
        _actions.Add(new ProBalanceAction
        {
            Id = id, Timestamp = local.ToUniversalTime(), ProcessName = name, Pid = pid, Reason = reason, IsRestored = restored,
            OriginalPriority = 32, NewPriority = restored ? 32 : 16384,
        });

    private LogQueryService Service(EngineLogReader? reader = null, TimeProvider? time = null) =>
        new(reader ?? new EngineLogReader(LogPath), _store.Object, time);

    /// <summary>Pages through everything with <paramref name="query"/>, collecting the entries in the order they came.</summary>
    private static List<LogEntryDto> PageAll(LogQueryService service, LogQuery query, bool admin = true, Action<int>? betweenPages = null)
    {
        var all = new List<LogEntryDto>();
        string? cursor = null;
        for (int page = 0; page < 1000; page++)
        {
            query.Before = cursor;
            var result = service.Query(query, admin);
            all.AddRange(result.Entries);
            cursor = result.OlderCursor;
            if (cursor == null) return all;
            betweenPages?.Invoke(page);
        }
        throw new InvalidOperationException("Paging never ended");
    }

    // ------------------------------------------------------------------ reading the file

    [Fact]
    public void Reader_ParsesLevelsCategoriesProcessesAndStackTraces()
    {
        Append(Line(_t0, "INF", "ProBalanceEngine", "ProBalance restrained Discord (2144) from Normal to BelowNormal at 10.4% CPU"));
        Append(Line(_t0.AddSeconds(1), "INF", "IpcDispatcher", "IPC command SetPriority (pid 33268, arg 2) from PC\\me"));
        Append(Line(_t0.AddSeconds(2), "INF", "UserSessionLauncher", "Launched \"C:\\x\\ProGuard.SessionAgent.exe\" as process 2772 in session 1"));
        Append(Line(_t0.AddSeconds(3), "ERR", "RuleStore", "Failed to log ProBalance action") +
               "System.InvalidOperationException: boom\r\n   at Somewhere()\r\n");
        Append(Line(_t0.AddSeconds(4), "WRN", "Lifetime", "Message: with a colon"));

        var lines = new EngineLogReader(LogPath).Read();

        Assert.Equal(5, lines.Length);
        Assert.Equal(("Discord", 2144), (lines[0].ProcessName, lines[0].Pid));
        Assert.Equal((null, 33268), (lines[1].ProcessName, lines[1].Pid));
        Assert.Equal(("ProGuard.SessionAgent.exe", 2772), (lines[2].ProcessName, lines[2].Pid));
        Assert.Equal(LogLevelDto.Error, lines[3].Level);
        Assert.Equal("RuleStore", lines[3].Category);
        Assert.Equal("System.InvalidOperationException: boom\n   at Somewhere()", lines[3].Detail);
        Assert.Equal(LogLevelDto.Warning, lines[4].Level);
        Assert.Equal("Lifetime", lines[4].Category);
        Assert.Equal("Message: with a colon", lines[4].Message);
        Assert.Equal(_t0.AddSeconds(4).ToUniversalTime(), lines[4].TimestampUtc);
    }

    [Fact]
    public void Reader_ReadsOnlyWhatWasAppended_AndWaitsForAWholeLine()
    {
        AppendNumbered(0, 3);
        var reader = new EngineLogReader(LogPath);
        var first = reader.Read();
        Assert.Equal(3, first.Length);

        // Half a line: not an entry yet.
        Append(Line(_t0.AddSeconds(3), "INF", "Numbered", "line 3").TrimEnd('\n'));
        Assert.Equal(3, reader.Read().Length);

        Append("\n" + Line(_t0.AddSeconds(4), "ERR", "Numbered", "line 4") + "   at Frame()\n");
        var second = reader.Read();
        Assert.Equal(new[] { "line 0", "line 1", "line 2", "line 3", "line 4" }, second.Select(l => l.Message));
        Assert.Same(first[0], second[0]); // the cached entries, not a re-parse
        Assert.Equal("at Frame()", second[4].Detail?.Trim());

        // The stack trace can arrive in a later read than its first line.
        Append("   at Caller()\n");
        Assert.Equal("at Frame()\n   at Caller()", reader.Read()[4].Detail?.Trim());
    }

    [Fact]
    public void Reader_IdenticalLinesInTheSameMillisecond_GetDistinctKeys()
    {
        var same = Line(_t0, "INF", "X", "same");
        Append(same + same + same);

        var keys = new EngineLogReader(LogPath).Read().Select(l => l.Key).ToList();

        Assert.Equal(3, keys.Distinct().Count());
        Assert.Equal(new[] { 0, 1, 2 }, keys.Select(k => k.Ordinal));
    }

    [Fact]
    public void Reader_DropsItsCacheWhenNobodyHasLookedForAWhile()
    {
        AppendNumbered(0, 5);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var reader = new EngineLogReader(LogPath, time);
        reader.Read();
        Assert.True(reader.IsCached);

        time.Advance(EngineLogReader.IdleRelease - TimeSpan.FromSeconds(1));
        reader.ReleaseIfIdle();
        Assert.True(reader.IsCached);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(reader.IsCached); // the idle timer fired
        Assert.Equal(5, reader.Read().Length);
    }

    [Fact]
    public void Reader_MissingFiles_AreAnEmptyLog()
    {
        Assert.Empty(new EngineLogReader(Path.Combine(_dir, "nothing", "engine.log")).Read());
    }

    // ------------------------------------------------------------------ paging

    [Fact]
    public void Paging_VisitsEveryEntryOnce_NewestFirst_AcrossBothSources()
    {
        AppendNumbered(0, 250);
        for (int i = 0; i < 120; i++) AddAction(i + 1, _t0.AddSeconds(i * 2 + 0.5), "app.exe", 1000 + i, $"action {i}");

        var all = PageAll(Service(), new LogQuery { Limit = 37 });

        Assert.Equal(370, all.Count);
        Assert.Equal(all.Count, all.Select(e => e.Id).Distinct().Count());
        Assert.Equal(all.OrderByDescending(e => e.TimestampUtc).Select(e => e.Id), all.Select(e => e.Id));
        Assert.Equal("line 249", all[0].Message);
        Assert.Equal("line 0", all[^1].Message);
    }

    [Fact]
    public void Paging_FirstPage_IsTheNewest_WithCursors()
    {
        AppendNumbered(0, 10);
        var page = Service().Query(new LogQuery { Limit = 4 }, includeEngineLog: true);

        Assert.Equal(new[] { "line 9", "line 8", "line 7", "line 6" }, page.Entries.Select(e => e.Message));
        Assert.NotNull(page.OlderCursor);
        Assert.False(string.IsNullOrEmpty(page.HeadCursor));
        Assert.True(page.EngineLogIncluded);

        var last = Service().Query(new LogQuery { Limit = 50 }, includeEngineLog: true);
        Assert.Null(last.OlderCursor);
    }

    [Fact]
    public void Paging_WithASparseFilter_StillReachesEveryMatch()
    {
        // Matches far apart in the action log: each page has to look past many rows that don't match.
        for (int i = 1; i <= 3000; i++) AddAction(i, _t0.AddSeconds(i), i % 700 == 0 ? "rare.exe" : "common.exe", i, "x");

        var all = PageAll(Service(), new LogQuery { Process = "rare", Limit = 2 });

        Assert.Equal(new[] { 2800, 2100, 1400, 700 }, all.Select(e => e.Pid!.Value));
    }

    [Fact]
    public void Paging_ActionScanLimit_ReturnsACursorToCarryOnFrom()
    {
        // The one match is the oldest row, behind more rows than one page looks at.
        int total = LogQueryService.MaxActionScan + 600;
        AddAction(1, _t0.AddSeconds(-10), "needle.exe", 7, "old");
        for (int i = 2; i <= total; i++) AddAction(i, _t0.AddSeconds(i), "common.exe", i, "x");

        var service = Service();
        var first = service.Query(new LogQuery { Process = "needle", Sources = new() { LogSource.Actions } }, includeEngineLog: true);
        Assert.Empty(first.Entries);
        Assert.NotNull(first.OlderCursor);

        var second = service.Query(new LogQuery { Process = "needle", Sources = new() { LogSource.Actions }, Before = first.OlderCursor }, includeEngineLog: true);
        Assert.Equal("needle.exe", Assert.Single(second.Entries).ProcessName);
        Assert.Null(second.OlderCursor);
    }

    [Fact]
    public void Cursor_SurvivesTheLogRollingOver()
    {
        AppendNumbered(0, 100);
        var service = Service();
        var seen = new List<LogEntryDto>();

        var page = service.Query(new LogQuery { Limit = 30 }, includeEngineLog: true);
        seen.AddRange(page.Entries);

        // The logger rolls over: engine.log becomes engine.1.log and new lines start a fresh engine.log.
        File.Move(LogPath, RolledPath);
        AppendNumbered(100, 20);

        string? cursor = page.OlderCursor;
        while (cursor != null)
        {
            page = service.Query(new LogQuery { Limit = 30, Before = cursor }, includeEngineLog: true);
            seen.AddRange(page.Entries);
            cursor = page.OlderCursor;
        }

        Assert.Equal(Enumerable.Range(0, 100).Reverse().Select(i => $"line {i}"), seen.Select(e => e.Message));
    }

    [Fact]
    public void Cursor_IsStillUsable_AfterTheEngineRestarts()
    {
        AppendNumbered(0, 50);
        var page = Service().Query(new LogQuery { Limit = 10 }, includeEngineLog: true);

        // A new reader and service, as after a restart: nothing cached, the cursor comes from the client.
        var next = Service().Query(new LogQuery { Limit = 10, Before = page.OlderCursor }, includeEngineLog: true);

        Assert.Equal("line 39", next.Entries[0].Message);
    }

    [Fact]
    public void Cursor_WhoseEntryRolledAway_ContinuesByTime()
    {
        AppendNumbered(0, 40);
        var service = Service();
        var page = service.Query(new LogQuery { Limit = 10 }, includeEngineLog: true); // lines 39..30

        // Two roll-overs: the file holding the cursor's entry is gone; only 25..29 and new lines remain.
        File.Delete(LogPath);
        AppendNumbered(25, 5, path: RolledPath);
        AppendNumbered(40, 5);

        var next = service.Query(new LogQuery { Limit = 10, Before = page.OlderCursor }, includeEngineLog: true);

        Assert.Equal(new[] { "line 29", "line 28", "line 27", "line 26", "line 25" }, next.Entries.Select(e => e.Message));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("1;f=1.2;a=3")]
    [InlineData("1;f=*;a=x")]
    [InlineData("2;f=*;a=*")]
    public void Cursor_Malformed_IsRefused(string cursor)
    {
        Assert.Throws<ArgumentException>(() => Service().Query(new LogQuery { Before = cursor }, includeEngineLog: true));
    }

    [Fact]
    public void BeforeAndAfterTogether_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => Service().Query(new LogQuery { Before = "1;f=*;a=*", After = "1;f=*;a=*" }, includeEngineLog: true));
    }

    // ------------------------------------------------------------------ live tail

    [Fact]
    public void Tail_ReturnsOnlyWhatWasWrittenSince_IncludingAcrossARollOver()
    {
        AppendNumbered(0, 10);
        AddAction(1, _t0, "app.exe", 1, "old action");
        var service = Service();
        var head = service.Query(new LogQuery(), includeEngineLog: true).HeadCursor;

        var nothing = service.Query(new LogQuery { After = head }, includeEngineLog: true);
        Assert.Empty(nothing.Entries);

        AppendNumbered(10, 2);
        File.Move(LogPath, RolledPath);
        AppendNumbered(12, 2);
        AddAction(2, _t0.AddSeconds(20), "app.exe", 1, "new action");

        var tail = service.Query(new LogQuery { After = head }, includeEngineLog: true);

        Assert.Equal(new[] { "Restrained: new action", "line 13", "line 12", "line 11", "line 10" }, tail.Entries.Select(e => e.Message));
        Assert.False(tail.Truncated);
        Assert.False(tail.Reset);

        var again = service.Query(new LogQuery { After = tail.HeadCursor }, includeEngineLog: true);
        Assert.Empty(again.Entries);
    }

    [Fact]
    public void Tail_HeadMovesPastLinesTheFilterHides()
    {
        AppendNumbered(0, 3);
        var service = Service();
        var head = service.Query(new LogQuery { Levels = new() { LogLevelDto.Error } }, includeEngineLog: true).HeadCursor;

        AppendNumbered(3, 5);
        var tail = service.Query(new LogQuery { Levels = new() { LogLevelDto.Error }, After = head }, includeEngineLog: true);
        Assert.Empty(tail.Entries);
        Assert.NotEqual(head, tail.HeadCursor);

        Append(Line(_t0.AddSeconds(9), "ERR", "X", "bad"));
        var next = service.Query(new LogQuery { Levels = new() { LogLevelDto.Error }, After = tail.HeadCursor }, includeEngineLog: true);
        Assert.Equal("bad", Assert.Single(next.Entries).Message);
    }

    [Fact]
    public void Tail_MoreThanAPage_KeepsTheNewest_AndSaysSo()
    {
        AppendNumbered(0, 1);
        var service = Service();
        var head = service.Query(new LogQuery(), includeEngineLog: true).HeadCursor;
        AppendNumbered(1, 30);

        var tail = service.Query(new LogQuery { After = head, Limit = 10 }, includeEngineLog: true);

        Assert.True(tail.Truncated);
        Assert.Equal("line 30", tail.Entries[0].Message);
        Assert.Equal(10, tail.Entries.Count);
    }

    [Fact]
    public void Tail_AfterTheActionLogWasCleared_StartsAgain()
    {
        for (int i = 1; i <= 5; i++) AddAction(i, _t0.AddSeconds(i), "app.exe", i, $"a{i}");
        var service = Service();
        var head = service.Query(new LogQuery(), includeEngineLog: true).HeadCursor;

        _actions.Clear();
        AddAction(1, _t0.AddSeconds(60), "app.exe", 9, "after clear");
        var tail = service.Query(new LogQuery { After = head }, includeEngineLog: true);

        Assert.True(tail.Reset);
        Assert.Equal("Restrained: after clear", Assert.Single(tail.Entries).Message);
    }

    // ------------------------------------------------------------------ filters

    private void WriteMix()
    {
        Append(Line(_t0, "DBG", "ProcessMonitor", "Polled 200 processes"));
        Append(Line(_t0.AddMinutes(1), "INF", "ProBalanceEngine", "ProBalance restrained Discord (2144) from Normal to BelowNormal"));
        Append(Line(_t0.AddMinutes(2), "WRN", "SessionAgentSupervisor", "Session agent 2772 in session 1 exited"));
        Append(Line(_t0.AddMinutes(3), "ERR", "RuleStore", "Failed to log ProBalance action") + "SqliteException: database is locked\n");
        AddAction(1, _t0.AddMinutes(4), "chrome.exe", 4321, "chrome.exe (4321) is using 50% CPU");
        AddAction(2, _t0.AddMinutes(5), ProBalanceAction.PowerPlanEntryName, 0, "Switched to High performance");
    }

    private List<string> Messages(LogQuery query, bool admin = true) => Service().Query(query, admin).Entries.Select(e => e.Message).ToList();

    [Fact]
    public void Filter_ByLevel()
    {
        WriteMix();
        Assert.Equal(new[] { "Failed to log ProBalance action", "Session agent 2772 in session 1 exited" },
            Messages(new LogQuery { Levels = new() { LogLevelDto.Warning, LogLevelDto.Error } }));
    }

    [Fact]
    public void Filter_BySource()
    {
        WriteMix();
        Assert.Equal(2, Messages(new LogQuery { Sources = new() { LogSource.Actions } }).Count);
        Assert.Equal(4, Messages(new LogQuery { Sources = new() { LogSource.Engine } }).Count);
        Assert.Equal(6, Messages(new LogQuery()).Count);
    }

    [Fact]
    public void Filter_ByCategory_CoversBothSources()
    {
        WriteMix();
        Assert.Equal(new[] { "Restrained: chrome.exe (4321) is using 50% CPU", "ProBalance restrained Discord (2144) from Normal to BelowNormal" },
            Messages(new LogQuery { Category = "probalance" }));
        Assert.Equal(new[] { "Switched to High performance" }, Messages(new LogQuery { Category = "Power" }));
    }

    [Fact]
    public void Filter_ByProcessNameOrPid()
    {
        WriteMix();
        Assert.Equal(new[] { "ProBalance restrained Discord (2144) from Normal to BelowNormal" }, Messages(new LogQuery { Process = "disc" }));
        Assert.Equal(new[] { "Restrained: chrome.exe (4321) is using 50% CPU" }, Messages(new LogQuery { Process = "4321" }));
        Assert.Empty(Messages(new LogQuery { Process = "432" })); // a pid matches whole
    }

    [Fact]
    public void Filter_ByText_SearchesTheDetailToo()
    {
        WriteMix();
        Assert.Equal(new[] { "Failed to log ProBalance action" }, Messages(new LogQuery { Text = "DATABASE IS LOCKED" }));
    }

    [Fact]
    public void Filter_ByTimeRange_IsInclusive()
    {
        WriteMix();
        var messages = Messages(new LogQuery { FromUtc = _t0.AddMinutes(1).ToUniversalTime(), ToUtc = _t0.AddMinutes(3).ToUniversalTime() });
        Assert.Equal(3, messages.Count);
        Assert.Equal("Failed to log ProBalance action", messages[0]);
    }

    [Fact]
    public void ActionEntries_SayWhatChanged()
    {
        WriteMix();
        var entry = Service().Query(new LogQuery { Sources = new() { LogSource.Actions }, Process = "chrome" }, includeEngineLog: true).Entries.Single();

        Assert.Equal("a:1", entry.Id);
        Assert.Equal(LogSource.Actions, entry.Source);
        Assert.Equal(LogLevelDto.Information, entry.Level);
        Assert.Equal(4321, entry.Pid);
        Assert.Equal("Priority: Normal -> BelowNormal", entry.Detail);
    }

    // ------------------------------------------------------------------ who may read what

    [Fact]
    public void StandardUser_GetsTheActionLogOnly()
    {
        WriteMix();
        var page = Service().Query(new LogQuery(), includeEngineLog: false);

        Assert.False(page.EngineLogIncluded);
        Assert.All(page.Entries, e => Assert.Equal(LogSource.Actions, e.Source));
        Assert.Equal(2, page.Entries.Count);

        var engineOnly = Service().Query(new LogQuery { Sources = new() { LogSource.Engine } }, includeEngineLog: false);
        Assert.Empty(engineOnly.Entries);
        Assert.Null(engineOnly.OlderCursor);

        var export = Service().Export(new LogQuery(), includeEngineLog: false);
        Assert.Equal(2, export.Count);
        Assert.DoesNotContain("SessionAgentSupervisor", export.Csv);
    }

    [Fact]
    public void Authorization_LogCommandsAreOpen_ButOnlyAdministratorsReadTheEngineLog()
    {
        var standard = new ThinCaller();
        Assert.Null(IpcAuthorization.Check(new CommandMessage { CommandType = CommandTypes.GetLogEntries }, standard));
        Assert.Null(IpcAuthorization.Check(new CommandMessage { CommandType = CommandTypes.ExportLogs }, standard));
        Assert.False(IpcAuthorization.MayReadEngineLog(standard));
        Assert.True(IpcAuthorization.MayReadEngineLog(ThinCaller.Admin()));
    }

    [Fact]
    public void ThroughTheDispatcher_EachCallerGetsWhatTheyMayRead()
    {
        var harness = new ThinHarness(LogPath);
        Append(Line(_t0, "WRN", "IpcDispatcher", "Refused IPC request from OTHER\\user"));
        harness.Actions.Add(new ProBalanceAction { Id = 1, Timestamp = DateTime.UtcNow, ProcessName = "app.exe", Pid = 5, Reason = "busy" });

        var admin = harness.Data<LogPage>(harness.SendAsAdmin(CommandTypes.GetLogEntries, argument: new LogQuery()));
        Assert.True(admin.EngineLogIncluded);
        Assert.Equal(2, admin.Entries.Count);

        var standard = harness.Data<LogPage>(harness.Send(new ThinCaller(), CommandTypes.GetLogEntries));
        Assert.False(standard.EngineLogIncluded);
        Assert.Equal("a:1", Assert.Single(standard.Entries).Id);

        var export = harness.Data<LogExportDto>(harness.Send(new ThinCaller(), CommandTypes.ExportLogs, argument: new LogQuery()));
        Assert.Equal(1, export.Count);
        Assert.DoesNotContain("OTHER", export.Csv);

        var bad = harness.Send(new ThinCaller(), CommandTypes.GetLogEntries, argument: new LogQuery { Before = "nonsense" });
        Assert.False(bad.Success);
        Assert.Contains("cursor", bad.Error);
    }

    // ------------------------------------------------------------------ export

    [Fact]
    public void Export_IsCsv_Escaped_AndSafeToOpenInASpreadsheet()
    {
        Append(Line(_t0, "ERR", "Cat", "has, a comma and \"quotes\"") + "  at A()\n  at B()\n");
        AddAction(1, _t0.AddSeconds(1), "=cmd|' /c calc'!A0", 66, "-sneaky");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 14, 22, 33, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);

        var export = Service(time: time).Export(new LogQuery(), includeEngineLog: true);

        Assert.Equal("proguard-log-20261016-142233.csv", export.FileName);
        Assert.Equal(2, export.Count);
        Assert.False(export.Truncated);
        var rows = export.Csv.Split("\r\n");
        Assert.Equal(string.Join(',', LogQueryService.CsvColumns), rows[0]);
        Assert.Contains(",'=cmd|' /c calc'!A0,66,Restrained: -sneaky,", rows[1]);
        Assert.Contains("\"has, a comma and \"\"quotes\"\"\"", export.Csv);
        Assert.Contains("\"  at A()\n  at B()\"", export.Csv);
        Assert.EndsWith("\r\n", export.Csv);
    }

    [Fact]
    public void Export_HonoursTheFilters()
    {
        WriteMix();
        var export = Service().Export(new LogQuery { Levels = new() { LogLevelDto.Error } }, includeEngineLog: true);
        Assert.Equal(1, export.Count);
        Assert.Contains("database is locked", export.Csv);
    }

    // ------------------------------------------------------------------ the real action log

    private sealed class FileDbContext(string path) : ProGuardDbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlite($"Data Source={path}");
    }

    [Fact]
    public async Task RuleStore_PagesTheActionLogById()
    {
        var dbPath = Path.Combine(_dir, "proguard.db");
        var services = new ServiceCollection();
        services.AddScoped<ProGuardDbContext>(_ => new FileDbContext(dbPath));
        using (var provider = services.BuildServiceProvider())
        {
            var store = new RuleStore(NullLogger<RuleStore>.Instance, provider);
            for (int i = 0; i < 10; i++)
                await store.LogProBalanceActionAsync(new ProBalanceAction { Timestamp = DateTime.UtcNow.AddSeconds(i), ProcessName = $"p{i}.exe", Pid = i, Reason = "r" });

            var newest = store.GetActions(null, null, 3);
            Assert.Equal(new[] { "p9.exe", "p8.exe", "p7.exe" }, newest.Select(a => a.ProcessName));
            Assert.All(newest, a => Assert.Equal(DateTimeKind.Utc, a.Timestamp.Kind));

            var older = store.GetActions(newest[^1].Id, null, 3);
            Assert.Equal(new[] { "p6.exe", "p5.exe", "p4.exe" }, older.Select(a => a.ProcessName));

            var between = store.GetActions(newest[0].Id, older[0].Id, 100);
            Assert.Equal(new[] { "p8.exe", "p7.exe" }, between.Select(a => a.ProcessName));

            var page = new LogQueryService(new EngineLogReader(LogPath), store).Query(new LogQuery { Limit = 4 }, includeEngineLog: false);
            Assert.Equal(new[] { "p9.exe", "p8.exe", "p7.exe", "p6.exe" }, page.Entries.Select(e => e.ProcessName));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
