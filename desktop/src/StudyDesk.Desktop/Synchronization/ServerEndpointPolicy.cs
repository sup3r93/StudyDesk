using System.Net;

namespace StudyDesk.Desktop.Synchronization;

/// <summary>Validates server addresses before any association credential can be sent.</summary>
public static class ServerEndpointPolicy
{
    /// <summary>Requires a clean HTTPS origin, with an explicit loopback-only development exception.</summary>
    public static Uri Validate(string address, bool allowInsecureLoopback)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
        {
            throw new ArgumentException("Enter a server origin without credentials, a path, query or fragment.", nameof(address));
        }

        bool loopback = IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? ip) && IPAddress.IsLoopback(ip);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && allowInsecureLoopback && loopback))
        {
            throw new ArgumentException("A verified HTTPS server is required.", nameof(address));
        }

        return uri;
    }
}
