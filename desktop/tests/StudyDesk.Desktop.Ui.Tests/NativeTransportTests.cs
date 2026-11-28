using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudyDesk.Desktop.Synchronization;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Verifies the real native transport's secret headers and redirect boundary.</summary>
public sealed partial class NativeTransportTests
{
    /// <summary>Public identity requests never carry the association secret.</summary>
    [Fact]
    public async Task PublicIdentityRequestDoesNotCarryBearerToken()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        string body = JsonSerializer.Serialize(new { serverId = Guid.NewGuid(), generation = Guid.NewGuid(), protocolVersion = 1 });
        Task<string> request = ReceiveOneAsync(listener, "200 OK", body, null, timeout.Token);
        using DataServerClient client = new(Address(listener), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), true);
        await client.GetInfoAsync(timeout.Token);
        string headers = await request;
        Assert.StartsWith("GET /v1/server ", headers, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization:", headers, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Protected snapshot reads carry the credential only in the native request header.</summary>
    [Fact]
    public async Task SnapshotRequestCarriesNativeBearerToken()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        string body = JsonSerializer.Serialize(new { serverId = Guid.NewGuid(), generation = Guid.NewGuid(), cursor = 0, spaces = Array.Empty<object>(), pages = Array.Empty<object>() });
        Task<string> request = ReceiveOneAsync(listener, "200 OK", body, null, timeout.Token);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using DataServerClient client = new(Address(listener), token, true);
        await client.GetSnapshotAsync(timeout.Token);
        string headers = await request;
        Assert.StartsWith("GET /v1/snapshot/chunks?offset=0 ", headers, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer " + token, headers, StringComparison.Ordinal);
    }

    /// <summary>A server redirect is surfaced as failure before any credential can follow its destination.</summary>
    [Fact]
    public async Task RedirectResponseIsNotFollowed()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<string> request = ReceiveOneAsync(listener, "302 Found", string.Empty, "https://example.invalid/not-contacted", timeout.Token);
        using DataServerClient client = new(Address(listener), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), true);
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSnapshotAsync(timeout.Token));
        Assert.Equal(HttpStatusCode.Found, error.StatusCode);
        Assert.StartsWith("GET /v1/snapshot/chunks?offset=0 ", await request, StringComparison.Ordinal);
    }

    /// <summary>Returns a literal loopback origin for a listener assigned an ephemeral port.</summary>
    private static Uri Address(TcpListener listener)
    {
        return new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Reads one bounded request and supplies deterministic HTTP without an external service.</summary>
    private static async Task<string> ReceiveOneAsync(TcpListener listener, string status, string body, string? location, CancellationToken cancellationToken)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = connection.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        StringBuilder received = new();
        while (received.Length < 16384)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            received.Append(line).Append("\r\n");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string headers = "HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nContent-Length: "
            + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n"
            + (location is null ? string.Empty : "Location: " + location + "\r\n") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return received.ToString();
    }
}
