using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The Claude quota check through its seams: a fake HTTP handler and a credentials file of the test's own, so nothing
/// here reads the real token or reaches api.anthropic.com. The usage endpoint is rate limited per account and shared
/// with Claude Code, so these tests count the requests PhoneBox would send.
/// </summary>
public sealed class ClaudeQuotaScheduleTests : IDisposable
{
    private const long Minute = 60_000;
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-claude-schedule-").FullName;
    private readonly FakeAnthropic _api = new();
    private readonly long _t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Creds => Path.Combine(_dir, "credentials.json");

    private QuotaMonitor Agent() => new(NullLogger<QuotaMonitor>.Instance)
    {
        ClaudeCachePath = Path.Combine(_dir, "claude-quota.json"),
        CredentialsPath = Creds,
        HttpHandler = _api,
    };

    private void SignIn(string token, long expiresAt) => File.WriteAllText(Creds,
        $$$"""{"claudeAiOauth":{"accessToken":"{{{token}}}","expiresAt":{{{expiresAt}}},"subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}}""");

    private const string Usage = """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z","severity":"normal"}]}""";

    private static QuotaProvider Claude(QuotaMonitor agent) => Assert.Single(agent.Snapshot(), p => p.Id == "claude");

    [Fact]
    public async Task AFreshReadingIsNotFetchedAgainWhenTheAgentRestarts()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        await Agent().ClaudeTickAsync(_t0, default);
        Assert.Equal(1, _api.Calls);

        var restarted = Agent();
        restarted.LoadClaudeCache(_t0 + Minute);
        await restarted.ClaudeTickAsync(_t0 + Minute, default);
        Assert.Equal(1, _api.Calls); // the reading is a minute old: no request
        Assert.Equal(42, Assert.Single(Claude(restarted).Limits).Percent);

