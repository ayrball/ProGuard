using System.Xml.Linq;
using ProGuard.UI.Services;

namespace ProGuard.Tests;

/// <summary>
/// Start at sign-in without a UAC prompt: a scheduled task while the window runs elevated and always starts as
/// administrator, the Run key otherwise, never both. Fakes stand in for Task Scheduler and the registry.
/// </summary>
public class SignInStartupTests
{
    private const string Exe = @"C:\Program Files\ProGuard\ProGuard.UI.exe";
    private const string Sid = "S-1-5-21-1000";

    private sealed class FakeElevation(bool elevated, bool always) : IElevationLauncher
    {
        public bool IsElevated { get; set; } = elevated;
        public bool AlwaysElevate { get; set; } = always;
        public ElevationOutcome Restart(IReadOnlyList<string> arguments, out string? error) { error = null; return ElevationOutcome.Cancelled; }
    }

    private sealed class FakeRunKey : IStartupRegistration
    {
        public bool IsEnabled { get; set; }
        public void Enable() => IsEnabled = true;
        public void Disable() => IsEnabled = false;
    }

    private sealed class FakeScheduler : ITaskScheduler
    {
        public Dictionary<string, string> Tasks { get; } = new();
        public Exception? Refuse { get; set; }
        public string? Query(string name) => Tasks.TryGetValue(name, out var xml) ? xml : null;
        public void Create(string name, string xml) { if (Refuse != null) throw Refuse; Tasks[name] = xml; }
        public void Delete(string name) { if (Refuse != null) throw Refuse; Tasks.Remove(name); }
    }

    private static (SignInStartup Startup, FakeElevation Elevation, FakeRunKey RunKey, FakeScheduler Tasks) New(bool elevated, bool always)
    {
        var elevation = new FakeElevation(elevated, always);
        var runKey = new FakeRunKey();
        var tasks = new FakeScheduler();
        return (new SignInStartup(elevation, runKey, tasks, Exe, Sid), elevation, runKey, tasks);
    }

    [Fact]
    public void ElevatedAndAlwaysAdmin_StartsThroughTheTask_AndNotTheRunKey()
    {
        var (startup, _, runKey, tasks) = New(elevated: true, always: true);
        runKey.IsEnabled = true;

        startup.Enable();

        Assert.True(tasks.Tasks.ContainsKey(startup.TaskName));
        Assert.Equal(@"\ProGuard\Start at sign-in S-1-5-21-1000", startup.TaskName);
        Assert.False(runKey.IsEnabled); // never both: it would start twice
        Assert.True(startup.IsEnabled);

        startup.Disable();
        Assert.Empty(tasks.Tasks);
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public void WithoutAlwaysAdmin_OrWithStandardRights_TheRunKeyIsUsed()
    {
        var (startup, _, runKey, tasks) = New(elevated: true, always: false);
        startup.Enable();
        Assert.True(runKey.IsEnabled);
        Assert.Empty(tasks.Tasks);

        (startup, _, runKey, tasks) = New(elevated: false, always: true);
        startup.Enable();
        Assert.True(runKey.IsEnabled); // can't create the task without administrator rights
        Assert.Empty(tasks.Tasks);
    }

    [Fact]
    public void Reconcile_RemovesARunKeyAddedBack_AndMovesToTheTask()
    {
        var (startup, _, runKey, tasks) = New(elevated: true, always: true);
        runKey.IsEnabled = true; // what the installer writes, and an upgrade writes again

        startup.Reconcile();
        Assert.True(tasks.Tasks.ContainsKey(startup.TaskName));
        Assert.False(runKey.IsEnabled);

        // An upgrade adds the Run key again: the next elevated start takes it away.
        runKey.IsEnabled = true;
        startup.Reconcile();
        Assert.False(runKey.IsEnabled);
        Assert.Single(tasks.Tasks);
    }

    [Fact]
    public void Reconcile_MovesBackToTheRunKey_WhenAlwaysAdminIsTurnedOff()
    {
        var (startup, elevation, runKey, tasks) = New(elevated: true, always: true);
        startup.Enable();

        elevation.AlwaysElevate = false;
        startup.Reconcile();

        Assert.Empty(tasks.Tasks);
        Assert.True(runKey.IsEnabled);
        Assert.True(startup.IsEnabled); // still starts at sign-in, now with the prompt-free path gone
    }

    [Fact]
    public void Reconcile_LeavesStartAtSignInOff_AndDoesNothingWithStandardRights()
    {
        var (startup, _, runKey, tasks) = New(elevated: true, always: true);
        startup.Reconcile();
        Assert.Empty(tasks.Tasks);
        Assert.False(runKey.IsEnabled);

        (startup, _, runKey, tasks) = New(elevated: false, always: true);
        runKey.IsEnabled = true;
        startup.Reconcile();
        Assert.True(runKey.IsEnabled); // a standard copy may not create the task
        Assert.Empty(tasks.Tasks);
    }

    [Fact]
    public void Reconcile_ReplacesATaskThatStartsAnotherCopy()
    {
        var (startup, _, _, tasks) = New(elevated: true, always: true);
        tasks.Tasks[startup.TaskName] = SignInStartup.TaskXml(Sid, @"D:\old\ProGuard.UI.exe");
        Assert.False(startup.IsEnabled); // it starts something else

        startup.Reconcile();

        Assert.Equal(Exe, SignInStartup.CommandIn(tasks.Tasks[startup.TaskName]));
        Assert.True(startup.IsEnabled);
    }

    [Fact]
    public void StandardRights_CanNotRemoveTheTask_AndSaySo()
    {
        var (startup, elevation, _, tasks) = New(elevated: true, always: true);
        startup.Enable();
        elevation.IsElevated = false;

        var refused = Assert.Throws<UnauthorizedAccessException>(startup.Disable);
        Assert.Contains("as administrator", refused.Message); // the toast then offers Restart as administrator
        Assert.Single(tasks.Tasks);

        startup.Enable(); // already starts at sign-in through the task: nothing to add, nothing to break
        Assert.Single(tasks.Tasks);
    }

    [Fact]
    public void TaskSchedulerRefusals_ReachTheCaller()
    {
        var (startup, _, _, tasks) = New(elevated: true, always: true);
        tasks.Refuse = new InvalidOperationException("The task XML is malformed.");

        Assert.Throws<InvalidOperationException>(startup.Enable);
    }

    [Fact]
    public void TheTask_RunsTheTrayCopyAtThisUsersSignIn_WithHighestRights_NoTimeLimit_AndNormalPriority()
    {
        var xml = SignInStartup.TaskXml(Sid, Exe);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = XDocument.Parse(xml);
        string Value(string name) => task.Descendants(ns + name).Single().Value;

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        Assert.Equal(Sid, task.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("HighestAvailable", Value("RunLevel"));
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal("PT0S", Value("ExecutionTimeLimit"));  // the default (72 hours) would end the tray copy
        Assert.Equal("4", Value("Priority"));               // the default (7) starts it below normal
        Assert.Equal("IgnoreNew", Value("MultipleInstancesPolicy"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("StopIfGoingOnBatteries"));
        Assert.Equal($"\"{Exe}\"", Value("Command"));
        Assert.Equal("--tray", Value("Arguments"));
        Assert.Equal(@"C:\Program Files\ProGuard", Value("WorkingDirectory"));
        Assert.Equal(Exe, SignInStartup.CommandIn(xml));
        Assert.Null(SignInStartup.CommandIn("not xml"));
    }
}
