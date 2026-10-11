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
#pragma warning restore CA2255
}
