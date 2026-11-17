using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ModernHFS;

public sealed record Adapter(string Name, string Address);

public static class Network
{
    /// <summary>Connected network cards with an IPv4 address (Ethernet, Wi-Fi, VPN, Hyper-V switches...).</summary>
    public static List<Adapter> Adapters()
    {
        var list = new List<Adapter>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                        list.Add(new Adapter(ni.Name, u.Address.ToString()));
            }
        }
        catch { }
        return list;
    }

    static readonly string[] PublicIpServices = ["https://api.ipify.org", "https://ipv4.icanhazip.com", "https://checkip.amazonaws.com"];
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ModernHFS/1.0");
        return http;
    }

    /// <summary>The address the Internet sees for this network (asks a public "what is my IP" service).</summary>
    public static async Task<string?> PublicIPv4Async()
    {
        foreach (var url in PublicIpServices)
        {
            try
            {
                var text = (await Http.GetStringAsync(url)).Trim();
                if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString();
            }
            catch { }
        }
        return null;
    }
}

/// <summary>
/// Reverse DNS for visitor addresses, looked up in the background and cached for 6 hours.
/// LAN devices resolve through the router / hosts file ("galaxy-s21.lan"), Internet ones to the provider's host name.
/// </summary>
public sealed class HostNames
{
    static readonly TimeSpan Keep = TimeSpan.FromHours(6);
    readonly ConcurrentDictionary<string, (string? Name, DateTime At)> cache = new();
    readonly ConcurrentDictionary<string, byte> pending = new();
    readonly SemaphoreSlim lookups = new(4);
    int version;

    /// <summary>Changes whenever a new name arrives (the UI repaints then).</summary>
    public int Version => Volatile.Read(ref version);

    /// <summary>Drops expired names (and everything, past 20,000 addresses).</summary>
    public void Prune()
    {
        if (cache.Count > 20_000) { cache.Clear(); return; }
        var now = DateTime.UtcNow;
        foreach (var kv in cache) if (now - kv.Value.At >= Keep) cache.TryRemove(kv.Key, out _);
    }

    /// <summary>The cached name, or null (and a lookup starts in the background).</summary>
    public string? Get(string address)
    {
        if (cache.TryGetValue(address, out var hit) && DateTime.UtcNow - hit.At < Keep) return hit.Name;
        Start(address);
        return hit.Name;
    }

    void Start(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.Equals(IPAddress.None) || !pending.TryAdd(address, 0)) return;
        _ = Task.Run(async () =>
        {
            await lookups.WaitAsync();
            string? name = null;
            try
            {
                if (IPAddress.IsLoopback(ip)) name = "this PC";
                else
                {
                    var lookup = Dns.GetHostEntryAsync(ip);
                    if (await Task.WhenAny(lookup, Task.Delay(4000)) == lookup && lookup.Status == TaskStatus.RanToCompletion)
                        name = lookup.Result.HostName;
                    if (string.IsNullOrWhiteSpace(name) || name == address) name = null;
                    else name = Traffic.Clean(name.TrimEnd('.'), 120);
                }
            }
            catch { }
            finally
            {
                cache[address] = (name, DateTime.UtcNow);
                pending.TryRemove(address, out _);
                lookups.Release();
                Interlocked.Increment(ref version);
            }
        });
    }
}
