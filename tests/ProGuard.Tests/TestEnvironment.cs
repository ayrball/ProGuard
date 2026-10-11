using System;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using ProGuard.Core.Data;

namespace ProGuard.Tests;

/// <summary>
/// Points every <see cref="ProGuardDbContext"/> created during the test run (including the ones the view
/// models create for themselves) at a throw-away database. Without this the tests would read and write the
/// real shared database in %ProgramData%\ProGuard that a live engine is also using.
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
