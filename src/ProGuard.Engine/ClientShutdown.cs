using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ProGuard.Engine.Native;
using ProGuard.Shared.Messages;

namespace ProGuard.Engine;

/// <summary>
/// <c>ProGuard.Engine.exe --stop-clients</c>: what the MSI runs, as LocalSystem, after it has stopped the service and
/// before it replaces or removes the files (on uninstall, repair, and, from the old package, on upgrade). It ends the
/// ProGuard windows (<c>ProGuard.UI.exe</c>) and session agents (<c>ProGuard.SessionAgent.exe</c>) still running from
/// this installation's folder, in every session, so none of their files is in use.
/// <list type="bullet">
/// <item>Only processes whose image is inside the folder this exe runs from: a portable copy elsewhere, with the same
/// file names, is left alone.</item>
/// <item>Each window is first asked to quit, through its session's quit event (<see cref="UiInstanceNames.QuitEvent"/>),
/// so it removes its tray icon and closes its pipes as its tray menu's Exit would. Whatever is still running after
/// <see cref="GracePeriod"/> is terminated.</item>
/// <item>Session agents have no window and keep nothing to save. The engine ends them itself when the service stops
/// (<c>SessionAgentSupervisor</c>), so normally none is left; any that is, is terminated at once.</item>
/// </list>
/// No shell, no PowerShell: the processes are found and ended through the Win32 API. It always exits 0, so a problem
/// here never fails an uninstall; a file still in use is then replaced at the next restart, as Windows Installer does.
/// </summary>
internal sealed class ClientShutdown
{
    public const string Argument = "--stop-clients";

    public const string UiImage = "ProGuard.UI";
    public const string AgentImage = "ProGuard.SessionAgent";

    public static bool IsRequested(string[] args) => args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    public required ILogger Logger { get; init; }
    public required IClientProcesses Processes { get; init; }
    /// <summary>The folder ProGuard was installed to: only processes running from inside it are ended.</summary>
    public required string InstallDirectory { get; init; }
    /// <summary>How long the windows get to quit on their own before they are terminated.</summary>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>The image names to look for, without .exe: the window, which is asked to quit first, then the rest.</summary>
    public string WindowImage { get; init; } = UiImage;
    public IReadOnlyList<string> OtherImages { get; init; } = new[] { AgentImage };

    /// <summary>The real thing, for <c>Main</c>. Always returns 0.</summary>
    public static int Run()
    {
        using var factory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
        var logger = factory.CreateLogger("ProGuard.Engine.ClientShutdown");
        try
        {
            new ClientShutdown { Logger = logger, Processes = new Win32ClientProcesses(), InstallDirectory = AppContext.BaseDirectory }.StopAll();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stopping ProGuard's windows and session agents failed");
        }
        return 0;
    }

    /// <summary>Asks this installation's windows to quit, waits, then terminates what is left. Returns how many processes were running.</summary>
    public int StopAll()
    {
        var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallDirectory)) + Path.DirectorySeparatorChar;
        var ours = new List<IClientProcess>();
        foreach (var process in Processes.Find(OtherImages.Prepend(WindowImage).ToList()))
        {
            if (process.ImagePath is { } path && Path.IsPathRooted(path)
                && Path.GetFullPath(path).StartsWith(installed, StringComparison.OrdinalIgnoreCase))
                ours.Add(process);
            else
                process.Dispose();
        }

        try
        {
            if (ours.Count == 0)
            {
                Logger.LogInformation("No ProGuard window or session agent is running from {Folder}", installed);
                return 0;
            }

            var windows = ours.Where(p => p.Name.Equals(WindowImage, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var session in windows.Select(p => p.SessionId).Distinct())
            {
                var asked = Processes.AskWindowsToQuit(session);
                Logger.LogInformation("Asked {Count} ProGuard window(s) in session {Session} to quit", asked, session);
            }

            // The windows quit within a second; the deadline is shared, so many sessions don't add up.
            var deadline = Stopwatch.StartNew();
            foreach (var window in windows)
            {
                var left = GracePeriod - deadline.Elapsed;
                if (window.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                    Logger.LogInformation("ProGuard window {Pid} in session {Session} quit", window.Pid, window.SessionId);
            }

            foreach (var process in ours.Where(p => !p.HasExited))
            {
                try
                {
                    process.Terminate();
                    Logger.LogInformation("Ended {Name} {Pid} in session {Session}", process.Name, process.Pid, process.SessionId);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    Logger.LogWarning(ex, "Could not end {Name} {Pid}", process.Name, process.Pid);
                }
            }
            return ours.Count;
        }
        finally
        {
            foreach (var process in ours) process.Dispose();
        }
    }
}

