using System.Runtime.InteropServices;

namespace ProGuard.Shared.Security;

/// <summary>Who a process runs as, as far as ProGuard's trust decisions need to know.</summary>
/// <param name="UserSid">The token's user, as an SDDL string such as S-1-5-18.</param>
/// <param name="Elevated">An elevated token, or one above medium integrity (an administrator's full token, SYSTEM, a service).</param>
/// <param name="IntegrityRid">
/// The token's mandatory integrity level, as the RID of its label (<see cref="LowRid"/>, <see cref="MediumRid"/>,
/// <see cref="HighRid"/>, <see cref="SystemRid"/>), or <see cref="UnknownRid"/> when it couldn't be read. Medium unless
/// given, which is what an ordinary user's program runs at.
/// </param>
public readonly record struct TokenIdentity(string UserSid, bool Elevated, uint IntegrityRid = TokenIdentity.MediumRid)
{
    public const string LocalSystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>Couldn't be read: below every real level, so a check that needs "at least" fails.</summary>
    public const uint UnknownRid = 0;
    public const uint UntrustedRid = 0x0000;
    public const uint LowRid = 0x1000;
    public const uint MediumRid = 0x2000;
    public const uint HighRid = 0x3000;
    public const uint SystemRid = 0x4000;

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
        var integrity = ReadIntegrityRid(token);
        return new TokenIdentity(user, IsElevated(token, integrity), integrity ?? UnknownRid);
    }

    /// <summary>The RID of the last sub-authority of a mandatory label SID: its integrity level.</summary>
    internal static uint? IntegrityRidOf(IntPtr labelSid)
    {
        if (labelSid == IntPtr.Zero) return null;
        var count = Marshal.ReadByte(TokenNative.GetSidSubAuthorityCount(labelSid));
        if (count == 0) return null;
        return (uint)Marshal.ReadInt32(TokenNative.GetSidSubAuthority(labelSid, (uint)(count - 1)));
    }

    private static uint? ReadIntegrityRid(IntPtr token)
    {
        // TOKEN_MANDATORY_LABEL: SID_AND_ATTRIBUTES; the integrity level is the SID's last sub-authority.
        TokenNative.GetTokenInformation(token, TokenNative.TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) return null;
        var label = Marshal.AllocHGlobal(needed);
        try
        {
            if (!TokenNative.GetTokenInformation(token, TokenNative.TokenIntegrityLevel, label, needed, out _)) return null;
            return IntegrityRidOf(Marshal.ReadIntPtr(label));
        }
        finally
        {
            Marshal.FreeHGlobal(label);
        }
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

    private static bool IsElevated(IntPtr token, uint? integrity)
    {
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            if (TokenNative.GetTokenInformation(token, TokenNative.TokenElevation, buffer, 4, out _) && Marshal.ReadInt32(buffer) != 0)
                return true;
            return integrity >= TokenNative.SECURITY_MANDATORY_HIGH_RID;
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

    public const uint LABEL_SECURITY_INFORMATION = 0x10;
    private const byte SYSTEM_MANDATORY_LABEL_ACE_TYPE = 0x11;

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo")]
    private static extern uint GetSecurityInfoSacl(IntPtr handle, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

    /// <summary>
    /// The integrity level of the kernel object behind <paramref name="handle"/> (which needs READ_CONTROL): the RID of
    /// its mandatory label, or Medium when it has none (Windows treats an unlabeled object as Medium). Null when the
    /// label can't be read.
    /// </summary>
    public static uint? MandatoryLabelRidOf(IntPtr handle)
    {
        if (GetSecurityInfoSacl(handle, SE_KERNEL_OBJECT, LABEL_SECURITY_INFORMATION, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                out var sacl, out var descriptor) != 0)
            return null;
        try { return LabelRidOf(sacl); }
        finally { LocalFree(descriptor); }
    }

    /// <summary>The label in an ACL holding a SYSTEM_MANDATORY_LABEL_ACE, Medium if it holds none. Separate for tests.</summary>
    internal static uint? LabelRidOf(IntPtr acl)
    {
        if (acl == IntPtr.Zero) return TokenIdentity.MediumRid;
        // ACL: revision, Sbz1 (bytes), AclSize, AceCount, Sbz2 (ushorts); then each ACE starts with ACE_HEADER: type,
        // flags (bytes), size (ushort). A mandatory label ACE then has its mask (uint) and the label SID.
        var size = (ushort)Marshal.ReadInt16(acl, 2);
        var count = (ushort)Marshal.ReadInt16(acl, 4);
        var offset = 8;
        for (var i = 0; i < count && offset + 4 <= size; i++)
        {
            var type = Marshal.ReadByte(acl, offset);
            var aceSize = (ushort)Marshal.ReadInt16(acl, offset + 2);
            if (aceSize < 4) return null; // malformed
            if (type == SYSTEM_MANDATORY_LABEL_ACE_TYPE) return TokenIdentity.IntegrityRidOf(acl + offset + 8);
            offset += aceSize;
        }
        return TokenIdentity.MediumRid;
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
