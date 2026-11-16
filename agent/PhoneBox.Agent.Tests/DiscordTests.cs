using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The Discord widget's guards: the control option gates every action, and image ids never reach the URL unchecked.</summary>
public sealed class DiscordTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"phonebox-discord-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_configPath); } catch { } }

    private DiscordMonitor Monitor(bool? control, bool pageEnabled = true)
    {
        object options = control is null ? new { } : new { control };
        File.WriteAllText(_configPath, JsonSerializer.Serialize(new
        {
            discord = new { enabled = true, clientId = "1", clientSecret = "s" },
            // At least one page must stay enabled (the config repairs an all-disabled list), so a second page holds no widgets.
            pages = new[]
            {
                new { id = "p", title = "P", enabled = pageEnabled, widgets = new object[] { new { id = "d", type = "discord", x = 0, y = 0, w = 4, h = 5, options } } },
                new { id = "q", title = "Q", enabled = true, widgets = Array.Empty<object>() },
            },
        }));
        return new DiscordMonitor(new ConfigStore(_configPath), NullLogger<DiscordMonitor>.Instance);
    }

    [Fact]
    public async Task A_widget_on_a_disabled_page_does_not_allow_control()
    {
        var error = await Monitor(control: true, pageEnabled: false).ActAsync("mute", null);
        Assert.Contains("turned off", error);
        Assert.False(DiscordMonitor.ControlAllowed(new ConfigStore(_configPath).Get()));
    }

    [Fact]
    public void Control_is_allowed_for_an_enabled_page_with_the_option_on()
    {
        Monitor(control: true);
        Assert.True(DiscordMonitor.ControlAllowed(new ConfigStore(_configPath).Get()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Actions_are_refused_unless_control_is_on(bool? control)
    {
        var error = await Monitor(control).ActAsync("mute", null);
        Assert.Contains("turned off", error);
    }

    [Fact]
    public async Task Actions_need_a_connection_even_with_control_on()
    {
        var error = await Monitor(true).ActAsync("leave", null);
        Assert.Equal("Discord is not connected", error);
    }

    [Fact]
    public void Snapshot_starts_disconnected_and_empty()
    {
        var snap = Monitor(true).Snapshot();
        Assert.False(snap.Connected);
        Assert.Empty(snap.Channels);
        Assert.Empty(snap.Notifications);
    }

    [Theory]
    [InlineData("bad", "1", "abc")]          // unknown kind
    [InlineData("guild", "../1", "abc")]     // id must be digits only
    [InlineData("guild", "", "abc")]
    [InlineData("guild", "1", "a/b")]        // hash must be letters, digits or underscore
    [InlineData("user", "1", "")]
    [InlineData("user", "123456789012345678901234567890", "abc")]
    public async Task Image_requests_with_unsafe_ids_are_rejected_without_a_network_call(string kind, string id, string hash)
    {
        Assert.Null(await DiscordMonitor.ImageAsync(kind, id, hash, CancellationToken.None));
    }
}
