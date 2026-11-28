using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StudyDesk.Desktop.Configuration;

/// <summary>Creates private Windows directories and rejects existing paths readable by unrelated users.</summary>
public static class PrivateDirectoryPolicy
{
    /// <summary>Rejects an existing private file with a foreign access grant or a redirected filesystem target.</summary>
    public static void ValidateExistingFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        FileInfo file = new(Path.GetFullPath(path));
        file.Refresh();
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Private files cannot be symbolic links or reparse points.");
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("A Windows user identity is required.");
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        FileSecurity security = file.GetAccessControl();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & SensitiveRights) != 0
                && rule.IdentityReference != user && rule.IdentityReference != system && rule.IdentityReference != administrators)
            {
                throw new InvalidOperationException("A private application file grants access to unrelated Windows users.");
            }
        }
    }

    private const FileSystemRights SensitiveRights = FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Ensures only the current Windows user, system and local administrators can access private files.</summary>
    public static void Ensure(string path)
    {
        string fullPath = Path.GetFullPath(path);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("A Windows user identity is required.");
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        DirectoryInfo directory = new(fullPath);
        if (!directory.Exists)
        {
            DirectorySecurity security = new();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            foreach (SecurityIdentifier allowed in new[] { user, system, administrators })
            {
                security.AddAccessRule(new FileSystemAccessRule(allowed, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }

            directory.Create(security);
        }

        directory.Refresh();
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Private directories cannot be symbolic links or junctions.");
        }

        DirectorySecurity existing = directory.GetAccessControl();
        foreach (FileSystemAccessRule rule in existing.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & SensitiveRights) != 0 && rule.IdentityReference != user && rule.IdentityReference != system && rule.IdentityReference != administrators)
            {
                throw new InvalidOperationException("Choose a private data directory without permissions for unrelated Windows users.");
            }
        }
    }
}
