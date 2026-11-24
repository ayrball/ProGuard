using Microsoft.Win32.SafeHandles;

namespace ProGuard.Shared.Security;

/// <summary>The process at the other end of a pipe isn't one ProGuard trusts to be its engine. Nothing was sent to it.</summary>
public sealed class UntrustedPipeServerException : Exception
{
    public UntrustedPipeServerException(string message) : base(message) { }
}

/// <summary>
/// Checks, right after connecting and before sending anything, that a pipe really belongs to the ProGuard engine and not
/// to someone who created a pipe of that name while the engine was down (a squatter would capture the caller's
/// impersonation token, and an elevated UI's is worth having).
/// <para>
/// Policy: the server must be at least as privileged as the client. That holds when
/// </para>
/// <list type="bullet">
/// <item>the pipe's owner is LocalSystem or Administrators (what an engine running as a service, or elevated, creates;
/// a standard user can't make either the owner of anything), or the client's own account while the client isn't
/// elevated (a portable engine, run.ps1 or the tests running as the same user, which gain nothing from the token), and</item>
/// <item>when the client may open the server process at all (a standard user can't open a service), its token is
/// LocalSystem, elevated, or the client's own unelevated account by the same rule.</item>
/// <item>A server running as the client's own account must also run at no lower an integrity level than the client, and
/// its pipe must carry no lower a mandatory label: a sandboxed, low-integrity program of the same user (a browser's
/// renderer, say) can create the pipe too, and must not be handed a medium-integrity token. Pipes owned by SYSTEM or
/// Administrators are exempt: nothing below high integrity can create one, and the engine's own carries no label (which
/// Windows reads as Medium) even when the client is elevated.</item>
/// </list>
/// The owner check works for every client; the process check adds a second opinion where Windows allows it.
/// </summary>
public static class PipeServerCheck
{
    /// <summary>Throws <see cref="UntrustedPipeServerException"/> unless the server behind <paramref name="pipe"/> is trusted.</summary>
    public static void Verify(SafePipeHandle pipe)
    {
        var handle = pipe.DangerousGetHandle();
        var owner = TokenNative.OwnerOf(handle);
        var label = TokenNative.MandatoryLabelRidOf(handle);
        var client = TokenIdentity.Current();
        TokenIdentity? server = null;
        var serverIsSelf = false;
        if (TokenNative.GetNamedPipeServerProcessId(handle, out var pid))
        {
            serverIsSelf = pid == (uint)Environment.ProcessId;
            server = TokenIdentity.OfProcess((int)pid);
        }

        if (serverIsSelf) return; // a server in this very process: tests, and nothing to steal from ourselves
        if (Problem(owner, server, client, label) is { } problem) throw new UntrustedPipeServerException(problem);
    }

    /// <summary>
    /// Null when the server may be trusted, otherwise why not. Pure, for tests. <paramref name="pipeLabelRid"/> is the
    /// pipe's mandatory label (Medium when it has none), null when it couldn't be read.
    /// </summary>
    internal static string? Problem(string? pipeOwner, TokenIdentity? server, TokenIdentity? client, uint? pipeLabelRid = TokenIdentity.MediumRid)
    {
        if (client is not { } me) return "this process's own account can't be read";
        if (pipeOwner == null) return "the pipe's owner can't be read";
        if (!TrustedOwner(pipeOwner, me))
            return $"the pipe is owned by {pipeOwner}, not by SYSTEM or Administrators: another program may have taken the engine's pipe name while the engine wasn't running";
        if (server is { } s && !TrustedServer(s, me))
            return $"the process serving the pipe runs as {s.UserSid}, not as SYSTEM or an administrator";

        if (!IsAdministrative(pipeOwner))
        {
            // The client's own account: that is only as trustworthy as the integrity level it runs at.
            if (me.IntegrityRid == TokenIdentity.UnknownRid) return "this process's own integrity level can't be read";
            if (pipeLabelRid is not { } label || label < me.IntegrityRid)
                return $"the pipe was created at a lower integrity level ({Describe(pipeLabelRid)}) than this program's ({Describe(me.IntegrityRid)}): a sandboxed program may have taken the engine's pipe name";
            if (server is { } same && Same(same.UserSid, me.UserSid) && same.IntegrityRid < me.IntegrityRid)
                return $"the process serving the pipe runs at a lower integrity level ({Describe(same.IntegrityRid)}) than this program's ({Describe(me.IntegrityRid)})";
        }
        return null;
    }

    private static bool IsAdministrative(string owner) => Same(owner, TokenIdentity.LocalSystemSid) || Same(owner, TokenIdentity.AdministratorsSid);

    private static bool TrustedOwner(string owner, TokenIdentity client) =>
        IsAdministrative(owner) || (!client.Elevated && Same(owner, client.UserSid));

    private static bool TrustedServer(TokenIdentity server, TokenIdentity client) =>
        Same(server.UserSid, TokenIdentity.LocalSystemSid) || server.Elevated
        || (!client.Elevated && Same(server.UserSid, client.UserSid));

    private static string Describe(uint? rid) => rid switch
    {
        null => "unknown",
        TokenIdentity.UntrustedRid => "untrusted",
        < TokenIdentity.MediumRid => "low",
        < TokenIdentity.HighRid => "medium",
        < TokenIdentity.SystemRid => "high",
        _ => "system",
    };

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
