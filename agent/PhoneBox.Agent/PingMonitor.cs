using System.Net.NetworkInformation;

namespace PhoneBox.Agent;

public sealed record PingSnapshot(string Host, double? Ms, double? AvgMs, double LossPct);

<<<<<<< HEAD
/// <summary>Measures internet latency (1.1.1.1) every 5 s; reports the 5-minute average and packet loss.</summary>
public sealed class PingMonitor : BackgroundService
{
    private const string Host = "1.1.1.1";
    private readonly Queue<double?> _samples = new();
    private volatile PingSnapshot? _snap;

    public PingSnapshot? Snapshot() => _snap;

=======
/// <summary>
/// Measures internet latency to <see cref="PingConfig.Host"/> (1.1.1.1 by default) every 5 s; reports the 5-minute
/// average and packet loss. An empty host turns it off (no packets, no snapshot).
/// </summary>
public sealed class PingMonitor(ConfigStore? config = null) : BackgroundService
{
    internal const string DefaultHost = "1.1.1.1";
    private readonly Queue<double?> _samples = new();
    private volatile PingSnapshot? _snap;
    private string? _host;

    public PingSnapshot? Snapshot() => _snap;

    private string? CurrentHost() => config is null ? DefaultHost : config.Get().Ping.Host;

>>>>>>> origin/main
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
<<<<<<< HEAD
            double? ms = null;
            try
            {
                var r = await ping.SendPingAsync(Host, 2000);
                if (r.Status == IPStatus.Success) ms = r.RoundtripTime;
            }
            catch { }

            _samples.Enqueue(ms);
            while (_samples.Count > 60) _samples.Dequeue();
            var ok = _samples.Where(s => s is not null).Select(s => s!.Value).ToList();
            _snap = new PingSnapshot(Host, ms, ok.Count > 0 ? Math.Round(ok.Average(), 1) : null,
                Math.Round(100.0 * (_samples.Count - ok.Count) / _samples.Count, 1));
            await Task.Delay(5000, ct);
        }
    }
=======
            var host = CurrentHost();
            if (host != _host)
            {
                // a new target (or none): the average and the loss must not mix two hosts
                _host = host;
                _samples.Clear();
                _snap = null;
            }
            if (!string.IsNullOrEmpty(host)) Sample(await PingOnce(ping, host), host);
            await Task.Delay(5000, ct);
        }
    }

    private static async Task<double?> PingOnce(Ping ping, string host)
    {
        try
        {
            var r = await ping.SendPingAsync(host, 2000);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch { return null; }
    }

    internal void Sample(double? ms, string host)
    {
        _samples.Enqueue(ms);
        while (_samples.Count > 60) _samples.Dequeue();
        var ok = _samples.Where(s => s is not null).Select(s => s!.Value).ToList();
        _snap = new PingSnapshot(host, ms, ok.Count > 0 ? Math.Round(ok.Average(), 1) : null,
            Math.Round(100.0 * (_samples.Count - ok.Count) / _samples.Count, 1));
    }
>>>>>>> origin/main
}
