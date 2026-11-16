using Microsoft.AspNetCore.Http.Features;

namespace PhoneBox.Agent;

/// <summary>
/// Shared security middleware and HTTP route registrations for Photo frame endpoints so both
/// <see cref="Program"/> and real HTTP integration tests run the exact same pipeline.
/// </summary>
internal static class PhotoEndpoints
{
    public static void UseSecurityGate(
        WebApplication app,
        ConfigStore configStore,
        Func<int> getLocalPort,
        Func<int?> getLanPort,
        Func<string?, bool>? isSpotifyCallback = null,
        Action<HttpContext>? onAccepted = null)
    {
        app.Use((ctx, next) =>
        {
            if (ctx.Connection.LocalPort == getLocalPort() && ctx.Request.Path.StartsWithSegments("/agent/api", out var rest))
                ctx.Request.Path = "/api" + rest;
            return next(ctx);
        });

        app.UseRouting();
        app.Use(async (ctx, next) =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            var localPort = getLocalPort();
            var lanPort = getLanPort();
            var keyless = false;
            if (lanPort is { } lp && ctx.Connection.LocalPort == lp)
            {
                var lan = configStore.Get().Lan;
                var keyed = Lan.Matches(ctx.Request.Headers.Authorization, lan.Token!)
                    || (Lan.IsWebhook(ctx.Request.Path) && (Lan.Matches(ctx.Request.Headers.Authorization, lan.WebhookToken!)
                        || Lan.MatchesQuery(ctx.Request.Query["token"], lan.WebhookToken!)));
                keyless = !keyed && Lan.IsPairingClaim(ctx.Request.Method, ctx.Request.Path);
                if (origin != "" || !Lan.AllowedOverLan(ctx.Request.Path) || !(keyed || keyless))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (keyless)
                {
                    ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 1024;
                    ctx.Response.Headers.CacheControl = "no-store";
                    await next();
                    return;
                }
            }
            else
            {
                var host = ctx.Request.Host.Host;
                var spotifyCallback = ctx.Request.Method == "GET" && ctx.Request.Path == "/api/controls/spotify/callback"
                    && isSpotifyCallback?.Invoke(ctx.Request.Query["state"]) == true;
                var sameOrigin1 = $"http://127.0.0.1:{localPort}";
                var sameOrigin2 = $"http://localhost:{localPort}";
                if ((host != "127.0.0.1" && host != "localhost")
                    || (origin != "" && origin != sameOrigin1 && origin != sameOrigin2)
                    || (!spotifyCallback && BrowserGuard.IsForeign(
                        ctx.Request.Headers["Sec-Fetch-Site"],
                        ctx.Request.Headers["Sec-Fetch-Mode"],
                        ctx.Request.Method,
                        ctx.Request.Path)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            onAccepted?.Invoke(ctx);
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Frame-Options"] = ctx.Request.Path.StartsWithSegments("/panel") ? "SAMEORIGIN" : "DENY";
            await next();
        });
    }

    public static void Map(WebApplication app, Func<int?> getLanPort)
    {
        bool IsLan(HttpContext ctx) => getLanPort() is { } lp && ctx.Connection.LocalPort == lp;

        app.MapGet("/api/photos/feed", async (string? widgetId, bool? refresh, CloudPhotoService photos, HttpContext ctx, CancellationToken ct) =>
        {
            var isLan = IsLan(ctx);
            var rawDevice = ctx.Request.Headers["X-PhoneBox-Device"].ToString().Trim();
            var deviceId = string.IsNullOrEmpty(rawDevice) ? null : rawDevice;
            if (isLan && deviceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (string.IsNullOrWhiteSpace(widgetId) || photos.FindWidgetSettings(widgetId, deviceId, enabledPagesOnly: true) is null)
                return Results.Json(new { error = "Photo frame widget was not found" }, statusCode: StatusCodes.Status404NotFound);

            if (isLan && refresh == true && !photos.TryConsumeLanRefresh(deviceId!, widgetId))
            {
                ctx.Response.Headers.RetryAfter = "15";
                return Results.Json(new { error = "Too many refresh requests" }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            return Results.Json(await photos.GetWidgetFeedAsync(widgetId, refresh == true, ct, deviceId, sanitizeErrors: true));
        });

        app.MapGet("/api/photos/image/{id}", async (string id, CloudPhotoService photos, HttpContext ctx, CancellationToken ct) =>
        {
            var isLan = IsLan(ctx);
            var rawDevice = ctx.Request.Headers["X-PhoneBox-Device"].ToString().Trim();
            var deviceId = string.IsNullOrEmpty(rawDevice) ? null : rawDevice;
            if (isLan && deviceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if ((isLan || deviceId is not null) && !photos.HasAnyAuthorizedPhotosWidget(deviceId))
                return Results.Json(new { error = "Photo could not be loaded" }, statusCode: StatusCodes.Status404NotFound);

            var img = await photos.GetPhotoBytesAsync(id, ct, deviceId, enforceDeviceScope: isLan || deviceId is not null);
            if (img is null)
                return Results.Json(new { error = "Photo could not be loaded" }, statusCode: StatusCodes.Status404NotFound);

            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(img.Value.Bytes, img.Value.Mime);
        });

        app.MapPost("/api/photos/preview-feed", async (PhotoPreviewRequest body, CloudPhotoService photos, CancellationToken ct) =>
            Results.Json(await photos.GetPreviewFeedAsync(body, ct)));
        app.MapGet("/api/photos/status", (CloudPhotoService photos) => Results.Json(photos.GetStatus()));
        app.MapGet("/api/photos/local-roots", (CloudPhotoService photos) => Results.Json(photos.DiscoverLocalRoots()));
        app.MapPost("/api/photos/browse", async (PhotoBrowseRequest body, CloudPhotoService photos, CancellationToken ct) =>
            Results.Json(await photos.BrowseAsync(body, ct)));
        app.MapPost("/api/photos/test", async (PhotoTestRequest body, CloudPhotoService photos, CancellationToken ct) =>
            Results.Json(await photos.TestSourceAsync(body, ct)));
    }
}
