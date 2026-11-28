using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using StudyDesk.Desktop.Configuration;
using StudyDesk.Desktop.Editor;
using StudyDesk.Desktop.Synchronization;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Exercises credential destinations, private persistence and untrusted document boundaries.</summary>
public sealed partial class DesktopBoundaryTests
{
    /// <summary>Rejects lookalike origins, unusual ports and embedded credentials at the native bridge.</summary>
    [Theory]
    [InlineData("https://studydesk.local/index.html", true)]
    [InlineData("https://studydesk.local.evil.example/index.html", false)]
    [InlineData("https://studydesk.local:9443/index.html", false)]
    [InlineData("https://user@studydesk.local/index.html", false)]
    [InlineData("http://studydesk.local/index.html", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    public void BridgeRequiresExactLocalOrigin(string address, bool expected)
    {
        Assert.Equal(expected, LocalEditorPolicy.IsTrusted(address));
    }

    /// <summary>Allows HTTP only when the address is loopback and the owner has explicitly enabled it.</summary>
    [Theory]
    [InlineData("http://127.0.0.1:5091", true, true)]
    [InlineData("http://[::1]:5091", true, true)]
    [InlineData("http://127.0.0.1:5091", false, false)]
    [InlineData("http://localhost:5091", true, false)]
    [InlineData("http://192.0.2.1:5091", true, false)]
    [InlineData("https://example.invalid", false, true)]
    [InlineData("https://user:secret@example.invalid", false, false)]
    [InlineData("https://example.invalid/another-path", false, false)]
    public void ServerEndpointRequiresSecureExplicitDestination(string address, bool allowLoopback, bool expected)
    {
        if (expected)
        {
            Assert.NotNull(ServerEndpointPolicy.Validate(address, allowLoopback));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => ServerEndpointPolicy.Validate(address, allowLoopback));
        }
    }

    /// <summary>Rejects active or remote image sources before a saved document reaches Chromium.</summary>
    [Theory]
    [InlineData("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=")]
    [InlineData("data:text/html;base64,PGh0bWw+PC9odG1sPg==")]
    [InlineData("https://example.invalid/image.png")]
    [InlineData("data:image/png;base64,bm90IGEgcG5nIGltYWdl")]
    public void LoadedDocumentRejectsUnsupportedImages(string source)
    {
        string document = JsonSerializer.Serialize(new { type = "doc", content = new[] { new { type = "image", attrs = new { src = source } } } });
        Assert.False(DocumentPolicy.IsAllowedImage(source));
        Assert.Throws<InvalidOperationException>(() => DocumentPolicy.Validate(document));
    }

    /// <summary>Accepts bounded raster signatures and rejects total document overflow.</summary>
    [Fact]
    public void DocumentAndImageBoundsCoverLoadedContent()
    {
        string png = "data:image/png;base64," + Convert.ToBase64String(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Assert.True(DocumentPolicy.IsAllowedImage(png));
        DocumentPolicy.Validate("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}");
        string oversized = JsonSerializer.Serialize(new { type = "doc", content = new[] { new { type = "text", text = new string('x', 1024 * 1024) } } });
        Assert.Throws<InvalidOperationException>(() => DocumentPolicy.Validate(oversized));
        string hugeImage = "data:image/png;base64," + Convert.ToBase64String(new byte[262145]);
        Assert.False(DocumentPolicy.IsAllowedImage(hugeImage));
    }

    /// <summary>Survives missing or stale advisory metadata without sending a credential to another server.</summary>
    [Fact]
    public void ProtectedAssociationIsOneAtomicAuthoritativeUnit()
    {
        using PrivateWorkspace fixture = new();
        DesktopSettings settings = DesktopSettings.Load();
        ConnectionStore storage = new(settings);
        ServerConnection original = new(new Uri("https://first.example.invalid"), Guid.NewGuid(), Guid.NewGuid());
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        storage.Save(original, token);
        Assert.Equal(original, storage.LoadConnection());
        Assert.Equal(token, storage.LoadToken(original));
        Assert.DoesNotContain(token, Encoding.UTF8.GetString(File.ReadAllBytes(settings.TokenFile)), StringComparison.Ordinal);

        ServerConnection unrelated = new(new Uri("https://second.example.invalid"), Guid.NewGuid(), Guid.NewGuid());
        File.WriteAllText(settings.ConnectionFile, JsonSerializer.Serialize(unrelated));
        Assert.Equal(original, storage.LoadConnection());
        Assert.Throws<InvalidOperationException>(() => storage.LoadToken(unrelated));
        File.Delete(settings.ConnectionFile);
        Assert.Equal(original, storage.LoadConnection());
        Assert.Equal(token, storage.LoadToken(original));
    }

    /// <summary>Refuses existing private paths exposed to unrelated Windows users.</summary>
    [Fact]
    public void SharedDirectoryIsRejectedWithoutChangingItsPermissions()
    {
        using PrivateWorkspace fixture = new();
        string path = Path.Combine(fixture.Root, "shared-test");
        PrivateDirectoryPolicy.Ensure(path);
        DirectoryInfo directory = new(path);
        DirectorySecurity security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData, AccessControlType.Allow));
        directory.SetAccessControl(security);
        Assert.Throws<InvalidOperationException>(() => PrivateDirectoryPolicy.Ensure(path));
    }

