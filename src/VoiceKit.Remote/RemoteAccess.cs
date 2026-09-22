using System.Security.Cryptography;
using System.Text;

namespace VoiceKit.Remote;

/// <summary>Per-server-lifetime pairing. No passwords, audio or tokens are written to disk/logs.</summary>
public sealed class RemoteAccess(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset _window;
    private int _attempts;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string PairingCode { get; } = RandomNumberGenerator.GetInt32(100_000_000).ToString("D8");
    public (string? Token, bool Limited) Pair(string? code)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _window >= TimeSpan.FromMinutes(1)) { _window = now; _attempts = 0; }
            // Limit globally, not only by IP (IPv6/address rotation must not bypass the limit).
            if (++_attempts > 10) return (null, true);
            return (Equal(code, PairingCode) ? _token : null, false);
        }
    }
    public bool Authorize(string? token) => Equal(token, _token);
    private static bool Equal(string? supplied, string expected) => supplied is not null && supplied.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}
