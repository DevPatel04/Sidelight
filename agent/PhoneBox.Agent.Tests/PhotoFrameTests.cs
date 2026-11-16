using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The Photo frame's folder rules: bounded scan, no links or cloud-only files, id-only serving, gating.</summary>
public sealed class PhotoFrameTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-photos-" + Guid.NewGuid().ToString("N"));
    private string Photos => Path.Combine(_dir, "photos");

    public PhotoFrameTests() => Directory.CreateDirectory(Photos);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private PhotoFrame Frame(bool widget = true, bool pageEnabled = true, bool subfolders = true)
    {
        var path = Path.Combine(_dir, "config.json");
        var widgets = widget ? """[{"id":"w","type":"photos","x":0,"y":0,"w":6,"h":4}]""" : "[]";
        File.WriteAllText(path, $$"""
            {"photos":{"folder":{{JsonSerializer.Serialize(Photos)}},"subfolders":{{(subfolders ? "true" : "false")}}},
             "pages":[{"id":"p","title":"P","enabled":{{(pageEnabled ? "true" : "false")}},"widgets":{{widgets}} },
                      {"id":"q","title":"Q","widgets":[{"id":"c","type":"clock","x":0,"y":0,"w":6,"h":4}]}]}
            """);
        return new PhotoFrame(new ConfigStore(path), NullLogger<PhotoFrame>.Instance) { CacheDir = Path.Combine(_dir, "cache") };
    }

    [Fact]
    public void BroadOrMissingFoldersAreRefused()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotNull(PhotoFrame.RefuseRoot(Path.GetPathRoot(home)!));
        Assert.NotNull(PhotoFrame.RefuseRoot(home));
        Assert.NotNull(PhotoFrame.RefuseRoot(Path.GetDirectoryName(home)!));
        Assert.NotNull(PhotoFrame.RefuseRoot("relative/folder"));
        Assert.NotNull(PhotoFrame.RefuseRoot(Path.Combine(_dir, "missing")));
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(PhotoFrame.RefuseRoot(@"\\server\share\photos"));
            Assert.NotNull(PhotoFrame.RefuseRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        }
        else Assert.NotNull(PhotoFrame.RefuseRoot("/etc"));
        Assert.Null(PhotoFrame.RefuseRoot(Photos));
    }

    [Fact]
    public void TheScanKeepsImagesOnlyAndFollowsTheSubfolderSetting()
    {
        File.WriteAllBytes(Path.Combine(Photos, "a.jpg"), Jpeg);
        File.WriteAllBytes(Path.Combine(Photos, "b.PNG"), Png);
        File.WriteAllText(Path.Combine(Photos, "notes.txt"), "not a photo");
        File.WriteAllBytes(Path.Combine(Photos, "empty.jpg"), []);
        Directory.CreateDirectory(Path.Combine(Photos, "trip"));
        File.WriteAllBytes(Path.Combine(Photos, "trip", "c.jpeg"), Jpeg);

        var all = PhotoFrame.Scan(Photos, subfolders: true);
        Assert.Equal(["a.jpg", "b.PNG", "c.jpeg"], all.Photos.Select(p => Path.GetFileName(p.Path)).Order().ToArray());
        Assert.Null(all.Error);
        var top = PhotoFrame.Scan(Photos, subfolders: false);
        Assert.Equal(2, top.Photos.Count);
        Assert.Equal(PhotoFrame.IdOf("trip/c.jpeg"), all.Photos.Single(p => p.Path.EndsWith("c.jpeg")).Id);
    }

    [Fact]
    public void LinksOutOfTheFolderAreSkipped()
    {
        var outside = Path.Combine(_dir, "secret.jpg");
        File.WriteAllBytes(outside, Jpeg);
        try { File.CreateSymbolicLink(Path.Combine(Photos, "link.jpg"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no symlink rights on this machine
        Assert.Empty(PhotoFrame.Scan(Photos, subfolders: true).Photos);
    }

    [Theory]
    [InlineData(FileAttributes.Normal, false)]
    [InlineData(FileAttributes.Archive, false)]
    [InlineData(FileAttributes.Offline, true)]
    [InlineData((FileAttributes)0x00040000, true)] // FILE_ATTRIBUTE_RECALL_ON_OPEN
    [InlineData((FileAttributes)0x00400000, true)] // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (OneDrive Files On-Demand)
    public void CloudOnlyFilesAreRecognised(FileAttributes a, bool cloudOnly) => Assert.Equal(cloudOnly, PhotoFrame.CloudOnly(a));

    [Fact]
    public void TheTypeComesFromTheBytesNotTheName()
    {
        Assert.Equal("image/jpeg", PhotoFrame.MimeOf(Jpeg));
        Assert.Equal("image/png", PhotoFrame.MimeOf(Png));
        Assert.Equal("image/gif", PhotoFrame.MimeOf("GIF89a\0\0\0\0\0\0"u8));
        Assert.Equal("image/webp", PhotoFrame.MimeOf("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(PhotoFrame.MimeOf("<svg xmlns=\"http://www.w3.org/2000/svg\">"u8));
        Assert.Null(PhotoFrame.MimeOf("<html><script>"u8));
    }

    [Fact]
    public async Task PhotosAreServedByIdOnly()
    {
        File.WriteAllBytes(Path.Combine(Photos, "a.jpg"), Jpeg);
        File.WriteAllText(Path.Combine(Photos, "fake.jpg"), "<html>not an image</html>");
        var frame = Frame();
        frame.ScanNow();
        var (ids, error, _) = frame.List();
        Assert.Null(error);
        Assert.Equal(2, ids.Count);

        var real = await frame.Get(PhotoFrame.IdOf("a.jpg"), CancellationToken.None);
        Assert.Equal(Jpeg, real.Bytes);
        Assert.Equal("image/jpeg", real.Type);

        Assert.Null((await frame.Get(PhotoFrame.IdOf("fake.jpg"), CancellationToken.None)).Bytes); // not an image inside
        Assert.Null((await frame.Get("../../config.json", CancellationToken.None)).Bytes);
        Assert.Null((await frame.Get(PhotoFrame.IdOf("missing.jpg"), CancellationToken.None)).Bytes);
    }

    [Fact]
    public async Task NothingIsListedOrServedWithoutTheWidgetOnAnEnabledPage()
    {
        File.WriteAllBytes(Path.Combine(Photos, "a.jpg"), Jpeg);
        foreach (var frame in new[] { Frame(widget: false), Frame(pageEnabled: false) })
        {
            Assert.False(frame.Shown());
            var (ids, error, _) = frame.List();
            Assert.Empty(ids);
            Assert.NotNull(error);
            Assert.Null((await frame.Get(PhotoFrame.IdOf("a.jpg"), CancellationToken.None)).Bytes);
        }
    }

    [Fact]
    public void TheFolderNeverReachesThePhone()
    {
        var path = Path.Combine(_dir, "config.json");
        _ = Frame();
        var pub = JsonSerializer.Serialize(new ConfigStore(path).Get().ToPublic());
        Assert.DoesNotContain("phonebox-photos-", pub);
    }
}