    /// <summary>Inherit-only access still exposes newly created files and cannot be accepted as a private directory.</summary>
    [Fact]
    public void InheritOnlyForeignFileAccessIsRejected()
    {
        using PrivateWorkspace fixture = new();
        string path = Path.Combine(fixture.Root, "inheritable-test");
        PrivateDirectoryPolicy.Ensure(path);
        DirectoryInfo directory = new(path);
        DirectorySecurity security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        directory.SetAccessControl(security);
        Assert.Throws<InvalidOperationException>(() => PrivateDirectoryPolicy.Ensure(path));
    }

    /// <summary>Existing environment and database files are checked before configuration or storage reads.</summary>
    [Theory]
    [InlineData(".env")]
    [InlineData("workspace/workspace.sqlite3")]
    public void ExistingPrivateFileWithForeignReadAccessIsRejected(string relative)
    {
        using PrivateWorkspace fixture = new();
        DesktopSettings.Load();
        string path = Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "Synthetic test file");
        }

        FileInfo file = new(path);
        FileSecurity security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData, AccessControlType.Allow));
        file.SetAccessControl(security);
        Assert.Throws<InvalidOperationException>(() => DesktopSettings.Load());
    }

    /// <summary>Isolates environment configuration and restores all process state after a test.</summary>
    private sealed class PrivateWorkspace : IDisposable
    {
        private readonly Dictionary<string, string?> previous;

        /// <summary>Creates a private synthetic workspace without using the actual StudyDesk installation.</summary>
        public PrivateWorkspace()
        {
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudyDeskTests", Guid.NewGuid().ToString("N"));
            string[] names = ["STUDYDESK_DESKTOP_ENV_FILE", "STUDYDESK_DESKTOP_DATA_DIR", "STUDYDESK_DESKTOP_TOKEN_FILE", "STUDYDESK_DESKTOP_CONNECTION_FILE", "STUDYDESK_DESKTOP_LANGUAGE", "STUDYDESK_DESKTOP_ALLOW_INSECURE_LOOPBACK"];
            previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            foreach (string name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
            Environment.SetEnvironmentVariable("STUDYDESK_DESKTOP_ENV_FILE", Path.Combine(Root, ".env"));
        }

        /// <summary>Gets the unique synthetic data directory owned by this test.</summary>
        public string Root { get; }

        /// <summary>Restores the process setting and removes only this synthetic workspace.</summary>
        public void Dispose()
        {
            foreach ((string name, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            string allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudyDeskTests") + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
