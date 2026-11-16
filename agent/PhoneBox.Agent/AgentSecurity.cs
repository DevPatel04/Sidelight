using System.Security.Cryptography.X509Certificates;

namespace PhoneBox.Agent;

internal static class AgentWebPipeline
{
    internal const string IsLanItemKey = "PhoneBox.IsLan";

    public static void UseAgentPathRewrite(this IApplicationBuilder app, int localPort)
    {
        app.Use((ctx, next) =>
        {
            if (ctx.Connection.LocalPort == localPort && ctx.Request.Path.StartsWithSegments("/agent/api", out var rest))
                ctx.Request.Path = "/api" + rest;
            return next(ctx);
        });
    }

    public static void UseAgentSecurity(
        this IApplicationBuilder app,
        ConfigStore configStore,
        int localPort,
        string[] sameOrigins,
        int lanPort,
        X509Certificate2? lanCert,
        ILogger logger,
        Func<string?, bool>? hasPendingSpotifyState = null)
    {
        app.Use(async (ctx, next) =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            var keyless = false; // a Wi-Fi request let in without a key (pairing claim): it must not touch any phone state
            if (lanCert is not null && ctx.Connection.LocalPort == lanPort)
            {
                // The phone's key opens the panel API; the separate webhook key opens only the webhook endpoints, in a header
                // or, for senders that cannot set headers, in ?token= (URLs end up in logs, so it must not be the phone's key).
                var lan = configStore.Get().Lan;
                var keyed = Lan.Matches(ctx.Request.Headers.Authorization, lan.Token!)
                    || (Lan.IsWebhook(ctx.Request.Path) && (Lan.Matches(ctx.Request.Headers.Authorization, lan.WebhookToken!)
                        || Lan.MatchesQuery(ctx.Request.Query["token"], lan.WebhookToken!)));
                // Pairing without USB: the phone has no key yet and trades a one-time code for it (checked by the endpoint).
                keyless = !keyed && Lan.IsPairingClaim(ctx.Request.Method, ctx.Request.Path);
                if (origin != "" || !Lan.AllowedOverLan(ctx.Request.Path) || !(keyed || keyless))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (keyless)
                {
                    // the code is all it may send: no phone registry, app version or "phone connected" updates
                    if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { } maxBody)
                        maxBody.MaxRequestBodySize = 1024;
                    ctx.Response.Headers.CacheControl = "no-store";
                    await next();
                    return;
                }
                ctx.Items[IsLanItemKey] = true;
            }
            else
            {
                var host = ctx.Request.Host.Host;
                var spotifyCallback = ctx.Request.Method == "GET" && ctx.Request.Path == "/api/controls/spotify/callback"
                    && (hasPendingSpotifyState?.Invoke(ctx.Request.Query["state"]) ?? false);
                if ((host != "127.0.0.1" && host != "localhost") || (origin != "" && !sameOrigins.Contains(origin))
                    || (!spotifyCallback && BrowserGuard.IsForeign(ctx.Request.Headers["Sec-Fetch-Site"], ctx.Request.Headers["Sec-Fetch-Mode"], ctx.Request.Method, ctx.Request.Path)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            if (ctx.Request.Path.StartsWithSegments("/api/state") && ctx.Request.Headers.ContainsKey("X-PhoneBox-App"))
                Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (ctx.Request.Headers.TryGetValue("X-PhoneBox-Device", out var device))
            {
                var problem = PhoneRegistry.Touch(device.ToString(), ctx.Request.Headers["X-PhoneBox-Model"].ToString(),
                    ctx.Connection.LocalPort == localPort ? "USB" : "Wi-Fi", ctx.Request.Headers["X-PhoneBox-WebView"].ToString());
                if (problem is not null) logger.LogWarning("WebView: {Problem}", problem);
            }
            if (ctx.Request.Headers.TryGetValue("X-PhoneBox-App", out var appVersion) && appVersion.ToString().Length is > 0 and < 32)
                UpdateService.AppVersion = appVersion.ToString();
            ctx.Response.Headers.CacheControl = "no-store";
            // only the panel may be framed, and only by the settings page itself (the live preview)
            ctx.Response.Headers["X-Frame-Options"] = ctx.Request.Path.StartsWithSegments("/panel") ? "SAMEORIGIN" : "DENY";
            await next();
        });
    }

    public static void MapConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/console/state", (HttpContext ctx, ConfigStore cfgStore, ConsoleService console) =>
        {
            var cfg = cfgStore.Get();
            if (!cfg.Console.Enabled) return Results.StatusCode(StatusCodes.Status404NotFound);
            var isLan = ctx.Items.ContainsKey(IsLanItemKey);
            var deviceId = DeviceId(ctx);
            if ((isLan && deviceId is null) || !AppConsole.HasWidget(cfg, deviceId))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Json(console.Snapshot());
        });

        app.MapPost("/api/console/act", async (HttpContext ctx, ConsoleActRequest req, ConfigStore cfgStore, ConsoleService console, CancellationToken ct) =>
        {
            var cfg = cfgStore.Get();
            if (!cfg.Console.Enabled) return Results.StatusCode(StatusCodes.Status404NotFound);
            var isLan = ctx.Items.ContainsKey(IsLanItemKey);
            var deviceId = DeviceId(ctx);
            if ((isLan && deviceId is null) || !AppConsole.ControlAllowed(cfg, deviceId))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var clientKey = ClientKey(ctx, deviceId);
            var res = await console.ActAsync(req, ct, deviceId, clientKey);
            return Results.Json(res, statusCode: res.StatusCode);
        });

        app.MapGet("/api/console/windows", (ConsoleService console) => Results.Json(console.ListWindows()));
    }

    private static string? DeviceId(HttpContext ctx)
    {
        var raw = ctx.Request.Headers["X-PhoneBox-Device"].ToString().Trim();
        return raw.Length == 0 ? null : raw;
    }

    private static string ClientKey(HttpContext ctx, string? deviceId)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "local";
        return deviceId is not null ? $"{ip}|{deviceId}" : ip;
    }
}
