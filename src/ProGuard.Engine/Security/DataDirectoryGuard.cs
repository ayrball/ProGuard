using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ProGuard.Shared.Security;

namespace ProGuard.Engine.Security;

/// <summary>
/// Makes the engine's data directory (<c>%ProgramData%\ProGuard</c>: the database, the recovery files and
/// <c>logs\engine.log</c>) safe for a SYSTEM service to read and write. Runs in <c>Program.Main</c> before anything
/// opens a file there.
/// <para>
/// The threats: <c>%ProgramData%</c> lets any user create a folder, so a standard user who creates <c>ProGuard</c> first
/// owns it, can plant a <c>proguard.db</c> full of rules the engine then applies as SYSTEM, and can make <c>logs</c> a
/// junction so the engine's log writes land wherever they choose. And by inheritance from <c>%ProgramData%</c> the folder
/// was readable (and its files creatable) by every user, while <c>engine.log</c> records other users' command lines.
/// </para>
/// <list type="bullet">
/// <item>The folder itself, if it is a link (junction or symbolic link), a file, or owned by anyone but SYSTEM,
/// Administrators (or TrustedInstaller), is untrusted with everything in it: it is renamed
/// aside to <c>ProGuard.untrusted-&lt;time&gt;</c> (left for an administrator to look at) and a fresh one is created.
/// Renaming needs only rights on <c>%ProgramData%</c>, so the planter's own permissions on it don't matter.</item>
/// <item>Its DACL is then replaced with a protected one: SYSTEM and Administrators full control, inherited by
/// everything below, nothing for anyone else. The UI talks to the engine only over its pipes and never reads these
/// files, so users need nothing here.</item>
/// <item>Each entry inside (and inside <c>logs</c>) that is a link, a file with more than one hard link, or owned by an
/// untrusted account is moved into <c>untrusted-&lt;time&gt;</c> inside the folder; every other entry gets its own explicit
/// entries removed so it inherits the protected DACL.</item>
/// <item>Only when the engine runs as SYSTEM or an elevated administrator: a portable engine started without
/// administrator rights runs as the user and gains nothing from these files, can't set such a DACL, and leaves the folder
/// as it is (a warning says so).</item>
/// </list>
/// A database somewhere else (<c>PROGUARD_DB_PATH</c>, set by an administrator for a portable or test setup) is
/// checked, not changed: when the engine is elevated, its folder must be neither a link nor changeable by anyone but
/// administrators, or the engine refuses to start rather than load rules a standard user could have written.
/// </summary>
public sealed class DataDirectoryGuard
{
    /// <summary>Subfolders the engine writes to; created here so they inherit the protected DACL.</summary>
    public static readonly string[] Subdirectories = { "logs" };

    public sealed record Finding(LogLevel Level, string Message);

    private readonly List<Finding> _findings = new();
    private readonly string _stamp;

    public DataDirectoryGuard(bool elevated, DateTime? now = null)
    {
        Elevated = elevated;
        _stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>For the engine process: elevated when it runs as SYSTEM or with an administrator's full token.</summary>
    public static DataDirectoryGuard ForThisProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return new DataDirectoryGuard(elevated);
    }

    public bool Elevated { get; }

    /// <summary>What was found and done, to log once logging is up.</summary>
    public IReadOnlyList<Finding> Findings => _findings;

    /// <summary>Who counts as a trusted owner. Tests replace it; the default is <see cref="DirectoryTrust.IsTrustedOwner"/>.</summary>
    internal Func<SecurityIdentifier?, bool> IsTrustedOwner { get; init; } = DirectoryTrust.IsTrustedOwner;

    /// <summary>Whether to write DACLs. Tests that run unelevated turn it off where they can't.</summary>
    internal bool ApplyDacl { get; init; } = true;

    /// <summary>
    /// Prepares the engine's own data directory, <paramref name="directory"/>. Returns false when it can't be made safe;
    /// the engine must then not use it.
    /// </summary>
    public bool SecureOwnDirectory(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Elevated)
        {
            Note(LogLevel.Warning, $"The engine runs without administrator rights, so it left the permissions of {directory} as they are. Run it as the ProGuard Engine service, or elevated, to protect its rules and logs.");
            return true;
        }

