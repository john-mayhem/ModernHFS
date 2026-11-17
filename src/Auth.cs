using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace ModernHFS;

/// <summary>A visitor account. Only a salted PBKDF2 hash of the password is stored.</summary>
public sealed class Account
{
    const int Iterations = 120_000;

    public string Name { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;

    public static Account Create(string name, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new Account
        {
            Name = name,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Derive(password, salt)),
        };
    }

    public bool Verify(string password)
    {
        try
        {
            var expected = Convert.FromBase64String(Hash);
            var actual = Derive(password, Convert.FromBase64String(Salt));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }

    static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
}

/// <summary>
/// HTTP Basic sign-in (works in every browser back to IE6, no JavaScript or cookies).
/// Wrong passwords stall the reply a little more each time; too many get the address banned.
/// </summary>
public sealed class Authenticator
{
    public const string Realm = "Modern HTTP File Server";
    static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(15);
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);

    // Each password check costs ~50 ms of CPU; never run more than two at once (a flood of guesses can't hog the PC).
    readonly SemaphoreSlim hashing = new(2, 2);
    volatile Dictionary<string, Account> accounts = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (string User, DateTime Expires)> cache = new();
    readonly ConcurrentDictionary<string, List<DateTime>> failures = new();
    readonly ConcurrentDictionary<string, DateTime> lastSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (on a server thread) when an address reaches <see cref="BanAfter"/> failed sign-ins.</summary>
    public event Action<string>? AutoBan;
    public int BanAfter { get; set; } = 10;

    public bool HasAccounts => accounts.Count > 0;

    public void SetAccounts(IEnumerable<Account> list)
    {
        var d = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in list) d[a.Name] = a;
        accounts = d;
        cache.Clear();   // removed accounts / changed passwords stop working right away
    }

    public DateTime? LastSeen(string user) => lastSeen.TryGetValue(user, out var t) ? t : null;

    /// <summary>The signed-in account name, or null. <paramref name="wrong"/> = credentials were sent but don't match.</summary>
    public string? Check(HttpRequest request, out bool wrong, out string triedName)
    {
        wrong = false;
        triedName = "";
        string header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(header)));
        var known = accounts;
        if (cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow && known.ContainsKey(hit.User))
        {
            lastSeen[hit.User] = DateTime.Now;
            return hit.User;
        }

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
        catch { wrong = true; return null; }
        int colon = decoded.IndexOf(':');
        if (colon <= 0) { wrong = true; return null; }
        triedName = decoded[..colon];
        var password = decoded[(colon + 1)..];
        bool ok = false;
        if (known.TryGetValue(triedName, out var account))
        {
            hashing.Wait();
            try { ok = account.Verify(password); }
            finally { hashing.Release(); }
        }
        if (ok && account != null)
        {
            cache[key] = (account.Name, DateTime.UtcNow + CacheFor);
            lastSeen[account.Name] = DateTime.Now;
            return account.Name;
        }
        wrong = true;
        return null;
    }

    /// <summary>Records a failed sign-in and returns how long to stall the reply.</summary>
    public TimeSpan Fail(string address)
    {
        var now = DateTime.UtcNow;
        var list = failures.GetOrAdd(address, _ => []);
        int count;
        lock (list)
        {
            list.RemoveAll(t => now - t > FailWindow);
            list.Add(now);
            count = list.Count;
        }
        bool local = address is "127.0.0.1" or "::1";
        if (count == BanAfter && !local) AutoBan?.Invoke(address);
        return count >= 3 ? TimeSpan.FromSeconds(Math.Min(5, count - 2)) : TimeSpan.Zero;
    }

    public void Succeeded(string address) => failures.TryRemove(address, out _);
}
