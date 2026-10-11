using ProGuard.UI.Services;

namespace ProGuard.Tests;

public class SingleInstanceTests
{
    private static string UniqueName() => "ProGuard.Tests.SingleInstance." + Guid.NewGuid().ToString("N");

    [Fact]
    public void ASecondLaunch_AsksTheFirstToShowItself_AndIsToldToExit()
    {
        var name = UniqueName();
        using var shown = new ManualResetEventSlim();
        Assert.True(SingleInstance.TryClaim(() => shown.Set(), out var first, name: name));
        using (first)
        {
            Assert.NotNull(first);

            Assert.False(SingleInstance.TryClaim(() => { }, out var second, name: name));
            Assert.Null(second);
            Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "the first copy was never asked to show its window");
        }
    }

    [Fact]
    public void ASecondSignInStart_ExitsWithoutShowingTheFirst()
    {
        var name = UniqueName();
        int shows = 0;
        Assert.True(SingleInstance.TryClaim(() => Interlocked.Increment(ref shows), out var first, name: name));
        using (first)
        {
            Assert.False(SingleInstance.TryClaim(() => { }, out _, askToShow: false, name: name));
            Thread.Sleep(200);
            Assert.Equal(0, shows);
        }
    }

    [Fact]
    public void OnceTheFirstCopyHasGone_TheNextLaunchRunsNormally()
    {
        var name = UniqueName();
        Assert.True(SingleInstance.TryClaim(() => { }, out var first, name: name));
        first!.Dispose();

        Assert.True(SingleInstance.TryClaim(() => { }, out var next, name: name));
        using (next) Assert.NotNull(next);
    }
}
