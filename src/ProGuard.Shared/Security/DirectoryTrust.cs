using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ProGuard.Shared.Security;

/// <summary>
/// Whether a file or folder is one only administrators can change. ProGuard's engine runs as SYSTEM and reads its
/// rules from disk, and the UI's sign-in task starts ProGuard with the user's highest rights; both must only trust files
/// a standard user can't have planted or replaced.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DirectoryTrust
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    /// <summary>Rights that let someone change a file or a folder's contents, or its permissions.</summary>
    private const FileSystemRights ChangeRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>
    /// SYSTEM, Administrators or TrustedInstaller. Not an administrator's own account: the owner keeps the right to change
    /// permissions even from that account's unelevated programs, which is exactly what must not reach a file that SYSTEM
    /// trusts or that starts elevated.
    /// </summary>
    public static bool IsTrustedOwner(SecurityIdentifier? owner) => owner != null && IsAdministrativeSid(owner);

    private static bool IsAdministrativeSid(SecurityIdentifier sid) =>
        sid.Equals(LocalSystem) || sid.Equals(Administrators) || sid.Equals(TrustedInstaller);

    /// <summary>
    /// Null when only SYSTEM, Administrators and TrustedInstaller (and a trusted owner) can change <paramref name="security"/>'s
    /// object, otherwise who else can. Deny entries are ignored: an allow to a broad group is reason enough to distrust.
    /// </summary>
    public static string? WhoElseCanChange(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!IsTrustedOwner(owner)) return $"it is owned by {Describe(owner)}";

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue; // reaches only children, checked on their own
            if ((rule.FileSystemRights & ChangeRights) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid) continue;
            if (IsAdministrativeSid(sid)) continue;
            // CREATOR OWNER only ever reaches new children, whose owner is whoever creates them: that is someone with
            // create rights, which are checked by their own entries.
            if (sid.Equals(CreatorOwner)) continue;
            return $"{Describe(sid)} may change it";
        }
        return null;
    }

    /// <summary>
    /// Null when no one but administrators can replace <paramref name="filePath"/>: neither the file, nor its folder (which
    /// could have the file swapped out), nor the folder above that (which could have the folder renamed away and
    /// replaced). Otherwise why not, worded for a log line.
    /// </summary>
    public static string? WhyReplaceable(string filePath) => WhyReplaceable(filePath, allowMissingFile: false);

    /// <summary>
    /// <see cref="WhyReplaceable(string)"/>, for a file that may not have been created yet (a database the engine is about
    /// to create): then the file itself is skipped, and so are folders that don't exist yet, so the check covers the
    /// first two that do, which are what decide who could put something there first.
    /// </summary>
    public static string? WhyReplaceable(string filePath, bool allowMissingFile)
    {
        try
        {
            var full = Path.GetFullPath(filePath);
            var file = new FileInfo(full);
            var fileExists = file.Exists || IsLink(file);
            if (fileExists || !allowMissingFile)
            {
                if (IsLink(file)) return $"{full} is a link to somewhere else";
                if (WhoElseCanChange(file.GetAccessControl()) is { } fileProblem) return $"{full}: {fileProblem}";
            }

            var directory = file.Directory;
            for (var level = 0; level < 2 && directory != null; directory = directory.Parent)
            {
                if (allowMissingFile && !directory.Exists && !IsLink(directory)) continue;
                if (IsLink(directory)) return $"{directory.FullName} is a link to somewhere else";
                if (WhoElseCanChange(directory.GetAccessControl()) is { } problem) return $"{directory.FullName}: {problem}";
                level++;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            return $"{filePath}: its permissions can't be read ({ex.Message})";
        }
    }

    /// <summary>A junction or symbolic link itself (not its target); false for a path that doesn't exist.</summary>
    private static bool IsLink(FileSystemInfo entry)
    {
        var attributes = entry.Attributes; // -1 when nothing is there
        return attributes != (FileAttributes)(-1) && attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid == null) return "an unknown account";
        try { return $"{sid.Translate(typeof(NTAccount)).Value} ({sid.Value})"; }
        catch (Exception) { return sid.Value; }
    }
}
