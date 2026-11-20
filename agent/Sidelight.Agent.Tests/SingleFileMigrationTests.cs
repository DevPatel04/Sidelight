using Sidelight.Agent;
using Xunit;

public sealed class SingleFileMigrationTests
{
    private static string WithFiles(params string[] names)
    {
        var dir = Directory.CreateTempSubdirectory("sidelight-migrate-").FullName;
        foreach (var n in names) File.WriteAllText(Path.Combine(dir, n), "x");
        return dir;
    }

    [Fact]
    public void ASingleFileAgentRemovesTheFrameworkDependentFilesAndKeepsTheRest()
    {
        var keep = new[] { "SidelightAgent.exe", "THIRD-PARTY-NOTICES.txt", "config.default.json", "widgets.json", "aspnetcorev2_inprocess.dll", "agent.log" };
        var dir = WithFiles([.. UpdateService.FrameworkDependentFiles, .. keep]);
        try
        {
            UpdateService.RemoveFrameworkDependentLeftovers(dir, singleFile: true);
            Assert.Equal(keep.Order(), Directory.EnumerateFiles(dir).Select(Path.GetFileName).Order());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AFrameworkDependentAgentKeepsItsFiles()
    {
        var dir = WithFiles(UpdateService.FrameworkDependentFiles);
        try
        {
            UpdateService.RemoveFrameworkDependentLeftovers(dir, singleFile: false);
            Assert.Equal(UpdateService.FrameworkDependentFiles.Length, Directory.EnumerateFiles(dir).Count());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AMissingDirectoryOrPartialSetIsFine()
    {
        UpdateService.RemoveFrameworkDependentLeftovers(Path.Combine(Path.GetTempPath(), "sidelight-missing-" + Guid.NewGuid().ToString("N")), singleFile: true);
        var dir = WithFiles("SidelightAgent.dll");
        try
        {
            UpdateService.RemoveFrameworkDependentLeftovers(dir, singleFile: true);
            Assert.Empty(Directory.EnumerateFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
