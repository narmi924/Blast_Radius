using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Blast;

internal sealed record FileSecurityState(string Version, string OwnerSid, string AccessSddl,
    bool DaclProtected);

internal static class FileMetadata
{
    internal static Action? BeforeSecurityReadForTest { get; set; }
    internal const string Version = "owner-dacl-attributes-v1";
    private const FileAttributes AllowedFlags = FileAttributes.Archive | FileAttributes.Hidden;

    internal static void CheckAttributes(FileAttributes value)
    {
        if (value != FileAttributes.Normal &&
            (value == 0 || (value & FileAttributes.Normal) != 0 ||
             (value & ~AllowedFlags) != 0))
            throw new NotSupportedException("Unsupported file attribute combination: " + value);
    }

    internal static FileSecurityState Read(SafeFileHandle handle)
    {
        using var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(borrowed, FileAccess.Read);
        return Read(stream);
    }

    internal static FileSecurityState Read(FileStream stream)
    {
        BeforeSecurityReadForTest?.Invoke();
        var security = stream.GetAccessControl();
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidDataException("File owner SID is missing.");
        string sddl = security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Owner | AccessControlSections.Access);
        var raw = new RawSecurityDescriptor(sddl);
        if (raw.DiscretionaryAcl is null)
            throw new NotSupportedException("A null DACL is not supported.");
        foreach (GenericAce ace in raw.DiscretionaryAcl)
            if (ace.AceType is not (AceType.AccessAllowed or AceType.AccessDenied))
                throw new NotSupportedException("A nonstandard DACL entry is unsupported.");
        return new(Version, owner.Value, sddl,
            raw.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
    }

    internal static FileStream CreateWithSecurity(string path, FileSecurityState expected)
    {
        RequireVersion(expected);
        if (expected.OwnerSid != WindowsIdentity.GetCurrent().User?.Value)
            throw new NotSupportedException("Restoring another owner's file is unsupported.");
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(expected.AccessSddl,
            AccessControlSections.Owner | AccessControlSections.Access);
        if (security.AreAccessRulesProtected != expected.DaclProtected)
            throw new InvalidDataException("Saved DACL protection state differs from saved descriptor.");
        return new FileInfo(path).Create(FileMode.CreateNew,
            FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.ReadPermissions,
            FileShare.None, 4096, FileOptions.WriteThrough, security);
    }

    internal static void RequireCreatableSecurity(FileSecurityState expected)
    {
        RequireVersion(expected);
        var raw = new RawSecurityDescriptor(expected.AccessSddl);
        if (expected.DaclProtected &&
            raw.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited))
            throw new NotSupportedException(
                "Protected auto-inherited DACL cannot be recreated with exact metadata.");
    }

    internal static void RequireVersion(FileSecurityState expected)
    {
        if (expected.Version != Version)
            throw new InvalidDataException("Unsupported file security metadata version.");
    }
}