        try
        {
            if (Exists(directory) && UntrustedRoot(directory) is { } why)
            {
                var aside = $"{directory}.untrusted-{_stamp}";
                Note(LogLevel.Error, $"{directory} can't be trusted ({why}). It was renamed to {aside} and will not be used; a new, protected one is created in its place. Delete the old one once you have looked at it.");
                if (File.Exists(directory)) File.Move(directory, aside);
                else Directory.Move(directory, aside);
            }

            if (!Directory.Exists(directory))
            {
                if (ApplyDacl) new DirectoryInfo(directory).Create(ProtectedSecurity());
                else Directory.CreateDirectory(directory);
            }
            else if (ApplyDacl)
            {
                new DirectoryInfo(directory).SetAccessControl(ProtectedSecurity());
            }

            CleanChildren(directory, directory, depth: 0);

            foreach (var name in Subdirectories)
            {
                var sub = Path.Combine(directory, name);
                if (!Directory.Exists(sub)) Directory.CreateDirectory(sub); // inherits the protected DACL
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            Note(LogLevel.Critical, $"Could not make {directory} safe to use: {ex.Message}. The engine won't use it.");
            return false;
        }
    }

    /// <summary>
    /// Checks a data directory the engine doesn't own (the folder of a <c>PROGUARD_DB_PATH</c> database) without changing
    /// it. False when the elevated engine must not use it.
    /// </summary>
    public bool CheckOtherDirectory(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Elevated) return true; // the engine has only the user's own rights, so nothing to protect
        if (!Exists(directory)) return true; // created by the engine itself, as an administrator, when the database opens

        try
        {
            if (UntrustedRoot(directory) is { } why)
            {
                Note(LogLevel.Critical, $"The database folder {directory} (from {Core.Data.ProGuardDbContext.DatabasePathVariable}) can't be trusted: {why}. The engine runs with administrator rights and won't load rules from it. Point {Core.Data.ProGuardDbContext.DatabasePathVariable} at a folder only administrators can change.");
                return false;
            }
            if (DirectoryTrust.WhoElseCanChange(new DirectoryInfo(directory).GetAccessControl()) is { } writable)
            {
                Note(LogLevel.Critical, $"The database folder {directory} (from {Core.Data.ProGuardDbContext.DatabasePathVariable}) isn't protected: {writable}. The engine runs with administrator rights and won't load rules from it. Point {Core.Data.ProGuardDbContext.DatabasePathVariable} at a folder only administrators can change.");
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            Note(LogLevel.Critical, $"Could not check the database folder {directory}: {ex.Message}. The engine won't use it.");
            return false;
        }
    }

    /// <summary>SYSTEM and Administrators full control, inherited by every file and folder below; nothing for anyone else.</summary>
    public static DirectorySecurity ProtectedSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    private string? UntrustedRoot(string path)
    {
        var attributes = File.GetAttributes(path); // of the link itself, not its target
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) return "it is a link to somewhere else";
        if (!attributes.HasFlag(FileAttributes.Directory)) return "it is a file, not a folder";
        var owner = OwnerOf(path);
        return IsTrustedOwner(owner) ? null : $"it is owned by {owner?.Value ?? "an account that can't be read"}, not by SYSTEM or an administrator";
    }

    private void CleanChildren(string root, string directory, int depth)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).ToList())
        {
            var name = Path.GetFileName(path);
            if (depth == 0 && name.StartsWith("untrusted-", StringComparison.OrdinalIgnoreCase) && Owned(path) && !IsLink(path))
                continue; // an earlier run's quarantine

            var attributes = File.GetAttributes(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            string? why = null;
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) why = "it is a link to somewhere else";
            else if (!Owned(path)) why = $"it is owned by {OwnerOf(path)?.Value ?? "an account that can't be read"}";
            else if (!isDirectory && HardLinkCount(path) > 1) why = "it has other names elsewhere on the disk (hard links)";

            if (why != null)
            {
                var quarantine = Path.Combine(root, $"untrusted-{_stamp}");
                Directory.CreateDirectory(quarantine);
                var target = Path.Combine(quarantine, Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '_'));
                Note(LogLevel.Error, $"{path} can't be trusted ({why}). It was moved to {target} and will not be used.");
                if (isDirectory) Directory.Move(path, target);
                else File.Move(path, target);
                continue;
            }

            if (ApplyDacl) InheritOnly(path, isDirectory);
            if (isDirectory && depth < 4) CleanChildren(root, path, depth + 1);
        }
    }

    /// <summary>Drops the entry's own ACEs and protection so it gets exactly what its folder hands down.</summary>
    private static void InheritOnly(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var changed = security.AreAccessRulesProtected;
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
            changed = true;
        }
        if (!changed) return;
        if (isDirectory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    private bool Owned(string path) => IsTrustedOwner(OwnerOf(path));

    /// <summary>The owner, or null when it can't be read (whoever denied us that is not to be trusted either).</summary>
    private static SecurityIdentifier? OwnerOf(string path)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            return null;
        }
    }

    private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path) || IsDanglingLink(path);

    private static bool IsDanglingLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception) { return false; }
    }

    private void Note(LogLevel level, string message) => _findings.Add(new Finding(level, message));

    // ---- Hard links ----

    private static uint HardLinkCount(string path)
    {
        using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return 1;
        return GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : 1;
    }

    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);
}
