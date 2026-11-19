using System;
using System.Collections.Generic;
using System.IO.Pipes;
using ProGuard.Core.Native;
using ProGuard.Shared.Security;

namespace ProGuard.Engine.IPC;

/// <summary>
/// Bookkeeping shared by a pipe server's connections: how many are open (none means the next instance must be created
/// with <see cref="PipeOptions.FirstPipeInstance"/>), and how many each account holds, so one user can't take every
/// instance and lock everyone else's UI out.
/// </summary>
internal sealed class PipeConnectionLimiter
{
    /// <summary>The bucket for clients whose account can't be read. They share one allowance.</summary>
    internal const string UnknownAccount = "unknown";

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _perAccount = new(StringComparer.OrdinalIgnoreCase);
    private int _open;

    public PipeConnectionLimiter(int maxPerAccount) => MaxPerAccount = maxPerAccount;

    public int MaxPerAccount { get; }

    /// <summary>Connected instances, admitted or not yet decided.</summary>
    public int Open { get { lock (_lock) return _open; } }

    /// <summary>Call as soon as an instance is connected, before anything else; pair with <see cref="Closed"/>.</summary>
    public void Connected() { lock (_lock) _open++; }

    /// <summary>Call once the connected instance has been disposed.</summary>
    public void Closed() { lock (_lock) _open--; }

    /// <summary>Counts a connection against <paramref name="account"/>; false (and nothing counted) when it already holds its share.</summary>
    public bool TryAdmit(string account)
    {
        lock (_lock)
        {
            _perAccount.TryGetValue(account, out var held);
            if (held >= MaxPerAccount) return false;
            _perAccount[account] = held + 1;
            return true;
        }
    }

    /// <summary>Gives back a connection <see cref="TryAdmit"/> counted.</summary>
    public void Release(string account)
    {
        lock (_lock)
        {
            if (!_perAccount.TryGetValue(account, out var held)) return;
            if (held <= 1) _perAccount.Remove(account);
            else _perAccount[account] = held - 1;
        }
    }

    public int HeldBy(string account) { lock (_lock) return _perAccount.GetValueOrDefault(account); }

    /// <summary>
    /// The account of the process at the other end of <paramref name="pipe"/>, read from that process's token (the engine
    /// may open any process). Unlike impersonation it works before the client has written anything, so a client that
    /// connects and stays silent is counted too.
    /// </summary>
    public static string AccountOf(NamedPipeServerStream pipe)
    {
        try
        {
            if (Kernel32.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)
                && TokenIdentity.OfProcess((int)pid) is { } identity)
                return identity.UserSid;
        }
        catch (Exception)
        {
            // Disposed or broken: counted as unknown, and the read that follows fails anyway.
        }
        return UnknownAccount;
    }
}
