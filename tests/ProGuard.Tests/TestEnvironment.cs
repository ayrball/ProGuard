using System;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using ProGuard.Core.Data;

namespace ProGuard.Tests;

/// <summary>
/// Points every <see cref="ProGuardDbContext"/> created during the test run (including the ones the view
/// models create for themselves) at a throw-away database, and every default engine connection at a pipe no engine
/// serves. Without this the tests would read and write the real shared database in %ProgramData%\ProGuard that a
/// live engine is also using, and send commands to that engine.
/// </summary>
internal static class TestEnvironment
{
#pragma warning disable CA2255 // module initializer is intentional: it must run before any test touches the database
    [ModuleInitializer]
    internal static void IsolateDatabase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(ProGuardDbContext.DatabasePathVariable, Path.Combine(dir, "proguard.db"));
        // Likewise for the engine's pipe: an EngineClient built with the default pipe name must never reach an installed
        // engine (a test toggling Performance Mode used to switch the real machine's power plan through it).
        Environment.SetEnvironmentVariable(ProGuard.Shared.Client.EngineClient.PipeNameVariable, "ProGuardTests_" + Guid.NewGuid().ToString("N"));
        // The same for foreground reports and the session agent's mutex, which agents the tests start inherit: they must
        // neither report to an installed engine nor make the UI on this desktop stop reporting.
        Environment.SetEnvironmentVariable(ProGuard.Shared.Reporting.ForegroundReporter.PipeNameVariable, "ProGuardTests_Fg_" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(ProGuard.Shared.Reporting.SessionAgentInstance.MutexNameVariable, @"Local\ProGuardTests_Agent_" + Guid.NewGuid().ToString("N"));

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort cleanup of a temp folder */ }
        };
    }

    /// <summary>
    /// xUnit runs the test classes side by side, and many of them park a thread pool thread for a while: a pipe client
    /// waiting for its server, the engine's impersonation (which waits on a second pool thread), a child process being
    /// waited for. The pool starts with one thread per CPU and adds only a couple a second when they are all busy, so on a
    /// 2-CPU CI runner a few such tests at once stall everything else for seconds, and pipe tests ran into the client's
    /// 8 s request timeout. Starting the pool where a developer machine's would be keeps the timing the same everywhere.
    /// </summary>
    [ModuleInitializer]
    internal static void SizeThreadPool()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, MinPoolThreads), Math.Max(io, MinPoolThreads));
    }

    private const int MinPoolThreads = 16;
#pragma warning restore CA2255
}
