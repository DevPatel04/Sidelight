using Sidelight.Agent;
using Xunit;

/// <summary>
/// A PhoneBox settings folder is copied into the Sidelight one on first start: settings, keys and icons come along,
/// binaries and caches do not, and an existing Sidelight setup is never overwritten.
/// </summary>
public sealed class LegacyImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sidelight-import-" + Guid.NewGuid().ToString("N"));
    private string Old => Path.Combine(_root, "PhoneBox");
    private string New => Path.Combine(_root, "Sidelight");

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Write(string dir, string name, string text)
    {
        var path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void SettingsKeysAndIconsAreCopiedButBinariesAndCachesAreNot()
    {
        Write(Old, "config.json", "{\"lan\":{}}");
        Write(Old, "lan.pfx", "cert");
        Write(Old, Path.Combine("icons", "a.png"), "png");
        Write(Old, Path.Combine("app", "PhoneBoxAgent"), "exe");
        Write(Old, Path.Combine("photos-cache", "x.jpg"), "jpg");

        Assert.True(AppPaths.ImportLegacy(Old, New));
        Assert.Equal("{\"lan\":{}}", File.ReadAllText(Path.Combine(New, "config.json")));
        Assert.True(File.Exists(Path.Combine(New, "lan.pfx")));
        Assert.True(File.Exists(Path.Combine(New, "icons", "a.png")));
        Assert.False(Directory.Exists(Path.Combine(New, "app")));
        Assert.False(Directory.Exists(Path.Combine(New, "photos-cache")));
        Assert.True(File.Exists(Path.Combine(Old, "config.json"))); // copied, not moved
    }

    [Fact]
    public void AnExistingSidelightSetupOrAMissingPhoneBoxOneIsLeftAlone()
    {
        Assert.False(AppPaths.ImportLegacy(Old, New));
        Assert.False(Directory.Exists(New));

        Write(Old, "config.json", "old");
        Write(New, "config.json", "new");
        Assert.False(AppPaths.ImportLegacy(Old, New));
        Assert.Equal("new", File.ReadAllText(Path.Combine(New, "config.json")));
    }
}
