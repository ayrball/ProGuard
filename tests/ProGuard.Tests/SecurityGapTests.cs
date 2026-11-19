using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Security;

namespace ProGuard.Tests;

/// <summary>Gaps left by the earlier hardening: who Keep Running relaunches as, and pipe servers at a lower integrity level.</summary>
public class SecurityGapTests
{
    private const string Alice = ThinCaller.StandardSid;
    private const string Bob = "S-1-5-21-1111-2222-3333-1002";

    // ================= Keep Running starts a standard user's program only as that user =================

    private sealed class KeepRunningRig
    {
        public FakeProcessActions Actions { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public List<ProcessInfo> Processes { get; } = new() { new ProcessInfo { Pid = 11, Name = "tool" } };
        public ManualClock Clock { get; } = new();
        public WatchdogEventLog Events { get; }
        public KeepRunningService KeepRunning { get; }

        public KeepRunningRig(ProcessRule rule)
        {
            Actions.Running.Add(11);
            Actions.LaunchInfo[11] = new ProcessLaunchInfo(@"C:\alice\tool.exe", "\"C:\\alice\\tool.exe\"", @"C:\alice", 2);
            var store = new Mock<IRuleStore>();
            store.Setup(s => s.GetProcessRules()).Returns(() => new[] { rule });
            Events = new WatchdogEventLog(Clock);
            KeepRunning = new KeepRunningService(NullLogger<KeepRunningService>.Instance, store.Object, Actions, Launcher, Events, Clock)
            {
                Scope = new RuleScope(new FakeOwners().Own(11, Alice)),
            };
        }

        public void ExitAndWait(int seconds)
        {
            KeepRunning.Tick(Processes);
            Processes.Clear();
            Actions.Running.Remove(11);
            for (var i = 0; i < seconds; i++) { Clock.Advance(TimeSpan.FromSeconds(1)); KeepRunning.Tick(Processes); }
        }
    }

    private static ProcessRule Scoped() => new() { Id = 1, ProcessName = "tool.exe", KeepRunning = true, AuthorSid = Alice, AuthorIsAdministrator = false };

    [Fact]
    public void KeepRunning_AsksForTheAuthor_ForAStandardUsersRule()
    {
        var rig = new KeepRunningRig(Scoped());
        rig.ExitAndWait(3);
        Assert.Equal(Alice, Assert.Single(rig.Launcher.Requests).RequiredUserSid);
    }

    [Fact]
    public void KeepRunning_AsksForNoOneInParticular_ForAnAdministratorsRule()
    {
        var rule = Scoped();
        rule.AuthorIsAdministrator = true;
        var rig = new KeepRunningRig(rule);
        rig.ExitAndWait(3);
        Assert.Null(Assert.Single(rig.Launcher.Requests).RequiredUserSid);
    }

    [Fact]
    public void KeepRunning_GivesUp_WhenSomeoneElseNowHasTheSession()
    {
        var rig = new KeepRunningRig(Scoped());
        rig.Launcher.Failure = new SessionUserMismatchException("session 2 now belongs to Bob");
        rig.ExitAndWait(60);

        Assert.Single(rig.Launcher.Requests); // no retries
        Assert.Contains(rig.Events.GetSince(0).Events, e => e.Notify && e.Message.Contains("Stopped relaunching"));
    }

    [Fact]
    public void KeepRunning_GivesUp_WhenTheSessionIsGone()
    {
        var rig = new KeepRunningRig(Scoped());
        rig.Launcher.Failure = new SessionUnavailableException("Session 2 no longer exists");
        rig.ExitAndWait(60);
        Assert.Single(rig.Launcher.Requests);
    }

    [Fact]
    public void KeepRunning_StillRetries_OtherFailures()
    {
        var rig = new KeepRunningRig(Scoped());
        rig.Launcher.Failure = new System.ComponentModel.Win32Exception(5);
        rig.ExitAndWait(30);
        Assert.True(rig.Launcher.Requests.Count > 1);
    }

    [Fact]
    public void TheLauncher_ComparesTheSessionsUserWithTheOneRequired()
    {
        UserSessionLauncher.CheckUser(null, Bob, 2);
        UserSessionLauncher.CheckUser(Alice, Alice.ToLowerInvariant(), 2);
        Assert.Throws<SessionUserMismatchException>(() => UserSessionLauncher.CheckUser(Alice, Bob, 2));
        Assert.Throws<SessionUserMismatchException>(() => UserSessionLauncher.CheckUser(Alice, null, 2));
    }

    [Theory]
    [InlineData(1008, true)]  // ERROR_NO_TOKEN: nobody signed in
    [InlineData(7022, true)]  // ERROR_CTX_WINSTATION_NOT_FOUND
    [InlineData(2, true)]     // ERROR_FILE_NOT_FOUND
    [InlineData(5, false)]    // access denied: something else
    public void TheLauncher_TellsAGoneSessionFromOtherFailures(int error, bool gone)
    {
        var failure = UserSessionLauncher.TokenQueryFailure(error, 3);
        Assert.Equal(gone, failure is SessionUnavailableException);
    }

