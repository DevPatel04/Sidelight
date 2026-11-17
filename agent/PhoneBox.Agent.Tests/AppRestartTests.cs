using PhoneBox.Agent;
using Xunit;

/// <summary>#182: under launchd or systemd the agent leaves the restart to the service manager.</summary>
public class AppRestartTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void TheSetupLoginItemAndSystemdAreServiceManagers()
    {
        Assert.True(AppRestart.ServiceManaged(Env(("XPC_SERVICE_NAME", "net.oxog.phonebox.agent"))));
        Assert.True(AppRestart.ServiceManaged(Env(("INVOCATION_ID", "5f0c1c3a9e8b4f0d"))));
    }

    [Fact]
    public void ATerminalOrTheTrayIsNot()
    {
        // macOS sets XPC_SERVICE_NAME for apps and Terminal sessions too ("0" or the app's own id)
        Assert.False(AppRestart.ServiceManaged(Env(("XPC_SERVICE_NAME", "0"))));
        Assert.False(AppRestart.ServiceManaged(Env(("XPC_SERVICE_NAME", "application.com.apple.Terminal.1234"))));
        Assert.False(AppRestart.ServiceManaged(Env()));
        Assert.False(AppRestart.ServiceManaged(Env(("INVOCATION_ID", ""))));
    }

    [Fact]
    public void TheRestartCodeIsAFailure() => Assert.NotEqual(0, AppRestart.ServiceRestartCode);
}
