using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PhoneBox.Agent.Tests;

public sealed class AppCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-apps-" + Guid.NewGuid().ToString("N"));

    public AppCatalogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ConfigStore Config(string widgetType)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, $$"""{"pages":[{"id":"p","title":"P","widgets":[{"id":"w","type":"{{widgetType}}","x":0,"y":0,"w":6,"h":4}]}]}""");
        return new ConfigStore(path);
    }

    private string Root(params string[] bundles)
    {
        var root = Path.Combine(_dir, "Applications");
        foreach (var b in bundles) Directory.CreateDirectory(Path.Combine(root, b, "Contents"));
        return root;
    }

    [Fact]
    public void ScansTopLevelAndOneFolderDeepSortedByName()
    {
        var root = Root("Zed.app", "alpha.app", "Utilities/Terminal.app", "Utilities/Inner/TooDeep.app", "Zed.app/Contents/Helper.app", ".hidden/Ghost.app", ".Helper.app", "Utilities/.Agent.app", "NotAnApp");
        var apps = new AppCatalog(Config("apps"), NullLogger<AppCatalog>.Instance) { WarmIcons = false, Roots = [root] }.List();
        Assert.Equal(["alpha", "Terminal", "Zed"], apps.Select(a => a.Name));
    }

    [Fact]
    public void IdsAreStableAndOnlyKnownIdsResolve()
    {
        var root = Root("Notes.app");
        var catalog = new AppCatalog(Config("apps"), NullLogger<AppCatalog>.Instance) { WarmIcons = false, Roots = [root] };
        var notes = Assert.Single(catalog.List());
        Assert.Equal(AppCatalog.IdOf(Path.Combine(root, "Notes.app")), notes.Id);
        Assert.Equal(16, notes.Id.Length);
        Assert.Same(notes, catalog.Find(notes.Id));
        Assert.Null(catalog.Find("not-an-id"));
        Assert.Null(catalog.Find(Path.Combine(root, "Notes.app"))); // a path is never accepted in place of an id
        Assert.Null(catalog.Find(""));
    }

    [Fact]
    public void OpeningNeedsAnAppsWidgetOnAPage()
    {
        Assert.True(new AppCatalog(Config("apps"), NullLogger<AppCatalog>.Instance) { WarmIcons = false }.OpenAllowed());
        var off = new AppCatalog(Config("clock"), NullLogger<AppCatalog>.Instance) { WarmIcons = false, Roots = [Root("Notes.app")] };
        Assert.False(off.OpenAllowed());
        var id = off.List()[0].Id;
        Assert.NotNull(off.Open(id)); // refused (and on other systems: not supported), never launched
    }

    [Fact]
    public void UnknownIdsAreRefused()
    {
        var catalog = new AppCatalog(Config("apps"), NullLogger<AppCatalog>.Instance) { WarmIcons = false, Roots = [Root("Notes.app")] };
        Assert.NotNull(catalog.Open("0123456789abcdef"));
        Assert.NotNull(catalog.Open("/System/Applications/Calculator.app"));
    }

    [Fact]
    public void MissingRootsGiveAnEmptyList()
    {
        var catalog = new AppCatalog(Config("apps"), NullLogger<AppCatalog>.Instance) { WarmIcons = false, Roots = [Path.Combine(_dir, "nope")] };
        Assert.Empty(catalog.List());
    }
}
