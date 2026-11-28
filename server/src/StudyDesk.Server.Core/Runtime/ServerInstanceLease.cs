using StudyDesk.Server.Core.Security;

namespace StudyDesk.Server.Core.Runtime;

/// <summary>Enforces one HTTP host per data directory while leaving local provisioning available.</summary>
public sealed class ServerInstanceLease : IDisposable
{
    private readonly FileStream handle;

    /// <summary>Acquires a non-overwriting private lease or fails if another host owns the directory.</summary>
    public ServerInstanceLease(string dataDirectory)
    {
        PrivateFile.EnsureDirectory(dataDirectory);
        handle = PrivateFile.OpenExclusiveLease(Path.Combine(dataDirectory, "server.host.lock"));
    }

    /// <summary>Releases the OS lease after all HTTP requests have stopped.</summary>
    public void Dispose() => handle.Dispose();
}
