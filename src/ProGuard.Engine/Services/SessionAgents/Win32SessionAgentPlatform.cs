using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ProGuard.Engine.Native;
using ProGuard.Shared.Reporting;

namespace ProGuard.Engine.Services.SessionAgents;

/// <summary>
/// The real <see cref="ISessionAgentPlatform"/>: WTSEnumerateSessionsEx for the sessions, <see cref="UserSessionLauncher"/>
/// (WTSQueryUserToken + CreateProcessAsUser) to start the agent as the session's user, and process handles to watch it.
/// </summary>
public sealed class Win32SessionAgentPlatform : ISessionAgentPlatform
{
    private readonly UserSessionLauncher _launcher;

    public Win32SessionAgentPlatform(UserSessionLauncher launcher, string? agentPath = null)
    {
        _launcher = launcher;
        AgentPath = agentPath ?? Path.Combine(AppContext.BaseDirectory, SessionAgentInstance.ExecutableName);
    }

    /// <summary>The agent this engine starts: the copy installed next to it.</summary>
    public string AgentPath { get; }

    public bool IsAgentInstalled => File.Exists(AgentPath);

    public IReadOnlyList<UserSession> GetUserSessions()
    {
        int level = 1;
        if (!LaunchNative.WTSEnumerateSessionsEx(IntPtr.Zero, ref level, 0, out var buffer, out var count))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "WTSEnumerateSessionsEx failed");
        try
        {
            var sessions = new List<UserSession>(count);
            var size = Marshal.SizeOf<LaunchNative.WTS_SESSION_INFO_1>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<LaunchNative.WTS_SESSION_INFO_1>(buffer + i * size);
                // Session 0 is the services'; a session without a user is a sign-in screen or an idle listener.
                if (info.SessionId == 0 || string.IsNullOrEmpty(Marshal.PtrToStringUni(info.pUserName))) continue;
                sessions.Add(new UserSession(info.SessionId, info.State == LaunchNative.WTSActive));
            }
            return sessions;
        }
        finally
        {
            LaunchNative.WTSFreeMemoryEx(LaunchNative.WTSTypeSessionInfoLevel1, buffer, count);
        }
    }

    public IAgentProcess? FindRunningAgent(int sessionId)
    {
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AgentPath)))
        {
            using (candidate)
            {
                try
                {
                    if (candidate.SessionId != sessionId) continue;
                }
                catch (InvalidOperationException) { continue; }   // exited meanwhile

                var handle = LaunchNative.OpenProcess(
                    LaunchNative.SYNCHRONIZE | LaunchNative.PROCESS_TERMINATE | LaunchNative.PROCESS_QUERY_LIMITED_INFORMATION,
                    false, candidate.Id);
                if (handle.IsInvalid) { handle.Dispose(); continue; }

                // Only our own copy: anything else of that name is none of our business, and not ours to end.
                if (string.Equals(ImagePath(handle), AgentPath, StringComparison.OrdinalIgnoreCase))
                    return new Win32AgentProcess(handle, candidate.Id);
                handle.Dispose();
            }
        }
        return null;
    }

    public IAgentProcess Launch(int sessionId)
    {
        var request = new LaunchRequest(AgentPath, $"\"{AgentPath}\"", Path.GetDirectoryName(AgentPath), sessionId);
        var handle = _launcher.Start(request, out var pid);
        return new Win32AgentProcess(handle, pid);
    }

    private static string? ImagePath(SafeProcessHandle handle)
    {
        var size = 1024;
        var name = new StringBuilder(size);
        return LaunchNative.QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString() : null;
    }

    private sealed class Win32AgentProcess : IAgentProcess
    {
        private readonly SafeProcessHandle _handle;

        public Win32AgentProcess(SafeProcessHandle handle, int id)
        {
            _handle = handle;
            Id = id;
        }

        public int Id { get; }

        public int? ExitCode
        {
            get
            {
                if (LaunchNative.WaitForSingleObject(_handle, 0) != LaunchNative.WAIT_OBJECT_0) return null;
                return LaunchNative.GetExitCodeProcess(_handle, out var code) ? unchecked((int)code) : -1;
            }
        }

        public void Kill()
        {
            if (!LaunchNative.TerminateProcess(_handle, 1) && ExitCode == null)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"TerminateProcess({Id}) failed");
        }

        public void Dispose() => _handle.Dispose();
    }
}