/// <summary>The processes <see cref="ClientShutdown"/> looks for and ends; a fake in tests.</summary>
internal interface IClientProcesses
{
    /// <summary>Every running process with one of these image names (without .exe), each held open so its pid can't be reused meanwhile.</summary>
    IReadOnlyList<IClientProcess> Find(IReadOnlyList<string> imageNames);

    /// <summary>Sets the quit event of the standard and the elevated window in <paramref name="sessionId"/>; returns how many were there.</summary>
    int AskWindowsToQuit(int sessionId);
}

internal interface IClientProcess : IDisposable
{
    int Pid { get; }
    int SessionId { get; }
    /// <summary>The image name without .exe.</summary>
    string Name { get; }
    /// <summary>Full path of the exe; null if it couldn't be read.</summary>
    string? ImagePath { get; }
    bool HasExited { get; }
    bool WaitForExit(TimeSpan timeout);
    void Terminate();
}

/// <summary>The real processes, through OpenProcess, QueryFullProcessImageName and TerminateProcess.</summary>
internal sealed class Win32ClientProcesses : IClientProcesses
{
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint EVENT_MODIFY_STATE = 0x0002;

    private readonly IReadOnlyList<string> _windowInstances;

    /// <param name="windowInstances">The windows' <see cref="UiInstanceNames"/>; other names in tests, so they never reach a real window.</param>
    public Win32ClientProcesses(IReadOnlyList<string>? windowInstances = null) =>
        _windowInstances = windowInstances ?? new[] { UiInstanceNames.Standard, UiInstanceNames.Elevated };

    public IReadOnlyList<IClientProcess> Find(IReadOnlyList<string> imageNames)
    {
        var found = new List<IClientProcess>();
        foreach (var name in imageNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    int sessionId;
                    try { sessionId = process.SessionId; }
                    catch (InvalidOperationException) { continue; } // already gone

                    var handle = LaunchNative.OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, process.Id);
                    if (handle.IsInvalid) { handle.Dispose(); continue; }
                    found.Add(new Win32ClientProcess(handle, process.Id, sessionId, name, ImagePath(handle)));
                }
            }
        }
        return found;
    }

    public int AskWindowsToQuit(int sessionId)
    {
        var asked = 0;
        foreach (var instance in _windowInstances)
        {
            // Another session's Local\ namespace, as seen from session 0: Session\<id>\<name>.
            using var quit = OpenEvent(EVENT_MODIFY_STATE, false, $@"Session\{sessionId}\{UiInstanceNames.QuitEvent(instance)}");
            if (quit.IsInvalid) continue; // no such window, or one too old to have a quit event
            if (SetEvent(quit)) asked++;
        }
        return asked;
    }

    private static string? ImagePath(SafeProcessHandle handle)
    {
        var size = 1024;
        var name = new StringBuilder(size);
        return LaunchNative.QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenEventW")]
    private static extern SafeWaitHandle OpenEvent(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(SafeWaitHandle handle);

    private sealed class Win32ClientProcess : IClientProcess
    {
        private readonly SafeProcessHandle _handle;

        public Win32ClientProcess(SafeProcessHandle handle, int pid, int sessionId, string name, string? imagePath)
        {
            _handle = handle;
            Pid = pid;
            SessionId = sessionId;
            Name = name;
            ImagePath = imagePath;
        }

        public int Pid { get; }
        public int SessionId { get; }
        public string Name { get; }
        public string? ImagePath { get; }
        public bool HasExited => LaunchNative.WaitForSingleObject(_handle, 0) == WAIT_OBJECT_0;

        public bool WaitForExit(TimeSpan timeout) =>
            LaunchNative.WaitForSingleObject(_handle, (uint)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue)) == WAIT_OBJECT_0;

        public void Terminate()
        {
            if (!LaunchNative.TerminateProcess(_handle, 1) && !HasExited)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"TerminateProcess({Pid}) failed");
        }

        public void Dispose() => _handle.Dispose();
    }
}
