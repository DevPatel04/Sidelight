using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class PushVersionTests
{
    [Fact]
    public void ConcurrentUpdatesEachAdvanceTheVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-push-{Guid.NewGuid():N}.json");
        var store = new PushStore(NullLogger<PushStore>.Instance, path);
        var original = store.Version;
        var value = PushStore.ParseValue("sample");
        const int writes = 8000;
        Parallel.For(0, writes, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            _ => Assert.Null(store.Set("sample", value, null)));
        Assert.True(store.Version - original >= writes, "Every successful write must advance the version");
    }
}
