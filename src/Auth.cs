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

public enum LoginOutcome { None, Ok, Wrong, Busy }

/// <param name="Counted">A new wrong guess was recorded (log it, apply Delay). Repeats of the same wrong
/// credentials (a browser still sending an old password) are not counted again.</param>
public readonly record struct LoginResult(LoginOutcome Outcome, string? User, string TriedName, bool Counted, TimeSpan Delay);

/// <summary>
/// HTTP Basic sign-in (works in every browser back to IE6, no JavaScript or cookies).
/// Every wrong guess counts, on public pages too; replies stall a little more each time and too many get the address banned.
/// </summary>
public sealed class Authenticator
{
    public const string Realm = "Modern HTTP File Server";
    static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(15);
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);
    const int MaxWaiting = 16;
    const int MaxCache = 5000;

    /// <summary>
    /// Accounts and the verdicts derived from them, replaced as a whole. A verification that started before an account
    /// change can't write its result into the new state (that was the "old password works 15 more minutes" race).
    /// </summary>
    sealed class State(Dictionary<string, Account> accounts)
    {
        public readonly Dictionary<string, Account> Accounts = accounts;
        public readonly ConcurrentDictionary<string, (string User, DateTime Expires)> Good = new();
        public readonly ConcurrentDictionary<string, DateTime> Bad = new();
    }

    // Unknown names are hashed against this too, so a wrong name takes as long as a wrong password (no name probing).
    static readonly Account Decoy = Account.Create("\0decoy", Guid.NewGuid().ToString());

    // Each password check costs ~50 ms of CPU: at most two at once, and a short queue instead of blocked threads.
    readonly SemaphoreSlim hashing = new(2, 2);
    int waiting;
    volatile State state = new(new(StringComparer.OrdinalIgnoreCase));
    readonly ConcurrentDictionary<string, List<DateTime>> failures = new();
    readonly ConcurrentDictionary<string, DateTime> lastSeen = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> banned = new();

    /// <summary>Raised (on a server thread) when an address reaches <see cref="BanAfter"/> wrong guesses.</summary>
    public event Action<string>? AutoBan;
    public int BanAfter { get; set; } = 10;

    public bool HasAccounts => state.Accounts.Count > 0;

    public void SetAccounts(IEnumerable<Account> list)
    {
        var d = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in list) d[a.Name] = a;
        state = new State(d);   // fresh caches: removed accounts / changed passwords stop working right away
    }

    public DateTime? LastSeen(string user) => lastSeen.TryGetValue(user, out var t) ? t : null;

    public async Task<LoginResult> CheckAsync(HttpRequest request, string address, bool neverBan, CancellationToken ct)
    {
        string header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return new(LoginOutcome.None, null, "", false, TimeSpan.Zero);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(header)));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var s = state;
            var now = DateTime.UtcNow;
            if (s.Good.TryGetValue(key, out var hit) && hit.Expires > now && s.Accounts.ContainsKey(hit.User))
            {
                lastSeen[hit.User] = DateTime.Now;
                return new(LoginOutcome.Ok, hit.User, hit.User, false, TimeSpan.Zero);
            }
            if (s.Bad.TryGetValue(key, out var badAt) && now - badAt < CacheFor)
                return new(LoginOutcome.Wrong, null, "", false, TimeSpan.Zero);   // same wrong credentials again: not a new guess

            string triedName = "", password = "";
            bool parsed = false;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                int colon = decoded.IndexOf(':');
                if (colon > 0) { triedName = decoded[..colon]; password = decoded[(colon + 1)..]; parsed = true; }
            }
            catch { }
            if (!parsed) return Wrong(s, key, address, "", neverBan);

            // An address that has already used up its guesses doesn't get to spend CPU on more.
            if (FailureCount(address) >= BanAfter * 2) return new(LoginOutcome.Wrong, null, triedName, false, TimeSpan.FromSeconds(5));

            if (Interlocked.Increment(ref waiting) > MaxWaiting)
            {
                Interlocked.Decrement(ref waiting);
                return new(LoginOutcome.Busy, null, triedName, false, TimeSpan.Zero);
            }
            bool ok;
            s.Accounts.TryGetValue(triedName, out var account);
            try
            {
                await hashing.WaitAsync(ct);
                try { ok = (account ?? Decoy).Verify(password) && account != null; }
                finally { hashing.Release(); }
            }
            finally { Interlocked.Decrement(ref waiting); }

            if (!ReferenceEquals(s, state)) continue;   // accounts changed while hashing: decide again against the new ones
            if (ok)
            {
                if (s.Good.Count > MaxCache) s.Good.Clear();
                s.Good[key] = (account!.Name, DateTime.UtcNow + CacheFor);
                lastSeen[account.Name] = DateTime.Now;
                failures.TryRemove(address, out _);
                return new(LoginOutcome.Ok, account.Name, triedName, false, TimeSpan.Zero);
            }
            return Wrong(s, key, address, triedName, neverBan);
        }
        return new(LoginOutcome.Wrong, null, "", false, TimeSpan.Zero);
    }

    LoginResult Wrong(State s, string key, string address, string triedName, bool neverBan)
    {
        if (s.Bad.Count > MaxCache) s.Bad.Clear();
        s.Bad[key] = DateTime.UtcNow;
        return new(LoginOutcome.Wrong, null, triedName, true, Fail(address, neverBan));
    }

    /// <summary>Records a wrong guess and returns how long to stall the reply.</summary>
    public TimeSpan Fail(string address, bool neverBan = false)
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
        if (count >= BanAfter && !neverBan && banned.TryAdd(address, 0)) AutoBan?.Invoke(address);
        return count >= 3 ? TimeSpan.FromSeconds(Math.Min(5, count - 2)) : TimeSpan.Zero;
    }

    public int FailureCount(string address)
    {
        if (!failures.TryGetValue(address, out var list)) return 0;
        var now = DateTime.UtcNow;
        lock (list) return list.Count(t => now - t <= FailWindow);
    }

    /// <summary>After an unban: start from a clean slate.</summary>
    public void Forget(string address)
    {
        failures.TryRemove(address, out _);
        banned.TryRemove(address, out _);
    }

    /// <summary>Drops expired entries; call now and then.</summary>
    public void Prune()
    {
        var now = DateTime.UtcNow;
        var s = state;
        foreach (var kv in s.Good) if (kv.Value.Expires <= now) s.Good.TryRemove(kv.Key, out _);
        foreach (var kv in s.Bad) if (now - kv.Value >= CacheFor) s.Bad.TryRemove(kv.Key, out _);
        foreach (var kv in failures)
            lock (kv.Value)
            {
                kv.Value.RemoveAll(t => now - t > FailWindow);
                if (kv.Value.Count == 0) failures.TryRemove(kv.Key, out _);
            }
    }
}