    [Fact]
    public void TheLauncher_RefusesToStartAsTheWrongUser_InItsOwnSession()
    {
        // Unelevated tests launch into their own session as themselves; a launch for someone else must not happen.
        if (UserSessionLauncher.IsRunningAsSystem()) return;
        var launcher = new UserSessionLauncher(NullLogger<UserSessionLauncher>.Instance);
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        Assert.Throws<SessionUserMismatchException>(() =>
            launcher.Launch(new LaunchRequest(@"C:\Windows\System32\whoami.exe", "whoami.exe", null, session, Bob)));
    }

    // ================= Pipe servers at a lower integrity level =================

    private static readonly TokenIdentity Me = new("S-1-5-21-1-2-3-1001", Elevated: false, TokenIdentity.MediumRid);
    private static readonly TokenIdentity MeHigh = new("S-1-5-21-1-2-3-1001", Elevated: true, TokenIdentity.HighRid);

    [Fact]
    public void PipeServerCheck_TrustsTheUnlabeledServicePipe_EvenForAnElevatedClient()
    {
        var system = new TokenIdentity(TokenIdentity.LocalSystemSid, true, TokenIdentity.SystemRid);
        Assert.Null(PipeServerCheck.Problem(TokenIdentity.LocalSystemSid, system, MeHigh, TokenIdentity.MediumRid));
        Assert.Null(PipeServerCheck.Problem(TokenIdentity.LocalSystemSid, null, Me, TokenIdentity.MediumRid));
        // A label that can't be read doesn't matter for a pipe SYSTEM owns.
        Assert.Null(PipeServerCheck.Problem(TokenIdentity.LocalSystemSid, system, MeHigh, null));
    }

    [Fact]
    public void PipeServerCheck_RefusesASameUserPipe_CreatedAtLowIntegrity()
    {
        Assert.NotNull(PipeServerCheck.Problem(Me.UserSid, Me, Me, TokenIdentity.LowRid));
        Assert.NotNull(PipeServerCheck.Problem(Me.UserSid, Me, Me, null)); // unreadable label: fail closed
        // The same user's medium-integrity engine (run by hand, or the tests) is still fine.
        Assert.Null(PipeServerCheck.Problem(Me.UserSid, Me, Me, TokenIdentity.MediumRid));
    }

    [Fact]
    public void PipeServerCheck_RefusesASameUserServerProcess_AtLowIntegrity()
    {
        var sandboxed = Me with { IntegrityRid = TokenIdentity.LowRid };
        Assert.NotNull(PipeServerCheck.Problem(Me.UserSid, sandboxed, Me, TokenIdentity.MediumRid));
        var unknown = Me with { IntegrityRid = TokenIdentity.UnknownRid };
        Assert.NotNull(PipeServerCheck.Problem(Me.UserSid, unknown, Me, TokenIdentity.MediumRid));
        // A client whose own level can't be read can't compare.
        Assert.NotNull(PipeServerCheck.Problem(Me.UserSid, Me, Me with { IntegrityRid = TokenIdentity.UnknownRid }, TokenIdentity.MediumRid));
    }

    [Fact]
    public void TokenIdentity_ReadsThisProcesssIntegrityLevel()
    {
        var me = TokenIdentity.Current();
        Assert.NotNull(me);
        Assert.True(me!.Value.IntegrityRid >= TokenIdentity.MediumRid, $"0x{me.Value.IntegrityRid:X}");
    }

    [Theory]
    [InlineData("S:(ML;;NW;;;LW)", TokenIdentity.LowRid)]
    [InlineData("S:(ML;;NW;;;ME)", TokenIdentity.MediumRid)]
    [InlineData("S:(ML;;NW;;;HI)", TokenIdentity.HighRid)]
    [InlineData("S:(AU;SA;FA;;;WD)", TokenIdentity.MediumRid)] // an audit entry is not a label
    public void TheMandatoryLabel_IsReadFromTheSacl(string sddl, uint expected)
    {
        var sacl = new RawSecurityDescriptor(sddl).SystemAcl!;
        var bytes = new byte[sacl.BinaryLength];
        sacl.GetBinaryForm(bytes, 0);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            Assert.Equal(expected, TokenNative.LabelRidOf(memory));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
        Assert.Equal(TokenIdentity.MediumRid, TokenNative.LabelRidOf(IntPtr.Zero));
    }

    [Fact]
    public void APipeThisProcessCreates_HasAReadableLabel()
    {
        using var server = new NamedPipeServerStream("ProGuardTest_" + Guid.NewGuid().ToString("N"), PipeDirection.InOut, 1);
        Assert.NotNull(TokenNative.MandatoryLabelRidOf(server.SafePipeHandle.DangerousGetHandle()));
    }
}
