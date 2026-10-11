using System;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using ProGuard.Core.Native;

namespace ProGuard.Engine.IPC;

/// <summary>Who sent an IPC request, as far as <see cref="IpcAuthorization"/> needs to know.</summary>
public interface IIpcCaller
{
    /// <summary>Account name, for logs and error replies.</summary>
    string Name { get; }

    /// <summary>True for SYSTEM and for an elevated administrator; false for a UAC-filtered administrator token.</summary>
    bool IsAdministrator { get; }

    /// <summary>Whether the caller's own token could open process <paramref name="pid"/> with <paramref name="access"/>.</summary>
    bool CanOpenProcess(int pid, ProcessAccessFlags access);
}

public static class IpcCaller
{
    /// <summary>Code running inside the engine, which already has the engine's rights.</summary>
    public static IIpcCaller InProcess { get; } = new InProcessCaller();

    private sealed class InProcessCaller : IIpcCaller
    {
        public string Name => "engine";
        public bool IsAdministrator => true;
        public bool CanOpenProcess(int pid, ProcessAccessFlags access) => true;
    }
}

/// <summary>
/// The account at the other end of a command-pipe connection. Every check impersonates the client, so Windows applies
/// the client's own token, including its integrity level and the absence of SeDebugPrivilege, rather than the
/// engine's SYSTEM token.
/// </summary>
internal sealed class PipeCaller : IIpcCaller
{
    private readonly NamedPipeServerStream _pipe;

    private PipeCaller(NamedPipeServerStream pipe, string name, bool isAdministrator)
    {
        _pipe = pipe;
        Name = name;
        IsAdministrator = isAdministrator;
    }

    public string Name { get; }
    public bool IsAdministrator { get; }

    /// <summary>
    /// Reads the client's identity from its token. Windows lets a server impersonate only after it has read from the
    /// pipe, so call this after the first request arrives. Returns null when the client can't be identified (for
    /// example, it connected anonymously).
    /// </summary>
    public static PipeCaller? Identify(NamedPipeServerStream pipe)
    {
        string? name = null;
        var isAdministrator = false;
        try
        {
            RunAsClient(pipe, () =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                name = identity.Name;
                isAdministrator = identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (Exception)
        {
            return null;
        }
        return name == null ? null : new PipeCaller(pipe, name, isAdministrator);
    }

    public bool CanOpenProcess(int pid, ProcessAccessFlags access)
    {
        var allowed = false;
        try
        {
            RunAsClient(_pipe, () =>
            {
                var handle = Kernel32.OpenProcess(access, false, pid);
                if (handle == IntPtr.Zero) return;
                allowed = true;
                Kernel32.CloseHandle(handle);
            });
        }
        catch (Exception)
        {
            return false;
        }
        return allowed;
    }

    /// <summary>
    /// Runs <paramref name="action"/> as the client. <see cref="NamedPipeServerStream.RunAsClient"/> reverts the thread's
    /// token afterwards but leaves the client's identity in the current ExecutionContext, so every await continuation,
    /// task and timer later started from this flow would run as the client. On the installed service that made "Purge
    /// now" fail to open SYSTEM's own token, and SmartTrim timers set up over IPC ran as the user (purges failed, their
    /// log lines were denied). Running it on a thread pool thread without flowing the context confines that to a
    /// context that is thrown away.
    /// </summary>
    private static void RunAsClient(NamedPipeServerStream pipe, Action action)
    {
        Task task;
        using (ExecutionContext.SuppressFlow())
            task = Task.Run(() => pipe.RunAsClient(() => action()));
        task.GetAwaiter().GetResult();
    }
}
