using System.Windows.Threading;
using ProGuard.Shared.Client;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>
/// "Restart as administrator": offered only to a window with standard rights, reopens on the same page, and quits this
/// copy only once Windows has started the new one. A fake launcher stands in, so no test ever raises a UAC prompt.
/// </summary>
public class UiElevationTests
{
    private sealed class FakeLauncher(bool elevated, ElevationOutcome outcome = ElevationOutcome.Started, string? error = null) : IElevationLauncher
    {
        /// <summary>The page each restart asked for.</summary>
        public List<string?> Restarts { get; } = new();
        public bool IsElevated => elevated;
        public bool AlwaysElevate { get; set; }

        public ElevationOutcome Restart(IReadOnlyList<string> arguments, out string? failure)
        {
            Restarts.Add(ElevationLauncher.PageFrom(arguments));
            failure = error;
            return outcome;
        }
    }

    private static (ShellViewModel Shell, FakeLauncher Launcher, List<string> Quits) New(bool elevated = false, ElevationOutcome outcome = ElevationOutcome.Started, string? error = null)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var toaster = new Toaster(dispatcher);
        var hub = new EngineHub(new EngineClient("ProGuard.Tests.NoEngine." + Guid.NewGuid().ToString("N")), dispatcher, toaster);
        var launcher = new FakeLauncher(elevated, outcome, error);
        var quits = new List<string>();
        var shell = new ShellViewModel(hub, toaster, new IconCache(dispatcher), elevation: launcher);
        shell.Quit = () => quits.Add(shell.CurrentPage.Title);
        return (shell, launcher, quits);
    }

    /// <summary>The toasts once everything already posted to this thread's dispatcher has run.</summary>
    private static Toast[] Toasts(ShellViewModel shell)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        return shell.Toaster.Items.ToArray();
    }

    [Fact]
    public void StandardWindow_RestartsOnTheSamePage_AndQuitsOnceStarted()
    {
        var (shell, launcher, quits) = New();
        Assert.True(shell.CanRestartElevated);
        shell.GoTo(shell.Processes);

        Assert.True(shell.RestartElevatedCommand.CanExecute(null));
        shell.RestartElevatedCommand.Execute(null);

        Assert.Equal(new[] { "PROCESSES" }, launcher.Restarts);
        Assert.Equal(new[] { "PROCESSES" }, quits);
        Assert.Empty(Toasts(shell));
    }

    [Fact]
    public void ElevatedWindow_DoesNotOfferIt()
    {
        var (shell, launcher, quits) = New(elevated: true);

        Assert.False(shell.CanRestartElevated);
        Assert.False(shell.RestartElevatedCommand.CanExecute(null));
        Assert.Null(shell.Toaster.Elevate);
        shell.RestartElevatedCommand.Execute(null);
        Assert.Empty(launcher.Restarts);
        Assert.Empty(quits);
    }

    [Fact]
    public void DecliningTheUacPrompt_LeavesTheWindowAsItWas()
    {
        var (shell, launcher, quits) = New(outcome: ElevationOutcome.Cancelled);

        shell.RestartElevatedCommand.Execute(null);

        Assert.Single(launcher.Restarts);
        Assert.Empty(quits);
        Assert.Empty(Toasts(shell)); // saying no needs no comment
    }

    [Fact]
    public void AFailedRestart_SaysWhy_StaysOpen_AndDoesNotOfferToRetryFromTheToast()
    {
        var (shell, _, quits) = New(outcome: ElevationOutcome.Failed, error: "Windows couldn't start an administrator copy of ProGuard: the file is gone.");

        shell.RestartElevatedCommand.Execute(null);

        Assert.Empty(quits);
        var toast = Assert.Single(Toasts(shell));
        Assert.True(toast.IsError);
        Assert.Contains("the file is gone", toast.Message);
        Assert.Null(toast.Action);
    }

    [Fact]
    public void ToastsThatNeedAdministratorRights_CarryTheRestartButton()
    {
        var (shell, launcher, quits) = New();
        shell.GoTo(shell.System);

        shell.Toaster.Show("Access denied: PC\\tee may not purge memory. Run ProGuard as administrator to do this.", isError: true);
        shell.Toaster.Show("I/O priority High needs administrator rights. Restart ProGuard as administrator to use it.", isError: true);
        shell.Toaster.Show("RULE SAVED · game.exe");

        var toasts = Toasts(shell);
        Assert.Equal(3, toasts.Length);
        Assert.Null(toasts[0].Action); // newest first: the ordinary one has no button
        Assert.All(toasts.Skip(1), t => Assert.Equal(Toaster.ElevateText, t.ActionText));

        toasts[1].Action!.Execute(null);
        Assert.Equal(new[] { "SYSTEM" }, launcher.Restarts);
        Assert.Equal(new[] { "SYSTEM" }, quits);
        Assert.DoesNotContain(toasts[1], shell.Toaster.Items); // pressing it closes the toast
    }

    [Fact]
    public void ToastsInAnElevatedWindow_HaveNoRestartButton()
    {
        var (shell, _, _) = New(elevated: true);

        shell.Toaster.Show("Run ProGuard as administrator to do this.", isError: true);

        Assert.Null(Assert.Single(Toasts(shell)).Action);
    }

    [Fact]
    public void AlwaysStartAsAdministrator_IsKept_AndOffersToRestartNow()
    {
        var (shell, launcher, quits) = New();

        shell.AlwaysStartElevated = true;
        Assert.True(launcher.AlwaysElevate);
        var toast = Assert.Single(Toasts(shell));
        Assert.Contains("starts as administrator", toast.Message);
        Assert.NotNull(toast.Action); // restart now, from the toast

        shell.AlwaysStartElevated = false;
        Assert.False(launcher.AlwaysElevate);
        Assert.Single(Toasts(shell)); // turning it off says nothing
        Assert.Empty(quits);
    }

    [Fact]
    public void AlwaysStartAsAdministrator_InAnElevatedWindow_IsKeptQuietly()
    {
        var (shell, launcher, _) = New(elevated: true);

        shell.AlwaysStartElevated = true;

        Assert.True(launcher.AlwaysElevate);
        Assert.Empty(Toasts(shell));
    }

    [Theory]
    [InlineData(null, null, false)] // nothing chosen, not installed (a build run from source)
    [InlineData(null, 1, true)]     // the installer's default
    [InlineData(null, 0, false)]    // installed with START_AS_ADMIN=0
    [InlineData(0, 1, false)]       // the user turned it off: their choice wins
    [InlineData(1, 0, true)]        // and the other way round
    public void AlwaysElevate_TheUsersChoiceWins_ThenTheInstallersDefault(int? user, int? machine, bool expected) =>
        Assert.Equal(expected, ElevationLauncher.AlwaysElevateFrom(user, machine));

    [Fact]
    public void PageArguments_RoundTripThroughPageFrom()
    {
        Assert.Equal("ADVANCED", ElevationLauncher.PageFrom(ElevationLauncher.PageArguments("ADVANCED")));
        Assert.Empty(ElevationLauncher.PageArguments(null));
    }

    [Theory]
    [InlineData("Run ProGuard as administrator to do this.", true)]
    [InlineData("Purge needs administrator rights.", true)]
    [InlineData("Windows couldn't start an administrator copy of ProGuard.", false)]
    [InlineData("RULE SAVED", false)]
    public void NeedsAdministrator_RecognisesTheRefusals(string message, bool expected) =>
        Assert.Equal(expected, Toaster.NeedsAdministrator(message));

    [Fact]
    public void GoToByTitle_IgnoresCase_AndUnknownTitles()
    {
        var (shell, _, _) = New();

        shell.GoTo("rules");
        Assert.Same(shell.Rules, shell.CurrentPage.Page);

        shell.GoTo("NO SUCH PAGE");
        Assert.Same(shell.Rules, shell.CurrentPage.Page);
    }

    [Theory]
    [InlineData(new[] { "--page", "PROCESSES" }, "PROCESSES")]
    [InlineData(new[] { "--tray", "--PAGE", "System" }, "System")]
    [InlineData(new[] { "--page" }, null)]
    [InlineData(new string[0], null)]
    public void PageFrom_ReadsThePageSwitch(string[] args, string? expected) =>
        Assert.Equal(expected, ElevationLauncher.PageFrom(args));
}
