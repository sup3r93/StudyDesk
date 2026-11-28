using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StudyDesk.Server.Core.Security;

/// <summary>Creates private directories and non-overwriting secret files using OS permissions.</summary>
public static class PrivateFile
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates an application directory privately, or validates existing OS permissions.</summary>
    /// <param name="path">Fully qualified, application-specific directory.</param>
    /// <exception cref="ArgumentException">The location is not an absolute application directory.</exception>
    /// <exception cref="UnauthorizedAccessException">An existing directory is not private or traverses a link.</exception>
    public static void EnsureDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || fullPath == Path.GetPathRoot(fullPath))
        {
            throw new ArgumentException("Select an absolute application data directory, not a filesystem root.", nameof(path));
        }

        RejectLinkedDirectories(fullPath);
        if (OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(fullPath))
            {
                new DirectoryInfo(fullPath).Create(CreateDirectorySecurity());
            }

            ValidateDirectorySecurity(fullPath);
        }
        else
        {
            Directory.CreateDirectory(fullPath, DirectoryMode);
            if ((File.GetUnixFileMode(fullPath) & ~DirectoryMode) != 0)
            {
                throw new UnauthorizedAccessException("The application data directory must be private to its OS user.");
            }
        }
    }

    /// <summary>Validates an existing regular private file without changing its data or permissions.</summary>
    /// <param name="path">Absolute application-owned file path; missing files are allowed.</param>
    public static void ValidateExistingFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A private file path must be absolute.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        RejectLinkedDirectories(Path.GetDirectoryName(fullPath)!);
        FileInfo file = new(fullPath);
        if (file.LinkTarget is not null)
        {
            throw new UnauthorizedAccessException("Private data files cannot be symbolic links.");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new UnauthorizedAccessException("Private storage requires regular files without reparse points.");
        }

        if (OperatingSystem.IsWindows())
        {
            ValidateAccessRules(file.GetAccessControl());
        }
        else if ((File.GetUnixFileMode(fullPath) & ~FileModeBits) != 0)
        {
            throw new UnauthorizedAccessException("Private data files must not grant group or other-user access.");
        }
    }

    /// <summary>Checks a SQLite database and both possible sidecars before any SQLite open.</summary>
    public static void ValidateSqliteDatabaseFiles(string dataDirectory, string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        if (Path.GetFileName(databaseName) != databaseName || databaseName is "." or "..")
        {
            throw new ArgumentException("A database name must be a single filename.", nameof(databaseName));
        }

        EnsureDirectory(dataDirectory);
        string path = Path.Combine(dataDirectory, databaseName);
        ValidateExistingFile(path);
        ValidateExistingFile(path + "-wal");
        ValidateExistingFile(path + "-shm");
    }

    /// <summary>Validates all existing SQLite files and creates only a missing database with private OS permissions.</summary>
    public static void PrepareSqliteDatabase(string dataDirectory, string databaseName)
    {
        ValidateSqliteDatabaseFiles(dataDirectory, databaseName);
        string path = Path.Combine(dataDirectory, databaseName);
        if (!File.Exists(path))
        {
            try
            {
                using FileStream stream = OperatingSystem.IsWindows()
                    ? new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                        FileShare.None, 4096, FileOptions.WriteThrough, CreateFileSecurity())
                    : new FileStream(path, new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        UnixCreateMode = FileModeBits,
                    });
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(path))
            {
                // A concurrent trusted provisioner may have created it; existing files are never replaced.
                ValidateExistingFile(path);
            }
        }

        ValidateSqliteDatabaseFiles(dataDirectory, databaseName);
    }

    /// <summary>Writes a new private file durably without replacing an existing destination.</summary>
    /// <param name="path">Absolute output path selected by the local operator.</param>
    /// <param name="content">Secret content, which is never sent to logs.</param>
    /// <param name="cancellationToken">Cancels the write before completion.</param>
    public static async Task WriteNewAsync(string path, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The private output path must be absolute.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(fullPath)!;
        EnsureDirectory(parent);
        bool created = false;
        try
        {
            using FileStream stream = OperatingSystem.IsWindows()
                ? new FileInfo(fullPath).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                    FileShare.None, 4096, FileOptions.WriteThrough, CreateFileSecurity())
                : new FileStream(fullPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough,
                    UnixCreateMode = FileModeBits,
                });
            created = true;
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            if (created)
            {
                File.Delete(fullPath);
            }

            throw;
        }
    }

    /// <summary>Opens a private persistent lease file exclusively for the lifetime of its owner.</summary>
    /// <param name="path">Absolute application-owned lease path.</param>
    /// <returns>An exclusive handle that releases the lease when disposed.</returns>
    public static FileStream OpenExclusiveLease(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The lease path must be absolute.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        EnsureDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            return OperatingSystem.IsWindows()
                ? new FileInfo(fullPath).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                    FileShare.None, 4096, FileOptions.WriteThrough, CreateFileSecurity())
                : new FileStream(fullPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    UnixCreateMode = FileModeBits,
                });
        }
        catch (IOException) when (File.Exists(fullPath))
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("A lease file cannot be a symbolic link.");
            }

            if (OperatingSystem.IsWindows())
            {
                ValidateAccessRules(new FileInfo(fullPath).GetAccessControl());
            }
            else if ((File.GetUnixFileMode(fullPath) & ~FileModeBits) != 0)
            {
                throw new UnauthorizedAccessException("The lease file must be private to its OS user.");
            }

            return new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    /// <summary>Rejects existing symbolic links and junctions in the selected directory chain.</summary>
    private static void RejectLinkedDirectories(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null || current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Private storage cannot traverse symbolic links or junctions.");
            }
        }
    }

    /// <summary>Refuses existing Windows ACL grants outside the operator and trusted OS administrators.</summary>
    [SupportedOSPlatform("windows")]
    private static void ValidateDirectorySecurity(string path)
    {
        ValidateAccessRules(new DirectoryInfo(path).GetAccessControl());
    }

    /// <summary>Allows grants only to the operator and privileged operating-system administrators.</summary>
    [SupportedOSPlatform("windows")]
    private static void ValidateAccessRules(FileSystemSecurity security)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new UnauthorizedAccessException("A Windows user identity is required.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            SecurityIdentifier principal = (SecurityIdentifier)rule.IdentityReference;
            bool trusted = principal == user || principal.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || principal.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
            if (rule.AccessControlType == AccessControlType.Allow && !trusted)
            {
                throw new UnauthorizedAccessException("The private storage item permits another Windows principal.");
            }
        }
    }

    /// <summary>Builds a protected file ACL containing only the current Windows identity.</summary>
    [SupportedOSPlatform("windows")]
    private static FileSecurity CreateFileSecurity()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new UnauthorizedAccessException("A Windows user identity is required.");
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>Builds a private inheritable directory ACL for new application files.</summary>
    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreateDirectorySecurity()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new UnauthorizedAccessException("A Windows user identity is required.");
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
