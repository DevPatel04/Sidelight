using System.Text.Json;
using Sidelight.Agent;
using Xunit;

/// <summary>
/// Update channels: which release the test channel picks from a /releases listing, and how the channel and repo
/// settings are cleaned up (the repo ends up in the GitHub API URL the updater calls).
/// </summary>
public class UpdateChannelTests
{
    private static AgentConfig Load(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sidelight-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { return new ConfigStore(path).Get(); }
        finally { File.Delete(path); }
    }

    private static string? Newest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return UpdateService.NewestRelease(doc.RootElement)?.GetProperty("tag_name").GetString();
    }

    [Fact]
    public void TestBuildsSortAfterTheReleaseTheyBuildOnAndBeforeTheNextOne()
    {
        Assert.Equal("v0.9.14.57", Newest("""
            [{ "tag_name": "v0.9.14", "prerelease": false },
             { "tag_name": "v0.9.14.57", "prerelease": true },
             { "tag_name": "v0.9.14.9", "prerelease": true }]
            """));
        Assert.Equal("v0.9.15", Newest("""
            [{ "tag_name": "v0.9.14.57", "prerelease": true },
             { "tag_name": "v0.9.15", "prerelease": false }]
            """));
    }

    [Fact]
    public void DraftsAndOddTagsAreSkipped()
    {
        Assert.Equal("v0.9.14", Newest("""
            [{ "tag_name": "v0.9.16", "draft": true },
             { "tag_name": "nightly" },
             { "tag_name": "v0.9.14" }]
            """));
        Assert.Null(Newest("[]"));
    }

    [Fact]
    public void ChannelAndRepoFallBackToTheDefaults()
    {
        var defaults = Load("{}").Updates;
        Assert.Equal("stable", defaults.Channel);
        Assert.Equal(UpdateConfig.DefaultRepo, defaults.Repo);

        var test = Load("""{ "updates": { "channel": "test", "repo": "someone/sidelight-fork" } }""").Updates;
        Assert.Equal("test", test.Channel);
        Assert.Equal("someone/sidelight-fork", test.Repo);

        Assert.Equal("stable", Load("""{ "updates": { "channel": "nightly" } }""").Updates.Channel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sidelight")]
    [InlineData("owner/..")]
    [InlineData("owner/name/releases")]
    [InlineData("owner/name?x=1")]
    [InlineData("https://github.com/owner/name")]
    public void ARepoThatIsNotOwnerSlashNameIsReplaced(string repo) =>
        Assert.Equal(UpdateConfig.DefaultRepo, Load($$"""{ "updates": { "repo": {{JsonSerializer.Serialize(repo)}} } }""").Updates.Repo);

    [Theory]
    [InlineData("ersinkoc/phonebox")]
    [InlineData("DevPatel04/PhoneBox")]
    public void APhoneBoxRepoFromCarriedOverSettingsBecomesTheSidelightOne(string repo) =>
        Assert.Equal(UpdateConfig.DefaultRepo, Load($$"""{ "updates": { "repo": "{{repo}}" } }""").Updates.Repo);
}
