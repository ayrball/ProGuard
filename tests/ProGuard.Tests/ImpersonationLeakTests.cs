using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using ProGuard.Core.Native;
using ProGuard.Engine.IPC;

namespace ProGuard.Tests;

/// <summary>
/// The engine checks each request by impersonating the pipe client. Whatever it does afterwards (later requests,
/// timers a request creates, await continuations) must run as the engine again: on the installed service a leaked
/// client identity made "Purge now" fail to open SYSTEM's token and SmartTrim timers set up over IPC run as the user.
/// </summary>
public class ImpersonationLeakTests
{
    private static TokenImpersonationLevel CurrentLevel()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.ImpersonationLevel;
    }

    [Fact]
    public async Task CheckingTheCaller_LeavesNoClientIdentityBehind()
    {
        var name = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var release = new ManualResetEventSlim();
        var client = Task.Run(() =>
        {
            using var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation);
            c.Connect(5000);
            new StreamWriter(c) { AutoFlush = true }.WriteLine("hello");
            release.Wait(10000);
        });
        try
        {
            await server.WaitForConnectionAsync();
            await new StreamReader(server).ReadLineAsync();
            Assert.Equal(TokenImpersonationLevel.None, CurrentLevel());

            var caller = PipeCaller.Identify(server);
            Assert.NotNull(caller);
            Assert.True(caller!.CanOpenProcess(Environment.ProcessId, ProcessAccessFlags.QueryLimitedInformation));

            Assert.Equal(TokenImpersonationLevel.None, CurrentLevel());
            Assert.Equal(TokenImpersonationLevel.None, await Task.Run(CurrentLevel));
            var fromTimer = new TaskCompletionSource<TokenImpersonationLevel>();
            using (new Timer(_ => fromTimer.TrySetResult(CurrentLevel()), null, 50, Timeout.Infinite))
                Assert.Equal(TokenImpersonationLevel.None, await fromTimer.Task);
            await Task.Yield();
            Assert.Equal(TokenImpersonationLevel.None, CurrentLevel());
        }
        finally
        {
            release.Set();
            await client;
        }
    }
}
