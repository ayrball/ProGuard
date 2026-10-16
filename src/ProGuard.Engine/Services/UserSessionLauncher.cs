using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;
using ProGuard.Engine.Native;

namespace ProGuard.Engine.Services;

/// <summary>
/// A program to start in a user's session. <see cref="SessionId"/> null means the session at the physical console.
/// <see cref="ExecutablePath"/> null lets CreateProcess find the program named at the start of <see cref="CommandLine"/>.
/// </summary>
public sealed record LaunchRequest(string? ExecutablePath, string CommandLine, string? WorkingDirectory = null, int? SessionId = null)
{
    public static LaunchRequest From(ProcessLaunchInfo info) =>
        new(info.ExecutablePath, info.CommandLine, info.WorkingDirectory, info.SessionId);
}

/// <summary>Starts processes on a user's desktop on behalf of the engine (Watchdog Restart and Launch, Keep Running).</summary>
public interface IUserSessionLauncher
{
    /// <summary>Starts the program and returns its process id. Throws when it can't be started.</summary>
    int Launch(LaunchRequest request);
}

/// <summary>
/// The engine runs as LocalSystem in session 0, where nothing is visible and anything it started would run as SYSTEM.
/// This starts the program as the user signed in to the target session instead: WTSQueryUserToken gives that user's
/// token (the filtered, unelevated one for a UAC administrator), and CreateProcessAsUser puts the process on their
/// interactive desktop, winsta0\default, with their own environment block.
/// <para>
/// Never launches into session 0: that is where services live, and a process started there would be a SYSTEM process
/// outside the Service Control Manager. When the engine is not SYSTEM (run from a console while developing),
/// WTSQueryUserToken is refused, so a launch into the engine's own session starts the program as the engine's user.
/// </para>
/// </summary>
public class UserSessionLauncher : IUserSessionLauncher
{
    private const string InteractiveDesktop = @"winsta0\default";

    private readonly ILogger<UserSessionLauncher> _logger;

    public UserSessionLauncher(ILogger<UserSessionLauncher> logger)
    {
        _logger = logger;
    }

    public int Launch(LaunchRequest request)
    {
        using var process = Start(request, out var pid);
        return pid;
    }

    /// <summary>
    /// <see cref="Launch"/>, keeping a handle to the new process (to wait for it or end it); the caller disposes it.
    /// </summary>
    public SafeProcessHandle Start(LaunchRequest request, out int processId)
    {
        if (string.IsNullOrWhiteSpace(request.CommandLine) && string.IsNullOrWhiteSpace(request.ExecutablePath))
            throw new ArgumentException("Nothing to launch: no program or command line", nameof(request));

        var session = ResolveSession(request.SessionId, Kernel32.WTSGetActiveConsoleSessionId());
        var commandLine = string.IsNullOrWhiteSpace(request.CommandLine)
            ? WatchdogArguments.BuildCommandLine(request.ExecutablePath!, null)
            : request.CommandLine;
        var directory = ResolveWorkingDirectory(request);

        LaunchNative.PROCESS_INFORMATION info;
        if (IsRunningAsSystem())
        {
            info = LaunchAsSessionUser(session, request.ExecutablePath, commandLine, directory);
        }
        else if (Kernel32.ProcessIdToSessionId((uint)Environment.ProcessId, out var own) && own == session)
        {
            info = LaunchAsSelf(request.ExecutablePath, commandLine, directory);
        }
        else
        {
            throw new InvalidOperationException($"Only the engine service (LocalSystem) can start programs in session {session}");
        }

        Kernel32.CloseHandle(info.hThread);
        processId = info.dwProcessId;
        _logger.LogInformation("Launched {CommandLine} as process {Pid} in session {Session}", commandLine, processId, session);
        return new SafeProcessHandle(info.hProcess, ownsHandle: true);
    }

    /// <summary>The session to launch into: the requested one, else the console's. Session 0 is refused.</summary>
    internal static uint ResolveSession(int? requested, uint consoleSession)
    {
        if (requested is int wanted)
        {
            if (wanted <= 0) throw new InvalidOperationException("Refusing to launch into session 0, where only services run");
            return (uint)wanted;
        }

        if (consoleSession == LaunchNative.INVALID_SESSION || consoleSession == 0)
            throw new InvalidOperationException("No user session is attached to the console");
        return consoleSession;
    }

    internal static string ResolveWorkingDirectory(LaunchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory) && Directory.Exists(request.WorkingDirectory))
            return request.WorkingDirectory;

        var program = request.ExecutablePath;
        if (string.IsNullOrWhiteSpace(program) && WatchdogArguments.TrySplitCommandLine(request.CommandLine, out var first, out _))
            program = first;
        var folder = !string.IsNullOrWhiteSpace(program) && Path.IsPathRooted(program) ? Path.GetDirectoryName(program) : null;
        return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : Environment.SystemDirectory;
    }

    internal static bool IsRunningAsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }

    private static LaunchNative.PROCESS_INFORMATION LaunchAsSessionUser(uint session, string? program, string commandLine, string directory)
    {
        IntPtr userToken = IntPtr.Zero, primaryToken = IntPtr.Zero, environment = IntPtr.Zero;
        try
        {
            if (!LaunchNative.WTSQueryUserToken(session, out userToken))
            {
                var error = Marshal.GetLastWin32Error();
                throw error == LaunchNative.ERROR_NO_TOKEN
                    ? new InvalidOperationException($"No user is signed in to session {session}")
                    : new Win32Exception(error, $"WTSQueryUserToken({session}) failed");
            }

            if (!LaunchNative.DuplicateTokenEx(userToken, LaunchNative.TOKEN_ALL_ACCESS, IntPtr.Zero,
                    LaunchNative.SecurityImpersonation, LaunchNative.TokenPrimary, out primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");

            if (!LaunchNative.CreateEnvironmentBlock(out environment, primaryToken, inherit: false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed");

            var startup = NewStartupInfo();
            if (!LaunchNative.CreateProcessAsUser(primaryToken, program, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
                    false, CreationFlags, environment, directory, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessAsUser failed for {commandLine}");

            return info;
        }
        finally
        {
            if (environment != IntPtr.Zero) LaunchNative.DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) Kernel32.CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) Kernel32.CloseHandle(userToken);
        }
    }

    private static LaunchNative.PROCESS_INFORMATION LaunchAsSelf(string? program, string commandLine, string directory)
    {
        var startup = NewStartupInfo();
        if (!LaunchNative.CreateProcess(program, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
                false, LaunchNative.CREATE_NEW_CONSOLE | LaunchNative.CREATE_DEFAULT_ERROR_MODE, IntPtr.Zero, directory, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for {commandLine}");
        return info;
    }

    private const uint CreationFlags =
        LaunchNative.CREATE_UNICODE_ENVIRONMENT | LaunchNative.CREATE_NEW_CONSOLE | LaunchNative.CREATE_DEFAULT_ERROR_MODE;

    private static LaunchNative.STARTUPINFO NewStartupInfo() => new()
    {
        cb = Marshal.SizeOf<LaunchNative.STARTUPINFO>(),
        lpDesktop = InteractiveDesktop
    };
}
