using System.Diagnostics;
using System.IO;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Engine.Services;
using ProGuard.Shared.Reporting;

namespace ProGuard.Tests;

/// <summary>
/// With no foreground report (no session agent or UI), ProBalance can't tell which program is in use, so it treats
/// every program that could be (a GUI program in a user session) as the foreground process.
/// </summary>
public class ProBalanceForegroundFallbackTests
{
    private const int Game = 100, Encoder = 101, Service = 102;

    private static ProBalanceHarness Harness()
    {
        var h = new ProBalanceHarness();
        h.Add(Game, cpu: 50);
        h.Add(Encoder, cpu: 50);
        h.Add(Service, cpu: 50, session: 0);
        h.Engine.ExcludeServices = false;   // so session 0 is decided by the fallback alone
        h.Fallback.Windowed.UnionWith(new[] { Game, Service });
        return h;
    }

    [Fact]
    public void WithoutAReport_WindowedProgramsInUserSessionsAreNotRestrained()
    {
        using var h = Harness();
        h.Fallback.IsForegroundKnown = false;

        h.At(0);
        h.At(1000);

        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(Game));
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(Encoder));    // a console program: still restrained
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(Service));    // session 0 is never the program in use
    }

    [Fact]
    public void WithAReport_OnlyTheReportedForegroundIsExempt()
    {
        using var h = Harness();
        h.ForegroundPid = Encoder;

        h.At(0);
        h.At(1000);

        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(Game));
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(Encoder));
        Assert.Equal(0, h.Fallback.Asked);
    }

    [Fact]
    public void WhenReportsStop_ARestrainedWindowedProgramIsReleased()
    {
        using var h = Harness();
        h.At(0);
        h.At(1000);
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(Game));

        // The agent died and the UI is closed: the game may be what the user is playing.
        h.Fallback.IsForegroundKnown = false;
        h.At(2000);
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(Game));
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(Encoder));
        h.Store.Verify(s => s.LogProBalanceActionAsync(It.Is<ProBalanceAction>(a =>
            a.Pid == Game && a.Reason.Contains("No foreground report"))));

        // Reports are back and the game isn't in front: it can be restrained again.
        h.Fallback.IsForegroundKnown = true;
        h.At(3000);
        h.At(4000);
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(Game));
    }

    [Fact]
    public void WithTheForegroundExemptionOff_TheFallbackIsOffToo()
    {
        using var h = Harness();
        h.Engine.ExcludeForegroundProcesses = false;
        h.Fallback.IsForegroundKnown = false;

        h.At(0);
        h.At(1000);

        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(Game));
    }

    // ---------- What counts as possibly in use ----------

    [Fact]
    public void GuiPrograms_AreTellableFromConsolePrograms()
    {
        Assert.True(GuiProgramFallback.IsGuiProgram(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")));
        Assert.True(GuiProgramFallback.IsGuiProgram(Path.Combine(AppContext.BaseDirectory, SessionAgentInstance.ExecutableName)));
        Assert.False(GuiProgramFallback.IsGuiProgram(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
        Assert.Null(GuiProgramFallback.IsGuiProgram(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe")));
    }

    [Fact]
    public void AProgramThatCantBeRead_CountsAsPossiblyInUse_AndThePathIsLookedUpWhenMissing()
    {
        var tracker = new ForegroundTracker(null, () => 0, _ => 1, () => 1, canSeeDesktop: false);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var fallback = new GuiProgramFallback(tracker, pid => pid == 1 ? cmd : null);

        Assert.False(fallback.IsForegroundKnown);
        Assert.False(fallback.MightBeForeground(1, imagePath: null));   // path from the pid: cmd.exe, a console program
        Assert.True(fallback.MightBeForeground(2, imagePath: null));    // no path at all
        Assert.True(fallback.MightBeForeground(3, Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".exe")));

        tracker.Report(1, 1, 0);
        Assert.True(fallback.IsForegroundKnown);
    }
}
