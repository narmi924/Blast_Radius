using System.Security.AccessControl;
using System.Security.Principal;

namespace Blast;

// This protects against accidental inheritance to other users, not a same-user agent.
internal static class StorageAccess
{
    private static readonly SecurityIdentifier User = WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    internal static void EnsurePrivateDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetOwner(User);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { User, SystemSid, AdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.CreateDirectory(security, path);
        }
        VerifyDirectory(path);
    }

    internal static void VerifyDirectory(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new NotSupportedException("Redirected state directory is unsupported.");
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        Verify(security, requireProtected: true);
    }

    internal static void VerifyFile(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new NotSupportedException("Redirected state file is unsupported.");
        var security = new FileInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        Verify(security, requireProtected: false);
    }

    private static void Verify(FileSystemSecurity security, bool requireProtected)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !owner.Equals(User) || requireProtected && !security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("State storage owner or DACL protection is not private.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        bool userAllowed = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid.Equals(User)) userAllowed = true;
            else if (!sid.Equals(SystemSid) && !sid.Equals(AdministratorsSid))
                throw new UnauthorizedAccessException("State storage grants access to another principal.");
        }
        if (!userAllowed) throw new UnauthorizedAccessException("State storage lacks current-user access.");
    }
}
