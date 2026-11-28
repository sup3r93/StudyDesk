using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using StudyDesk.Contracts;

namespace StudyDesk.Desktop.Synchronization;

/// <summary>Uses native HTTP transport so credentials never enter the embedded editor.</summary>
public sealed class DataServerClient : IDisposable
{
    private const long MaximumResponseBytes = 32 * 1024 * 1024;
    private const long MaximumSnapshotBytes = 256 * 1024 * 1024;
    private const int MaximumSnapshotEntities = 100000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly string token;

    /// <summary>Creates a bounded client with normal certificate validation and no automatic redirects.</summary>
    public DataServerClient(Uri origin, string token, bool allowInsecureLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ServerEndpointPolicy.Validate(origin.AbsoluteUri, allowInsecureLoopback);
        ConnectionStore.ValidateToken(token);
        this.token = token;
        HttpClientHandler handler = new() { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        client = new HttpClient(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Obtains public identity without attaching the association token.</summary>
    public async Task<ServerInfoResponse> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "v1/server");
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<ServerInfoResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks the device credential without downloading the owner's full workspace.</summary>
    public async Task<ServerInfoResponse> VerifyAccessAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "v1/access");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<ServerInfoResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Uploads a bounded operation batch and awaits a durable server receipt.</summary>
    public async Task<SyncPushResponse> PushAsync(SyncPushRequest body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        using HttpRequestMessage request = new(HttpMethod.Post, "v1/sync/push") { Content = JsonContent.Create(body, options: JsonOptions) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<SyncPushResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Assembles one coherent snapshot from bounded chunks without acknowledging partial downloads.</summary>
    public async Task<SyncSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        List<SpaceDto> spaces = [];
        List<PageDto> pages = [];
        HashSet<Guid> identities = [];
        Guid serverId = Guid.Empty;
        Guid generation = Guid.Empty;
        long? cursor = null;
        long offset = 0;
        long totalBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = "v1/snapshot/chunks?offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (cursor.HasValue ? "&cursor=" + cursor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
            using HttpRequestMessage request = new(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            SyncSnapshotChunkResponse chunk = await ReadAsync<SyncSnapshotChunkResponse>(response, cancellationToken, size =>
            {
                totalBytes += size;
                if (totalBytes > MaximumSnapshotBytes)
                {
                    throw new HttpRequestException("The workspace exceeds this version's 256 MiB snapshot limit. Local notes are unchanged.");
                }
            }).ConfigureAwait(false);
            if (chunk.ServerId == Guid.Empty || chunk.Generation == Guid.Empty || chunk.Cursor < 0 || chunk.Spaces is null || chunk.Pages is null
                || chunk.Spaces.Count > 64 || chunk.Pages.Count > 1)
            {
                throw new HttpRequestException("The server returned an invalid snapshot chunk.");
            }

            if (cursor is null)
            {
                serverId = chunk.ServerId;
                generation = chunk.Generation;
                cursor = chunk.Cursor;
            }
            else if (chunk.ServerId != serverId || chunk.Generation != generation || chunk.Cursor != cursor.Value)
            {
                throw new HttpRequestException("The workspace changed during download. Retry synchronization; local work is preserved.");
            }

            int count = chunk.Spaces.Count + chunk.Pages.Count;
            if (spaces.Count + pages.Count + count > MaximumSnapshotEntities
                || (chunk.NextOffset.HasValue && (count == 0 || chunk.NextOffset.Value != offset + count)))
            {
                throw new HttpRequestException("The server snapshot has an invalid continuation or too many entities.");
            }

            foreach (SpaceDto space in chunk.Spaces)
            {
                if (space is null || space.Id == Guid.Empty || !identities.Add(space.Id))
                {
                    throw new HttpRequestException("The snapshot contains an invalid or repeated entity identity.");
                }

                spaces.Add(space);
            }

            foreach (PageDto page in chunk.Pages)
            {
                if (page is null || page.Id == Guid.Empty || !identities.Add(page.Id))
                {
                    throw new HttpRequestException("The snapshot contains an invalid or repeated entity identity.");
                }

                pages.Add(page);
            }

            if (chunk.NextOffset is not long next)
            {
                return new SyncSnapshotResponse(serverId, generation, cursor.Value, spaces, pages);
            }

            offset = next;
        }
    }

    /// <summary>Releases the native connection pool and credential-bearing request headers.</summary>
    public void Dispose()
    {
        client.DefaultRequestHeaders.Authorization = null;
        client.Dispose();
    }

    /// <summary>Reads a bounded JSON response and maps protocol failures to safe UI messages.</summary>
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken, Action<int>? countBytes = null)
    {
        if (!response.IsSuccessStatusCode)
        {
            string message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "This device token was not accepted.",
                HttpStatusCode.Forbidden => "This device does not have permission for the requested operation.",
                HttpStatusCode.Conflict => "The workspace changed during download. Retry synchronization; local work is preserved.",
                _ => "The data server could not complete the request.",
            };
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new HttpRequestException("The server response exceeds the supported size.");
        }

        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        countBytes?.Invoke(bytes.Length);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new JsonException("The server returned an empty response.");
    }
}
