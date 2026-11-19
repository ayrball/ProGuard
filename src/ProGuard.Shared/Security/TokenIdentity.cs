using System.Runtime.InteropServices;

namespace ProGuard.Shared.Security;

/// <summary>Who a process runs as, as far as ProGuard's trust decisions need to know.</summary>
/// <param name="UserSid">The token's user, as an SDDL string such as S-1-5-18.</param>
/// <param name="Elevated">An elevated token, or one above medium integrity (an administrator's full token, SYSTEM, a service).</param>
public readonly record struct TokenIdentity(string UserSid, bool Elevated)
{
    public const string LocalSystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>This process's own token.</summary>
    public static TokenIdentity? Current()
    {
        if (!TokenNative.OpenProcessToken(TokenNative.GetCurrentProcess(), TokenNative.TOKEN_QUERY, out var token)) return null;
        try { return FromToken(token); }
        finally { TokenNative.CloseHandle(token); }
    }

    /// <summary>The token of process <paramref name="pid"/>, or null when this process may not read it (or it is gone).</summary>
    public static TokenIdentity? OfProcess(int pid)
    {
        if (pid <= 0) return null;
        var process = TokenNative.OpenProcess(TokenNative.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!TokenNative.OpenProcessToken(process, TokenNative.TOKEN_QUERY, out var token)) return null;
            try { return FromToken(token); }
            finally { TokenNative.CloseHandle(token); }
        }
        finally
        {
            TokenNative.CloseHandle(process);
        }
    }

    /// <summary>Reads a token opened with TOKEN_QUERY. Null when its user can't be read.</summary>
    public static TokenIdentity? FromToken(IntPtr token)
    {
        var user = ReadUserSid(token);
        if (user == null) return null;
        return new TokenIdentity(user, IsElevated(token));
    }

    private static string? ReadUserSid(IntPtr token)
    {
        TokenNative.GetTokenInformation(token, TokenNative.TokenUser, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!TokenNative.GetTokenInformation(token, TokenNative.TokenUser, buffer, needed, out _)) return null;
            // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
            return TokenNative.SidToString(Marshal.ReadIntPtr(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsElevated(IntPtr token)
    {
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            if (TokenNative.GetTokenInformation(token, TokenNative.TokenElevation, buffer, 4, out _) && Marshal.ReadInt32(buffer) != 0)
                return true;

            // TOKEN_MANDATORY_LABEL: SID_AND_ATTRIBUTES; the integrity level is the SID's last sub-authority.
            TokenNative.GetTokenInformation(token, TokenNative.TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return false;
            var label = Marshal.AllocHGlobal(needed);
            try
            {
                if (!TokenNative.GetTokenInformation(token, TokenNative.TokenIntegrityLevel, label, needed, out _)) return false;
                var sid = Marshal.ReadIntPtr(label);
                var count = Marshal.ReadByte(TokenNative.GetSidSubAuthorityCount(sid));
                if (count == 0) return false;
                var rid = (uint)Marshal.ReadInt32(TokenNative.GetSidSubAuthority(sid, (uint)(count - 1)));
                return rid >= TokenNative.SECURITY_MANDATORY_HIGH_RID;
            }
            finally
            {
                Marshal.FreeHGlobal(label);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>The few token and security calls ProGuard.Shared needs, by P/Invoke so the library stays platform-neutral to the analyzers.</summary>
internal static class TokenNative
{
    public const uint TOKEN_QUERY = 0x0008;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const int TokenUser = 1;
    public const int TokenElevation = 20;
    public const int TokenIntegrityLevel = 25;
    public const uint SECURITY_MANDATORY_HIGH_RID = 0x3000;
    public const int SE_KERNEL_OBJECT = 6;
    public const uint OWNER_SECURITY_INFORMATION = 0x1;

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("advapi32.dll")]
    public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    public static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll")]
    public static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
        out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    public static string? SidToString(IntPtr sid)
    {
        if (sid == IntPtr.Zero || !ConvertSidToStringSidW(sid, out var text)) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { LocalFree(text); }
    }

    /// <summary>The owner of the kernel object behind <paramref name="handle"/> (which needs READ_CONTROL), or null.</summary>
    public static string? OwnerOf(IntPtr handle)
    {
        if (GetSecurityInfo(handle, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor) != 0)
            return null;
        try { return SidToString(owner); }
        finally { LocalFree(descriptor); }
    }
}