        await restarted.ClaudeTickAsync(_t0 + 3 * Minute + 1, default);
        Assert.Equal(2, _api.Calls);
    }

    [Fact]
    public async Task ARunningAgentUsesTheReadingAnotherOneJustFetched()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var first = Agent();
        var second = Agent();
        await first.ClaudeTickAsync(_t0, default);
        await second.ClaudeTickAsync(_t0 + 10_000, default);
        Assert.Equal(1, _api.Calls);
        Assert.Equal(42, Assert.Single(Claude(second).Limits).Percent);
    }

    [Fact]
    public async Task ARetryAfterIsKeptAcrossARestart()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.TooManyRequests, "{}", retryAfterSeconds: 230);
        await Agent().ClaudeTickAsync(_t0, default);

        var restarted = Agent();
        restarted.LoadClaudeCache(_t0 + Minute);
        await restarted.ClaudeTickAsync(_t0 + Minute, default);
        await restarted.ClaudeTickAsync(_t0 + 3 * Minute, default);
        Assert.Equal(1, _api.Calls); // the server asked for 230 s
        Assert.Contains("limiting", Claude(restarted).Error);

        _api.Respond(HttpStatusCode.OK, Usage);
        await restarted.ClaudeTickAsync(_t0 + 236_000, default);
        Assert.Equal(2, _api.Calls);
        Assert.Null(Claude(restarted).Error);
    }

    [Fact]
    public async Task ARenewedTokenIsUsedOnTheNextPassAfterAnExpiredOne()
    {
        SignIn("old", _t0 - Minute);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        for (var t = _t0; t <= _t0 + 4 * Minute; t += 30_000) await agent.ClaudeTickAsync(t, default);
        Assert.Equal(0, _api.Calls); // no request with an expired token
        Assert.Contains("expired", Claude(agent).Error);

        SignIn("new", _t0 + 3_600_000); // Claude Code renewed it
        await agent.ClaudeTickAsync(_t0 + 4 * Minute + 30_000, default);
        await agent.ClaudeTickAsync(_t0 + 5 * Minute, default);
        Assert.Equal(1, _api.Calls); // used within a minute
        Assert.Null(Claude(agent).Error);
    }

    [Fact]
    public async Task ATokenThatExpiresWithinAMinuteIsNotSent()
    {
        SignIn("almost", _t0 + 20_000);
        await Agent().ClaudeTickAsync(_t0, default);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task ARefusedTokenIsNotSentAgainUntilClaudeCodeRenewsIt()
    {
        SignIn("revoked", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.Unauthorized, """{"error":"invalid token"}""");
        var agent = Agent();
        for (var t = _t0; t <= _t0 + 60 * Minute; t += 30_000) await agent.ClaudeTickAsync(t, default);
        Assert.Equal(1, _api.Calls);
        Assert.NotNull(Claude(agent).Error);

        SignIn("renewed", _t0 + 7_200_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        await agent.ClaudeTickAsync(_t0 + 61 * Minute, default);
        Assert.Equal(2, _api.Calls);
        Assert.Null(Claude(agent).Error);
    }

    [Fact]
    public async Task AnUnreachableNetworkIsRetriedSoonWithoutShowingAnError()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);

        // the computer woke up and the network is not back yet
        _api.Fail(new HttpRequestException(HttpRequestError.ConnectionError, "No route to host"));
        await agent.ClaudeTickAsync(_t0 + 3 * Minute, default);
        Assert.Null(Claude(agent).Error);
        Assert.Equal(42, Assert.Single(Claude(agent).Limits).Percent);

        _api.Respond(HttpStatusCode.OK, Usage);
        await agent.ClaudeTickAsync(_t0 + 3 * Minute + 30_000, default);
        Assert.Equal(3, _api.Calls);
        Assert.Null(Claude(agent).Error);
    }

    [Fact]
    public async Task ANetworkThatStaysDownIsReportedAndBacksOff()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Fail(new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known"));
        var agent = Agent();
        for (var t = _t0; t <= _t0 + 10 * Minute; t += 30_000) await agent.ClaudeTickAsync(t, default);
        Assert.NotNull(Claude(agent).Error);
        // three quick retries 30 s apart, then the back-off (1, 2, 4 … minutes)
        Assert.InRange(_api.Calls, 5, 7);
    }

    [Fact]
    public async Task AReplyWithoutLimitsKeepsTheLastReadingAndTheCache()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);

        _api.Respond(HttpStatusCode.OK, """{"something":"new"}""");
        await agent.ClaudeTickAsync(_t0 + 3 * Minute, default);
        Assert.Equal(42, Assert.Single(Claude(agent).Limits).Percent);
        Assert.NotNull(Claude(agent).Error);

        var restarted = Agent();
        restarted.LoadClaudeCache(_t0 + 4 * Minute);
        Assert.Equal(42, Assert.Single(Claude(restarted).Limits).Percent);

        // an unknown reply format won't change by asking again soon
        for (var t = _t0 + 3 * Minute + 30_000; t < _t0 + 18 * Minute; t += 30_000) await agent.ClaudeTickAsync(t, default);
        Assert.Equal(2, _api.Calls);
    }

    [Fact]
    public async Task AHtmlErrorPageWithStatus200KeepsTheLastReading()
    {
        SignIn("token-1", _t0 + 3_600_000);
        _api.Respond(HttpStatusCode.OK, Usage);
        var agent = Agent();
        await agent.ClaudeTickAsync(_t0, default);

        _api.Respond(HttpStatusCode.OK, "<html>captive portal</html>");
        await agent.ClaudeTickAsync(_t0 + 3 * Minute, default);
        Assert.Equal(42, Assert.Single(Claude(agent).Limits).Percent);
    }

    [Fact]
    public async Task AConnectFailureOfTheRealHandlerIsAConnectionError()
    {
        // the quick retry relies on SocketsHttpHandler reporting a failed connect (through the IPv4-first callback)
        // as HttpRequestError.ConnectionError
        var free = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = (ctx, ct) => QuotaMonitor.ConnectIpv4First(ctx.DnsEndPoint, ct) });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{port}/"));
        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
    }

    private sealed class FakeAnthropic : HttpMessageHandler
    {
        private Func<HttpResponseMessage> _next = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        public int Calls { get; private set; }

        public void Respond(HttpStatusCode status, string body, int? retryAfterSeconds = null) => _next = () =>
        {
            var res = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfterSeconds is { } s) res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
            return res;
        };

        public void Fail(Exception ex) => _next = () => throw ex;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.ToString());
            return Task.FromResult(_next());
        }
    }
}
