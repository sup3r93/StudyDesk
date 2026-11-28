using System.Net;

namespace StudyDesk.Server.Core.Configuration;

/// <summary>Contains validated server configuration obtained from environment variables.</summary>
public sealed class ServerSettings
{
    /// <summary>Creates validated settings; callers normally use environment configuration.</summary>
    /// <param name="dataDirectory">Absolute private application data directory.</param>
    /// <param name="publicOrigin">HTTPS server origin, or explicit insecure loopback test origin.</param>
    /// <param name="allowInsecureLoopback">Permits HTTP only when all HTTP listeners bind real loopback.</param>
    /// <param name="listenUrls">Semicolon-separated Kestrel listener URLs.</param>
    public ServerSettings(string dataDirectory, string publicOrigin, bool allowInsecureLoopback, string listenUrls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
        {
            throw new ArgumentException("The server data directory must be absolute.", nameof(dataDirectory));
        }

        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out Uri? origin)
            || origin.Scheme is not ("https" or "http") || origin.UserInfo.Length != 0
            || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/"
            || origin.Host is "*" or "+")
        {
            throw new ArgumentException("Configure a concrete HTTP(S) server origin without a path, user information or query.", nameof(publicOrigin));
        }

        if (origin.Scheme == "http" && (!allowInsecureLoopback || !IsLoopbackHost(origin.Host)))
        {
            throw new ArgumentException("HTTP origins require explicit loopback-only development configuration.", nameof(publicOrigin));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(listenUrls);
        string[] listeners = listenUrls.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (listeners.Length == 0)
        {
            throw new ArgumentException("Configure at least one explicit listener.", nameof(listenUrls));
        }

        foreach (string listener in listeners)
        {
            if (!Uri.TryCreate(listener, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/"
                || !(IPAddress.TryParse(uri.Host.Trim('[', ']'), out _) || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                || uri.Scheme == "http" && (!allowInsecureLoopback || !IsLoopbackHost(uri.Host)))
            {
                throw new ArgumentException("Each listener must bind an explicit IP address or localhost, using HTTPS or explicitly permitted loopback HTTP.", nameof(listenUrls));
            }
        }

        DataDirectory = Path.GetFullPath(dataDirectory);
        PublicOrigin = origin;
        AllowInsecureLoopback = allowInsecureLoopback;
        ListenUrls = Array.AsReadOnly(listeners);
    }

    /// <summary>Gets the configured private data location.</summary>
    public string DataDirectory { get; }
    /// <summary>Gets the verified origin written into private association bundles.</summary>
    public Uri PublicOrigin { get; }
    /// <summary>Gets whether loopback-only HTTP development traffic is explicitly enabled.</summary>
    public bool AllowInsecureLoopback { get; }
    /// <summary>Gets the validated Kestrel listener URLs.</summary>
    public IReadOnlyList<string> ListenUrls { get; }

    /// <summary>Reads configuration after optionally loading an explicitly selected private file.</summary>
    public static ServerSettings LoadFromEnvironment()
    {
        string? file = Environment.GetEnvironmentVariable("STUDYDESK_SERVER_ENV_FILE");
        if (!string.IsNullOrWhiteSpace(file))
        {
            EnvironmentFile.Load(file);
        }

        string data = Required("STUDYDESK_SERVER_DATA_DIR");
        string origin = Required("STUDYDESK_SERVER_ORIGIN");
        string urls = Required("ASPNETCORE_URLS");
        string? flag = Environment.GetEnvironmentVariable("STUDYDESK_SERVER_ALLOW_INSECURE_LOOPBACK");
        if (flag is not null && !bool.TryParse(flag, out _))
        {
            throw new InvalidOperationException("STUDYDESK_SERVER_ALLOW_INSECURE_LOOPBACK must be true or false.");
        }

        return new ServerSettings(data, origin, string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase), urls);
    }

    /// <summary>Accepts literal IP loopback addresses and Kestrel's localhost loopback binding.</summary>
    public static bool IsLoopbackHost(string host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address);

    /// <summary>Reads a required process variable without including its value in errors.</summary>
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Configure the required environment variable {name}.");
}
