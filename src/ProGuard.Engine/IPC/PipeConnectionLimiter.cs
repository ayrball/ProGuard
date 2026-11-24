using System;
using System.Collections.Generic;
using System.IO.Pipes;
using ProGuard.Core.Native;
using ProGuard.Shared.Security;

namespace ProGuard.Engine.IPC;

/// <summary>
/// Bookkeeping shared by a pipe server's connections: how many are open (none means the next instance must be created
/// with <see cref="PipeOptions.FirstPipeInstance"/>), how many each account holds, so one user can't take every
/// instance and lock everyone else's UI out, and how many standard (unelevated) clients hold between them, so a few
/// standard accounts together can't either: the instances above <see cref="MaxStandardTotal"/> are kept for
/// administrators and LocalSystem.
/// </summary>
internal sealed class PipeConnectionLimiter
{
    /// <summary>The bucket for clients whose account can't be read. They share one allowance.</summary>
    internal const string UnknownAccount = "unknown";

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _perAccount = new(StringComparer.OrdinalIgnoreCase);
    private int _open;
    private int _standard;

    public PipeConnectionLimiter(int maxPerAccount, int maxStandardTotal = int.MaxValue)
    {
        MaxPerAccount = maxPerAccount;
        MaxStandardTotal = maxStandardTotal;
    }

    public int MaxPerAccount { get; }

    /// <summary>Connections all standard clients together may hold; the rest are reserved for privileged ones.</summary>
    public int MaxStandardTotal { get; }

    /// <summary>Connected instances, admitted or not yet decided.</summary>
    public int Open { get { lock (_lock) return _open; } }

    /// <summary>Admitted connections from standard clients.</summary>
    public int StandardHeld { get { lock (_lock) return _standard; } }

    /// <summary>Call as soon as an instance is connected, before anything else; pair with <see cref="Closed"/>.</summary>
    public void Connected() { lock (_lock) _open++; }

    /// <summary>Call once the connected instance has been disposed.</summary>
    public void Closed() { lock (_lock) _open--; }

    /// <summary>
    /// Counts a connection against <paramref name="account"/> (and, unless <paramref name="privileged"/>, against the
    /// standard clients' share); false, with nothing counted, when either is used up.
    /// </summary>
    public bool TryAdmit(string account, bool privileged = false)
    {
        lock (_lock)
        {
            _perAccount.TryGetValue(account, out var held);
            if (held >= MaxPerAccount) return false;
            if (!privileged && _standard >= MaxStandardTotal) return false;
            _perAccount[account] = held + 1;
            if (!privileged) _standard++;
            return true;
        }
    }

    /// <summary>Gives back a connection <see cref="TryAdmit"/> counted, with the same <paramref name="privileged"/>.</summary>
    public void Release(string account, bool privileged = false)
    {
        lock (_lock)
        {
            if (!_perAccount.TryGetValue(account, out var held)) return;
            if (held <= 1) _perAccount.Remove(account);
            else _perAccount[account] = held - 1;
            if (!privileged && _standard > 0) _standard--;
        }
    }

    public int HeldBy(string account) { lock (_lock) return _perAccount.GetValueOrDefault(account); }

    /// <summary>
    /// The account of the process at the other end of <paramref name="pipe"/>, read from that process's token (the engine
    /// may open any process). Unlike impersonation it works before the client has written anything, so a client that
    /// connects and stays silent is counted too.
    /// </summary>
    public static string AccountOf(NamedPipeServerStream pipe) => ClientOf(pipe)?.UserSid ?? UnknownAccount;

    /// <summary>
    /// Whether the client is LocalSystem or runs elevated, read the same way as <see cref="AccountOf"/>. False when it
    /// can't be read, so an unknown client never takes a reserved instance.
    /// </summary>
    public static bool IsPrivileged(NamedPipeServerStream pipe) =>
        ClientOf(pipe) is { } identity && (identity.Elevated || string.Equals(identity.UserSid, TokenIdentity.LocalSystemSid, StringComparison.OrdinalIgnoreCase));

    private static TokenIdentity? ClientOf(NamedPipeServerStream pipe)
    {
        try
        {
            if (Kernel32.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
                return TokenIdentity.OfProcess((int)pid);
        }
        catch (Exception)
        {
            // Disposed or broken: counted as unknown, and the read that follows fails anyway.
        }
        return null;
    }
}
