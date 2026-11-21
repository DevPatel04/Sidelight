using System.Net;
using System.Text.Json;
using Sidelight.Agent;
using Xunit;

/// <summary>Antigravity conversations on the AI sessions widget, read from the IDE's language server.</summary>
public sealed class AntigravitySessionsTests
{
    private static readonly long Now = DateTimeOffset.Parse("2026-11-21T12:00:00Z").ToUnixTimeMilliseconds();

    private const string Reply = """
        {"trajectorySummaries":{
          "e265a9fe-b2c0-4fc6-97fc-8ecbade7433a":{"summary":"Private chat title","status":"CASCADE_RUN_STATUS_RUNNING",
            "lastModifiedTime":"2026-11-21T11:59:30.5Z","createdTime":"2026-11-21T11:40:00Z","lastUserInputTime":"2026-11-21T11:58:00Z",
            "workspaces":[{"workspaceFolderAbsoluteUri":"file:///home/me/projects/weather-kit"}],
            "latestNotifyUserStep":{"notifyUser":{"notificationContent":"secret reply text"}}},
          "4e5ebaee-9689-49c1-ae82-c9b9db5bc35d":{"summary":"Another title","status":"CASCADE_RUN_STATUS_IDLE",
            "lastModifiedTime":"2026-11-21T10:00:00Z","createdTime":"2026-11-21T09:00:00Z","workspaces":[]},
          "d0d121f7-26f8-4b33-b33b-5d2f28982383":{"status":"CASCADE_RUN_STATUS_IDLE","lastModifiedTime":"2026-11-20T08:00:00Z",
            "workspaces":[{"workspaceFolderAbsoluteUri":"file:///home/me/old"}]},
          "7210fd88-582d-4d40-8ba5-923f499de845":{"status":"CASCADE_RUN_STATUS_RUNNING","lastModifiedTime":"2026-11-19T08:00:00Z",
            "workspaces":[{"workspaceFolderAbsoluteUri":"file:///home/me/long-run/"}]}
        }}
        """;

    [Fact]
    public void RunningAndRecentConversationsBecomeSessionsWithoutTheirText()
    {
        using var doc = JsonDocument.Parse(Reply);
        var list = AntigravitySessions.Parse(doc.RootElement, Now)!;

        // running ones first however old their last change; an idle one only within the last hours
        Assert.Equal(new[] { "antigravity:e265a9fe-b2c0-4fc6-97fc-8ecbade7433a", "antigravity:7210fd88-582d-4d40-8ba5-923f499de845",
            "antigravity:4e5ebaee-9689-49c1-ae82-c9b9db5bc35d" }, list.Select(s => s.Id));
        var busy = list[0];
        Assert.Equal("antigravity", busy.Tool);
        Assert.Equal("busy", busy.Status);
        Assert.Equal("weather-kit", busy.Project);
        Assert.Equal("weather-kit-e265", busy.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T11:59:30.5Z").ToUnixTimeMilliseconds(), busy.Since);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T11:58:00Z").ToUnixTimeMilliseconds(), busy.TurnStartedAt);
        Assert.Null(busy.Title);
        Assert.Equal("long-run", list[1].Project);
        Assert.Equal("idle", list[2].Status);
        Assert.Equal("antigravity", list[2].Project); // no workspace folder
        Assert.Null(list[2].TurnStartedAt);

        var sent = JsonSerializer.Serialize(list);
        Assert.DoesNotContain("chat title", sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Another title", sent);
        Assert.DoesNotContain("secret", sent);
        Assert.DoesNotContain("/home/me", sent);
    }

    [Theory]
    [InlineData("{}", 0)]                         // a server with no conversations yet
    [InlineData("""{"trajectorySummaries":[]}""", -1)] // not the reply this reader knows
    [InlineData("[]", -1)]
    public void UnknownRepliesAreNotSessions(string json, int count)
    {
        using var doc = JsonDocument.Parse(json);
        var list = AntigravitySessions.Parse(doc.RootElement, Now);
        Assert.Equal(count, list?.Count ?? -1);
    }

    [Fact]
    public async Task TheListIsAskedWithTheServersOwnToken()
    {
        HttpRequestMessage? seen = null;
        using var http = new HttpClient(new Fake(req => { seen = req; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply) }; }));
        var list = await AntigravitySessions.ReadEndpointAsync(http, new QuotaMonitor.AntigravityEndpoint(44039, "main", "https"), Now, CancellationToken.None);

        Assert.Equal(3, list!.Count);
        Assert.Equal("https://127.0.0.1:44039/exa.language_server_pb.LanguageServerService/GetAllCascadeTrajectories", seen!.RequestUri!.ToString());
        Assert.Equal("main", seen.Headers.GetValues("X-Codeium-Csrf-Token").Single());
    }

    [Fact]
    public async Task ARefusingServerIsNotAnAnswer()
    {
        using var http = new HttpClient(new Fake(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.Null(await AntigravitySessions.ReadEndpointAsync(http, new QuotaMonitor.AntigravityEndpoint(33373, "ext"), Now, CancellationToken.None));
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }
}
