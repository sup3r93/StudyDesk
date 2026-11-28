using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using StudyDesk.Contracts;
using StudyDesk.Server.Core.Configuration;
using StudyDesk.Server.Core.Runtime;
using StudyDesk.Server.Core.Security;
using StudyDesk.Server.Core.Storage;

namespace StudyDesk.Server;

/// <summary>Builds the private-data sync API with explicit transport and authorization boundaries.</summary>
public static class ServerApplication
{
    private const long MaximumRequestBytes = 16 * 1024 * 1024;
    private const int MaximumOperations = 100;

    /// <summary>Initializes durable stores and creates an application without starting listeners.</summary>
    public static async Task<WebApplication> BuildAsync(ServerSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(ServerApplication).Assembly.FullName,
        });
        if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            throw new InvalidOperationException("Configure listeners only through ASPNETCORE_URLS; extra Kestrel endpoints are not supported.");
        }

        // Request paths, headers and payloads are never added to custom logs.
        builder.WebHost.ConfigureKestrel(options => ConfigureListeners(options, settings));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(_ => new ServerInstanceLease(settings.DataDirectory));
        builder.Services.AddSingleton<DeviceOperationGate>();
        builder.Services.AddSingleton(_ => new SqliteDeviceStore(settings.DataDirectory));
        builder.Services.AddSingleton<IServerWorkspaceStore>(_ => new SqliteServerWorkspaceStore(settings.DataDirectory));
        WebApplication app = builder.Build();
        try
        {
            _ = app.Services.GetRequiredService<ServerInstanceLease>();
            PrivateFile.ValidateSqliteDatabaseFiles(settings.DataDirectory, "devices.sqlite3");
            PrivateFile.ValidateSqliteDatabaseFiles(settings.DataDirectory, "workspace.sqlite3");
            PrivateFile.PrepareSqliteDatabase(settings.DataDirectory, "workspace.sqlite3");
            SqliteDeviceStore devices = app.Services.GetRequiredService<SqliteDeviceStore>();
            IServerWorkspaceStore workspace = app.Services.GetRequiredService<IServerWorkspaceStore>();
            await devices.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncSnapshotChunkResponse initial = await workspace.GetSnapshotChunkAsync(0, null, cancellationToken).ConfigureAwait(false);
            ServerInfoResponse serverInfo = new(initial.ServerId, initial.Generation, 1);
            app.Use((context, next) => HandleBoundaryAsync(context, next, settings));
            MapRoutes(app, serverInfo, devices, workspace);
            return app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Uses explicit endpoints so environment listener aliases cannot widen an HTTP binding.</summary>
    private static void ConfigureListeners(KestrelServerOptions options, ServerSettings settings)
    {
        options.AddServerHeader = false;
        options.Limits.MaxRequestBodySize = MaximumRequestBytes;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
        foreach (string value in settings.ListenUrls)
        {
            Uri uri = new(value);
            void Configure(ListenOptions listener)
            {
                if (uri.Scheme == "https")
                {
                    listener.UseHttps();
                }
            }

            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address))
            {
                options.Listen(address, uri.Port, Configure);
            }
            else if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                options.ListenLocalhost(uri.Port, Configure);
            }
            else
            {
                throw new InvalidOperationException("A listener requires an explicit IP address or localhost.");
            }
        }
    }

    /// <summary>Rejects insecure traffic and replaces unexpected failures with a content-free response.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "HTTP boundary must not expose private data or exception diagnostics.")]
    private static async Task HandleBoundaryAsync(HttpContext context, Func<Task> next, ServerSettings settings)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        bool loopback = settings.AllowInsecureLoopback
            && context.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local)
            && context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote);
        if (!context.Request.IsHttps && !loopback)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            await next().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException exception)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = exception.StatusCode;
            }
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "The operation could not be completed." }, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>Maps a minimal public identity route and owner-protected data/device routes.</summary>
    private static void MapRoutes(WebApplication app, ServerInfoResponse info, SqliteDeviceStore devices,
        IServerWorkspaceStore workspace)
    {
        app.MapGet("/v1/server", () => Results.Json(info));
        app.MapGet("/v1/access", async Task<IResult> (HttpContext context) => await AuthorizeAsync(context, devices,
            (_, _) => Task.FromResult<IResult>(Results.Json(info))));
        app.MapGet("/v1/snapshot/chunks", async Task<IResult> (HttpContext context) => await AuthorizeAsync(context, devices,
            (_, token) => SnapshotChunkAsync(context, workspace, token)));
        app.MapGet("/v1/snapshot", async Task<IResult> (HttpContext context) => await AuthorizeAsync(context, devices,
            (_, _) => Task.FromResult<IResult>(Results.Json(new { error = "Use /v1/snapshot/chunks?offset=0 for bounded snapshot downloads." },
                statusCode: StatusCodes.Status410Gone))));
        app.MapPost("/v1/sync/push", async Task<IResult> (HttpContext context) => await PushAsync(context, devices, workspace));
        app.MapGet("/v1/devices", async Task<IResult> (HttpContext context) => await AuthorizeAsync(context, devices,
            async (_, token) => Results.Json(await devices.ListAsync(token).ConfigureAwait(false))));
        app.MapPost("/v1/devices/{id:guid}/revoke", (HttpContext context, Guid id) => AuthorizeAsync(context, devices,
            async (_, token) => await devices.RevokeAsync(id, token).ConfigureAwait(false)
                ? Results.NoContent() : Results.NotFound()));
    }

    /// <summary>Reads one bounded coherent chunk, rejecting invalid positions and changed snapshots.</summary>
    private static async Task<IResult> SnapshotChunkAsync(HttpContext context, IServerWorkspaceStore workspace,
        CancellationToken cancellationToken)
    {
        var offsets = context.Request.Query["offset"];
        var cursors = context.Request.Query["cursor"];
        if (offsets.Count != 1 || !long.TryParse(offsets[0], NumberStyles.None, CultureInfo.InvariantCulture, out long offset)
            || cursors.Count > 1 || cursors.Count == 1 && !long.TryParse(cursors[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || offset > 0 && cursors.Count == 0)
        {
            return Results.BadRequest(new { error = "A valid snapshot offset and continuation cursor are required." });
        }

        long? cursor = cursors.Count == 1 ? long.Parse(cursors[0]!, NumberStyles.None, CultureInfo.InvariantCulture) : null;
        try
        {
            return Results.Json(await workspace.GetSnapshotChunkAsync(offset, cursor, cancellationToken).ConfigureAwait(false));
        }
        catch (SnapshotCursorChangedException)
        {
            return Results.Conflict(new { error = "The snapshot changed; restart the download." });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "The snapshot position is invalid." });
        }
    }

    /// <summary>Authorizes afresh inside the same gate used for revocation and mutation acceptance.</summary>
    private static Task<IResult> AuthorizeAsync(HttpContext context, SqliteDeviceStore devices,
        Func<DeviceMetadata, CancellationToken, Task<IResult>> operation) =>
        context.RequestServices.GetRequiredService<DeviceOperationGate>().ExecuteAsync(async token =>
        {
            DeviceMetadata? device = await AuthenticateAsync(context, devices, token).ConfigureAwait(false);
            IResult? refusal = RefuseAccess(context, device);
            if (refusal is not null)
            {
                return refusal;
            }

            PrivateFile.ValidateSqliteDatabaseFiles(context.RequestServices.GetRequiredService<ServerSettings>().DataDirectory, "workspace.sqlite3");
            return await operation(device!, token).ConfigureAwait(false);
        }, context.RequestAborted);

    /// <summary>Reads exactly one bearer header without accepting a credential from URLs or bodies.</summary>
    private static Task<DeviceMetadata?> AuthenticateAsync(HttpContext context, SqliteDeviceStore devices,
        CancellationToken cancellationToken)
    {
        var headers = context.Request.Headers.Authorization;
        string? header = headers.Count == 1 ? headers[0] : null;
        string? credential = header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..] : null;
        return devices.AuthenticateAsync(credential, cancellationToken);
    }

    /// <summary>Rejects missing credentials and unscoped roles without granting partial installation access.</summary>
    private static IResult? RefuseAccess(HttpContext context, DeviceMetadata? device)
    {
        if (device is null)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Unauthorized();
        }

        return device.Role != DeviceRole.Owner ? Results.StatusCode(StatusCodes.Status403Forbidden) : null;
    }

    /// <summary>Reads an authenticated, bounded upload outside the revocation gate, then authorizes again.</summary>
    private static async Task<IResult> PushAsync(HttpContext context, SqliteDeviceStore devices,
        IServerWorkspaceStore workspace)
    {
        DeviceOperationGate gate = context.RequestServices.GetRequiredService<DeviceOperationGate>();
        IResult? refusal = await gate.ExecuteAsync(async token =>
            RefuseAccess(context, await AuthenticateAsync(context, devices, token).ConfigureAwait(false)),
            context.RequestAborted).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!context.Request.HasJsonContentType())
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        SyncPushRequest? request;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            request = await JsonSerializer.DeserializeAsync<SyncPushRequest>(context.Request.Body,
                JsonSerializerOptions.Web, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "A valid sync operation batch is required." });
        }

        if (request?.Operations is null || request.Operations.Count > MaximumOperations
            || request.Operations.Any(operation => operation is null))
        {
            return Results.BadRequest(new { error = "A batch may contain at most 100 valid operations." });
        }

        // A device revoked during upload must fail here before any operation can reach persistence.
        return await AuthorizeAsync(context, devices, async (device, token) =>
        {
            SyncPushResponse response = await workspace.ApplyOperationsAsync(device.Id, request, token).ConfigureAwait(false);
            return Results.Json(response, statusCode: response.Generation == request.Generation
                ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        }).ConfigureAwait(false);
    }
}
