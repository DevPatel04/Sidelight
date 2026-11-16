using System.Net;
using System.Net.Sockets;
using PhoneBox.Agent;
using Xunit;

public sealed class QuotaConnectTests
{
    [Fact]
    public void Ipv4AddressesAreTriedBeforeIpv6ButKeepTheirOrder()
    {
        // api.anthropic.com resolves to an IPv6 address first; on networks where IPv6 is advertised but not
        // routable, .NET failed with "Socket is not connected" instead of using the IPv4 address after it.
        var addresses = new[]
        {
            IPAddress.Parse("2607:6bc0::10"), IPAddress.Parse("160.79.104.10"),
            IPAddress.Parse("fe80::1"), IPAddress.Parse("10.0.0.2"),
        };

        var ordered = QuotaMonitor.Ipv4First(addresses).Select(a => a.ToString()).ToArray();

        Assert.Equal(["160.79.104.10", "10.0.0.2", "2607:6bc0::10", "fe80::1"], ordered);
    }

    [Fact]
    public async Task ConnectsToAnIpv4Listener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await QuotaMonitor.ConnectIpv4First(new DnsEndPoint("127.0.0.1", port), cts.Token);
        using var server = await accept;

        await stream.WriteAsync(new byte[] { 42 }, cts.Token);
        var buffer = new byte[1];
        Assert.Equal(1, await server.GetStream().ReadAsync(buffer, cts.Token));
        Assert.Equal(42, buffer[0]);
    }

    [Fact]
    public async Task FailsWithAnHttpErrorWhenNothingListens()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await QuotaMonitor.ConnectIpv4First(new DnsEndPoint("127.0.0.1", port), cts.Token));
    }
}
