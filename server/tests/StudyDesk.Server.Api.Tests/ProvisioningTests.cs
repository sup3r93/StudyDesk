using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using StudyDesk.Server.Core.Configuration;
using StudyDesk.Server.Core.Security;
using Xunit;

namespace StudyDesk.Server.Api.Tests;

/// <summary>Verifies local pairing delivery and loopback-only insecure development configuration.</summary>
public sealed class ProvisioningTests
{
    /// <summary>Public HTTP listeners and implicit development HTTP are rejected before startup.</summary>
    [Theory]
    [InlineData("http://127.0.0.1", "http://0.0.0.0:5050", true)]
    [InlineData("http://127.0.0.1", "http://example.com:5050", true)]
    [InlineData("http://127.0.0.1", "http://127.0.0.1:5050", false)]
    [InlineData("http://example.com", "http://127.0.0.1:5050", true)]
    [InlineData("https://example.com/path", "https://127.0.0.1:5050", false)]
    [InlineData("https://example.com", "https://example.com:5050", false)]
    public void UnsafeConfigurationFailsClosed(string origin, string listener, bool allow)
    {
        string directory = Path.Combine(Path.GetTempPath(), "StudyDesk.Unused-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => new ServerSettings(directory, origin, allow, listener));
    }

    /// <summary>Existing private Windows directories are accepted, while broad grants fail without alteration.</summary>
    [Fact]
    public void WindowsDirectoryPermissionsFailClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "StudyDesk.Permissions.Tests-" + Guid.NewGuid().ToString("N"));
        PrivateFile.EnsureDirectory(directory);
        try
        {
            PrivateFile.EnsureDirectory(directory);
            string shared = Path.Combine(directory, "shared");
            Directory.CreateDirectory(shared);
            DirectoryInfo info = new(shared);
            DirectorySecurity security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            info.SetAccessControl(security);
            string before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.Throws<UnauthorizedAccessException>(() => new SqliteDeviceStore(shared));
            Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.Empty(Directory.GetFiles(shared));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Existing shared database and sidecar files fail before host or CLI opens SQLite, without changes.</summary>
    [Theory]
    [InlineData("devices.sqlite3")]
    [InlineData("devices.sqlite3-wal")]
    [InlineData("devices.sqlite3-shm")]
    [InlineData("workspace.sqlite3")]
    [InlineData("workspace.sqlite3-wal")]
    [InlineData("workspace.sqlite3-shm")]
    public async Task WindowsSharedDatabaseFilesAreRejectedWithoutModification(string filename)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "StudyDesk.FilePermissions.Tests-" + Guid.NewGuid().ToString("N"));
        PrivateFile.EnsureDirectory(directory);
        try
        {
            string file = Path.Combine(directory, filename);
            byte[] original = [17, 29, 43, 71];
            await PrivateFile.WriteNewAsync(file, original);
            FileInfo info = new(file);
            FileSecurity security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            info.SetAccessControl(security);
            string acl = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            ServerSettings settings = new(directory, "http://127.0.0.1", true, "http://127.0.0.1:0");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            {
                await using var unexpected = await ServerApplication.BuildAsync(settings);
            });
            string output = Path.Combine(directory, "never-created.studydesk-pairing.json");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PairingProvisioner.CreateOwnerAsync(settings, "Desktop", output));
            Assert.False(File.Exists(output));
            byte[] preserved = await File.ReadAllBytesAsync(file);
            Assert.True(original.SequenceEqual(preserved));
            Assert.Equal(acl, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Changing a database ACL after construction is rejected before subsequent device-store opens.</summary>
    [Fact]
    public async Task DeviceStoreRechecksExistingFilesBeforeEveryOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "StudyDesk.RecheckFiles.Tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            SqliteDeviceStore store = new(directory);
            await store.InitializeAsync();
            CreatedDevice device = await store.CreateAsync("Desktop", DeviceRole.Owner);
            string path = Path.Combine(directory, "devices.sqlite3");
            byte[] original = await File.ReadAllBytesAsync(path);
            FileInfo file = new(path);
            FileSecurity security = file.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(security);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.AuthenticateAsync(device.Token));
            byte[] preserved = await File.ReadAllBytesAsync(path);
            Assert.True(original.SequenceEqual(preserved));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Linked SQLite files are rejected without opening or changing their target.</summary>
    [Fact]
    public async Task LinkedSqliteFilesAreRejectedWithoutTouchingTheirTarget()
    {
        string directory = Path.Combine(Path.GetTempPath(), "StudyDesk.LinkedFiles.Tests-" + Guid.NewGuid().ToString("N"));
        PrivateFile.EnsureDirectory(directory);
        string link = Path.Combine(directory, "workspace.sqlite3-wal");
        try
        {
            string target = Path.Combine(directory, "original.bin");
            byte[] original = [11, 23, 47, 89];
            await PrivateFile.WriteNewAsync(target, original);
            File.CreateSymbolicLink(link, target);
            Assert.Throws<UnauthorizedAccessException>(() => PrivateFile.ValidateSqliteDatabaseFiles(directory, "workspace.sqlite3"));
            ServerSettings settings = new(directory, "http://127.0.0.1", true, "http://127.0.0.1:0");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            {
                await using var unexpected = await ServerApplication.BuildAsync(settings);
            });
            byte[] preserved = await File.ReadAllBytesAsync(target);
            Assert.True(original.SequenceEqual(preserved));
        }
        finally
        {
            File.Delete(link);
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Pairing is a private non-overwriting export and a failed second delivery is revoked.</summary>
    [Fact]
    public async Task PairingExportIsPrivateAndNeverOverwrites()
    {
        string directory = Path.Combine(Path.GetTempPath(), "StudyDesk.Pairing.Tests-" + Guid.NewGuid().ToString("N"));
        PrivateFile.EnsureDirectory(directory);
        try
        {
            ServerSettings settings = new(Path.Combine(directory, "data"), "https://example.com", false, "https://127.0.0.1:5050");
            string output = Path.Combine(directory, "desktop.studydesk-pairing.json");
            DeviceMetadata device = await PairingProvisioner.CreateOwnerAsync(settings, "Desktop", output);
            byte[] first = await File.ReadAllBytesAsync(output);
            using JsonDocument bundle = JsonDocument.Parse(first);
            Assert.Equal(device.Id, bundle.RootElement.GetProperty("deviceId").GetGuid());
            Assert.Equal("https://example.com", bundle.RootElement.GetProperty("serverUrl").GetString());
            string token = bundle.RootElement.GetProperty("token").GetString()!;
            SqliteDeviceStore devices = new(settings.DataDirectory);
            Assert.NotNull(await devices.AuthenticateAsync(token));
            await Assert.ThrowsAsync<IOException>(() => PairingProvisioner.CreateOwnerAsync(settings, "Laptop", output));
            byte[] preserved = await File.ReadAllBytesAsync(output);
            Assert.True(first.SequenceEqual(preserved));
            IReadOnlyList<DeviceMetadata> metadata = await devices.ListAsync();
            Assert.Single(metadata, item => item.RevokedAt is null);
            Assert.Single(metadata, item => item.RevokedAt is not null);
            if (OperatingSystem.IsWindows())
            {
                FileSecurity security = new FileInfo(output).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    Assert.Equal(identity.User, rule.IdentityReference);
                }
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output));
            }
        }
        finally
        {
            if (Path.GetFileName(directory).StartsWith("StudyDesk.Pairing.Tests-", StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
