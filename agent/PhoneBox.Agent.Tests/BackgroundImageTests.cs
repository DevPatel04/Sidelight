using Xunit;
using PhoneBox.Agent;

namespace PhoneBox.Agent.Tests;

public class BackgroundImageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 5, 6, 7, 8];

    private static (ConfigStore Store, string Dir) NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"phonebox-bg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return (new ConfigStore(Path.Combine(dir, "config.json")), dir);
    }

    [Theory]
    [InlineData("\"BLOBS\"", "blobs")]
    [InlineData("\" rain \"", "rain")]
    [InlineData("\"fireworks\"", "auto")] // a background the panel does not have
    [InlineData("null", "auto")]
    public void AnUnknownBackgroundFallsBackToTheThemesOwn(string json, string expected)
    {
        var (s, dir) = NewStore();
        try
        {
            Assert.Equal("auto", s.Get().Display.Background);
            Assert.Null(s.Save("{\"display\":{\"background\":" + json + "}}"));
            Assert.Equal(expected, s.Get().Display.Background);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("ABCDEF0123456789")] // ids are lower case
    [InlineData("0123")]
    public void ABackgroundImageThatIsNotAnIdIsDropped(string id)
    {
        var (s, dir) = NewStore();
        try
        {
            Assert.Null(s.Save("{\"display\":{\"backgroundImage\":\"" + id + "\"}}"));
            Assert.Equal("", s.Get().Display.BackgroundImage);
            Assert.Null(BackgroundImages.Get(s, id));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void APictureIsStoredUnderItsHashBesideTheSettings()
    {
        var (s, dir) = NewStore();
        try
        {
            var (id, error) = BackgroundImages.Save(s, Png);
            Assert.Null(error);
            Assert.True(BackgroundImages.IsId(id));
            Assert.True(File.Exists(Path.Combine(dir, "backgrounds", id!)));
            var got = BackgroundImages.Get(s, id!);
            Assert.NotNull(got);
            Assert.Equal("image/png", got.Value.Mime);
            Assert.Equal(Png, got.Value.Bytes);
            Assert.Equal(id, BackgroundImages.Save(s, Png).Id); // the same picture keeps its id
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OnlyJpegPngAndWebpAreAccepted()
    {
        var (s, dir) = NewStore();
        try
        {
            Assert.NotNull(BackgroundImages.Save(s, "GIF89a......"u8.ToArray()).Error);
            Assert.NotNull(BackgroundImages.Save(s, "<svg></svg>"u8.ToArray()).Error);
            Assert.NotNull(BackgroundImages.Save(s, []).Error);
            Assert.NotNull(BackgroundImages.Save(s, new byte[BackgroundImages.MaxBytes + 1]).Error);
            Assert.False(Directory.Exists(Path.Combine(dir, "backgrounds")) && Directory.EnumerateFiles(Path.Combine(dir, "backgrounds")).Any());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PicturesThatAreNeitherSavedNorJustUploadedAreRemoved()
    {
        var (s, dir) = NewStore();
        try
        {
            var saved = BackgroundImages.Save(s, Png).Id!;
            Assert.Null(s.Save("{\"display\":{\"backgroundImage\":\"" + saved + "\"}}"));
            var tried = BackgroundImages.Save(s, Jpeg).Id!;
            Assert.NotNull(BackgroundImages.Get(s, saved)); // an upload never removes what the phone shows
            Assert.NotNull(BackgroundImages.Get(s, tried));

            // the user tries another picture, then saves without one
            var other = BackgroundImages.Save(s, [.. Jpeg, 9]).Id!;
            Assert.Null(BackgroundImages.Get(s, tried));
            Assert.Null(s.Save("{\"display\":{\"backgroundImage\":\"\"}}"));
            BackgroundImages.Prune(s);
            Assert.Null(BackgroundImages.Get(s, saved));
            Assert.Null(BackgroundImages.Get(s, other));
        }
        finally { Directory.Delete(dir, true); }
    }
}
