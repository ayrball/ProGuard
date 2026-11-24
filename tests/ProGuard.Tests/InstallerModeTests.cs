using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Engine;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;

namespace ProGuard.Tests;

/// <summary>
/// The engine's installer modes that replaced the MSI's PowerShell, reg.exe and sc.exe custom actions:
/// <c>--stop-clients</c> (<see cref="ClientShutdown"/>) and <c>--configure-recovery</c> (<see cref="ServiceRecovery"/>).
/// Nothing here touches a real ProGuard window, session agent or service: the processes are fakes or copies under
/// unique names in a temporary folder, and the service is a fake.
/// </summary>
public class InstallerModeTests
{
    private const string Folder = @"C:\Program Files\ProGuard";

    private sealed class FakeProcess : IClientProcess
    {
        public required int Pid { get; init; }
        public int SessionId { get; init; } = 1;
        public required string Name { get; init; }
        public string? ImagePath { get; init; }
        /// <summary>Quits when its session's windows are asked to.</summary>
        public bool QuitsWhenAsked { get; init; }
        public bool HasExited { get; set; }
        public bool Terminated { get; private set; }
        public bool Disposed { get; private set; }
        public bool WaitForExit(TimeSpan timeout) => HasExited;
        public void Terminate() { Terminated = true; HasExited = true; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProcesses : IClientProcesses
    {
        public List<FakeProcess> All { get; } = new();
        public List<int> AskedSessions { get; } = new();
        public IReadOnlyList<string>? AskedFor { get; private set; }

        public IReadOnlyList<IClientProcess> Find(IReadOnlyList<string> imageNames)
        {
            AskedFor = imageNames;
            return All.Where(p => imageNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        public int AskWindowsToQuit(int sessionId)
        {
            AskedSessions.Add(sessionId);
            var windows = All.Where(p => p.SessionId == sessionId && p.Name == ClientShutdown.UiImage && p.QuitsWhenAsked).ToList();
            foreach (var w in windows) w.HasExited = true;
            return windows.Count;
        }
    }

    private static ClientShutdown Shutdown(FakeProcesses processes) => new()
    {
        Logger = NullLogger.Instance,
        Processes = processes,
        InstallDirectory = Folder,
        GracePeriod = TimeSpan.Zero,
    };

    [Fact]
    public void StopClients_LooksForTheWindowAndTheSessionAgent()
    {
        var processes = new FakeProcesses();
        Shutdown(processes).StopAll();
        Assert.Equal(new[] { "ProGuard.UI", "ProGuard.SessionAgent" }, processes.AskedFor);
    }

    [Fact]
    public void StopClients_AsksEachSessionsWindowToQuit_AndTerminatesOnlyWhatIsLeft()
    {
        var processes = new FakeProcesses();
        var polite = new FakeProcess { Pid = 10, SessionId = 1, Name = "ProGuard.UI", ImagePath = Folder + @"\ProGuard.UI.exe", QuitsWhenAsked = true };
        var stuck = new FakeProcess { Pid = 11, SessionId = 2, Name = "ProGuard.UI", ImagePath = Folder + @"\ProGuard.UI.exe" };
        var agent = new FakeProcess { Pid = 12, SessionId = 1, Name = "ProGuard.SessionAgent", ImagePath = Folder + @"\ProGuard.SessionAgent.exe" };
        processes.All.AddRange(new[] { polite, stuck, agent });

        Assert.Equal(3, Shutdown(processes).StopAll());

        Assert.Equal(new[] { 1, 2 }, processes.AskedSessions.OrderBy(s => s));
        Assert.False(polite.Terminated);
        Assert.True(stuck.Terminated);
        Assert.True(agent.Terminated);
        Assert.All(processes.All, p => Assert.True(p.Disposed));
    }

    [Fact]
    public void StopClients_LeavesCopiesOutsideTheInstallFolderAlone()
    {
        var processes = new FakeProcesses();
        var portable = new FakeProcess { Pid = 20, Name = "ProGuard.UI", ImagePath = @"D:\Tools\ProGuard\ProGuard.UI.exe" };
        // Same prefix, different folder: "ProGuard2" isn't inside "ProGuard".
        var sibling = new FakeProcess { Pid = 21, Name = "ProGuard.SessionAgent", ImagePath = @"C:\Program Files\ProGuard2\ProGuard.SessionAgent.exe" };
        var unreadable = new FakeProcess { Pid = 22, Name = "ProGuard.UI", ImagePath = null };
        var relative = new FakeProcess { Pid = 23, Name = "ProGuard.UI", ImagePath = "ProGuard.UI.exe" };
        processes.All.AddRange(new[] { portable, sibling, unreadable, relative });

        Assert.Equal(0, Shutdown(processes).StopAll());

        Assert.Empty(processes.AskedSessions);
        Assert.All(processes.All, p => Assert.False(p.Terminated));
        Assert.All(processes.All, p => Assert.True(p.Disposed));
    }

    [Fact]
    public void StopClients_MatchesTheFolderWhateverItsCase()
    {
        var processes = new FakeProcesses();
        var agent = new FakeProcess { Pid = 30, Name = "ProGuard.SessionAgent", ImagePath = @"c:\program files\proguard\ProGuard.SessionAgent.exe" };
        processes.All.Add(agent);
        Assert.Equal(1, Shutdown(processes).StopAll());
        Assert.True(agent.Terminated);
    }

    [Fact]
    public void StopClients_IsAnInstallerMode()
    {
        Assert.True(ClientShutdown.IsRequested(new[] { "--STOP-CLIENTS" }));
        Assert.False(ClientShutdown.IsRequested(new[] { "--cleanup-uninstall" }));
        Assert.True(ServiceRecovery.IsRequested(new[] { "--configure-recovery" }));
        Assert.False(ServiceRecovery.IsRequested(Array.Empty<string>()));
    }

    /// <summary>A real process: a copy of ping.exe under a unique name, in a temporary "install folder".</summary>
    [Fact]
    public void StopClients_TerminatesARealProcessRunningFromTheFolder_AndNotACopyElsewhere()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", "stop-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "install");
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(elsewhere);
        var name = "pgtest" + Guid.NewGuid().ToString("N")[..8];
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        File.Copy(ping, Path.Combine(installed, name + ".exe"));
        File.Copy(ping, Path.Combine(elsewhere, name + ".exe"));

        Process Start(string folder) => Process.Start(new ProcessStartInfo(Path.Combine(folder, name + ".exe"), "-n 60 127.0.0.1")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;

        using var ours = Start(installed);
        using var theirs = Start(elsewhere);
        try
        {
            var shutdown = new ClientShutdown
            {
                Logger = NullLogger.Instance,
                // Unique window names, so no real ProGuard window is ever asked to quit.
                Processes = new Win32ClientProcesses(new[] { "ProGuard.Tests." + name }),
                InstallDirectory = installed,
                WindowImage = name,
                OtherImages = Array.Empty<string>(),
                GracePeriod = TimeSpan.FromMilliseconds(200),
            };
            Assert.Equal(1, shutdown.StopAll());

            Assert.True(ours.WaitForExit(5000), "the copy in the install folder kept running");
            Assert.False(theirs.HasExited, "the copy elsewhere was ended too");
        }
        finally
        {
            foreach (var p in new[] { ours, theirs }) { try { if (!p.HasExited) p.Kill(); p.WaitForExit(5000); } catch (InvalidOperationException) { } }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The engine reaches each session's window through <c>Session\&lt;id&gt;\</c>, the same objects the window creates
    /// in its own <c>Local\</c> namespace. Checked within this test's session, under a unique name.
    /// </summary>
    [Fact]
    public void AWindowsQuitEvent_ReachesIt_ThroughItsSessionsNamespace()
    {
        var instance = "ProGuard.Tests.Quit." + Guid.NewGuid().ToString("N");
        using var quit = new ManualResetEventSlim();
        Assert.True(SingleInstance.TryClaim(() => { }, out var claimed, name: instance, onQuitRequested: () => quit.Set()));
        using (claimed)
        {
            var session = Process.GetCurrentProcess().SessionId;
            Assert.Equal(1, new Win32ClientProcesses(new[] { instance }).AskWindowsToQuit(session));
            Assert.True(quit.Wait(TimeSpan.FromSeconds(5)), "the window was never asked to quit");
        }
    }

    [Fact]
    public void AskingASessionWithNoWindow_AsksNobody()
    {
        var session = Process.GetCurrentProcess().SessionId;
        Assert.Equal(0, new Win32ClientProcesses(new[] { "ProGuard.Tests.NoSuchWindow." + Guid.NewGuid().ToString("N") }).AskWindowsToQuit(session));
    }

    [Fact]
    public void TheQuitEventName_FollowsTheWindowsInstanceName()
    {
        Assert.Equal("ProGuard.UI.Quit", UiInstanceNames.QuitEvent(SingleInstance.NameFor(elevated: false)));
        Assert.Equal("ProGuard.UI.Elevated.Quit", UiInstanceNames.QuitEvent(SingleInstance.NameFor(elevated: true)));
    }

    private sealed class FakeService : IServiceFailureActions
    {
        public string? Name;
        public TimeSpan Reset;
        public IReadOnlyList<TimeSpan>? Delays;
        public Exception? Fails;

        public void Set(string serviceName, TimeSpan resetPeriod, IReadOnlyList<TimeSpan> restartDelays)
        {
            if (Fails != null) throw Fails;
            Name = serviceName;
            Reset = resetPeriod;
            Delays = restartDelays;
        }
    }

    [Fact]
    public void ConfigureRecovery_RestartsAfter5_10_And60Seconds_AndResetsDaily()
    {
        var service = new FakeService();
        Assert.Equal(0, ServiceRecovery.Run(service));
        Assert.Equal("ProGuardEngine", service.Name);
        Assert.Equal(TimeSpan.FromSeconds(86400), service.Reset);
        Assert.Equal(new[] { 5000.0, 10000.0, 60000.0 }, service.Delays!.Select(d => d.TotalMilliseconds));
    }

    [Fact]
    public void ConfigureRecovery_ExitsWith1_WhenTheSettingsCantBeWritten_SoTheInstallRollsBack()
    {
        var service = new FakeService { Fails = new System.ComponentModel.Win32Exception(5) };
        Assert.Equal(1, ServiceRecovery.Run(service));
    }

    /// <summary>
    /// The real ChangeServiceConfig2 on a throw-away service that is never started, created and deleted here. Only on
    /// CI (GitHub Actions runners are elevated and disposable): it never creates a service on a developer's machine.
    /// </summary>
    [ThrowawayServiceFact]
    public void ConfigureRecovery_WritesTheActionsWindowsReadsBack()
    {
        var name = "ProGuardTest" + Guid.NewGuid().ToString("N")[..8];
        var manager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        Assert.NotEqual(IntPtr.Zero, manager);
        var service = CreateService(manager, name, name, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, SERVICE_DEMAND_START,
            SERVICE_ERROR_IGNORE, "\"" + Path.Combine(Environment.SystemDirectory, "PING.EXE") + "\"", null, IntPtr.Zero, null, null, null);
        Assert.NotEqual(IntPtr.Zero, service);
        try
        {
            new Win32ServiceFailureActions().Set(name, ServiceRecovery.ResetPeriod, ServiceRecovery.RestartDelays);

            QueryServiceConfig2(service, 2, IntPtr.Zero, 0, out var needed);
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)needed);
            try
            {
                Assert.True(QueryServiceConfig2(service, 2, buffer, needed, out _));
                // SERVICE_FAILURE_ACTIONS on x64: DWORD reset, (padding), LPWSTR reboot, LPWSTR command, DWORD count, (padding), SC_ACTION*.
                var reset = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, 0);
                var count = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, 24);
                var actions = System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer, 32);
                Assert.Equal(86400, reset);
                Assert.Equal(3, count);
                var read = Enumerable.Range(0, count).Select(i => (
                    Type: System.Runtime.InteropServices.Marshal.ReadInt32(actions, i * 8),
                    Delay: System.Runtime.InteropServices.Marshal.ReadInt32(actions, i * 8 + 4))).ToArray();
                Assert.Equal(new[] { (1, 5000), (1, 10000), (1, 60000) }, read); // SC_ACTION_RESTART
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            DeleteService(service);
            CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const uint SERVICE_DEMAND_START = 3;
    private const uint SERVICE_ERROR_IGNORE = 0;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "CreateServiceW")]
    private static extern IntPtr CreateService(IntPtr manager, string name, string displayName, uint access, uint type, uint start,
        uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfig2W")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(IntPtr service, uint infoLevel, IntPtr buffer, uint size, out uint needed);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [Fact]
    public void ConfigureRecovery_ForAServiceThatDoesntExist_FailsWithoutChangingAnything()
    {
        // The real Service Control Manager, but a service name nobody has: OpenService fails before anything is written.
        var actions = new Win32ServiceFailureActions();
        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            actions.Set("ProGuard.Tests.NoSuchService." + Guid.NewGuid().ToString("N"), TimeSpan.FromDays(1), new[] { TimeSpan.FromSeconds(5) }));
    }
}

/// <summary>Runs only on GitHub Actions with an elevated test host, where creating and deleting a throw-away service is harmless.</summary>
public sealed class ThrowawayServiceFactAttribute : FactAttribute
{
    public ThrowawayServiceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            Skip = "Creates a throw-away service: runs on CI only.";
        else if (!ElevationFactAttribute.IsElevated)
            Skip = "Needs an elevated test host to create a service.";
    }
}
