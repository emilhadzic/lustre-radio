using System.Security.Cryptography;
using LustreRadio.Core.Time;

namespace LustreRadio.Core.Security;

/// <summary>PIN se nikad ne cuva citljiv: cuva se so i PBKDF2 hes.</summary>
public static class PinVault
{
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>PIN ima 4 do 8 cifara.</summary>
    public static bool IsValidPin(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    public static string Hash(string pin)
    {
        if (!IsValidPin(pin))
        {
            throw new ArgumentException("PIN mora imati 4 do 8 cifara.", nameof(pin));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? pin, string? stored)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(stored))
        {
            return false;
        }

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public enum LoginOutcome
{
    Ok,
    WrongPin,

    /// <summary>Previse pogresnih pokusaja; unos je privremeno zakljucan.</summary>
    LockedOut,

    /// <summary>PIN jos nije postavljen.</summary>
    NoPinSet,
}

public sealed record LoginResult(LoginOutcome Outcome, string? Token, TimeSpan RetryAfter);

/// <summary>
/// Admin sesije (spec. deo 12): 5 pogresnih pokusaja zakljucava unos na 5 minuta,
/// sesija istice posle 3 minuta neaktivnosti. Vreme je sa stoperice.
/// PIN proverava audio proces, ne interfejs.
/// </summary>
public sealed class AdminSessions(IMonotonicClock clock, Func<string?> storedPinHash)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TimeSpan> _sessions = [];
    private int _failures;
    private TimeSpan _lockedUntil;

    public int MaxFailures { get; init; } = 5;

    public TimeSpan Lockout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public LoginResult Login(string? pin)
    {
        lock (_sync)
        {
            var now = clock.Elapsed;
            if (now < _lockedUntil)
            {
                return new(LoginOutcome.LockedOut, null, _lockedUntil - now);
            }

            var stored = storedPinHash();
            if (string.IsNullOrEmpty(stored))
            {
                return new(LoginOutcome.NoPinSet, null, TimeSpan.Zero);
            }

            if (!PinVault.Verify(pin, stored))
            {
                _failures++;
                if (_failures >= MaxFailures)
                {
                    _failures = 0;
                    _lockedUntil = now + Lockout;
                    return new(LoginOutcome.LockedOut, null, Lockout);
                }

                return new(LoginOutcome.WrongPin, null, TimeSpan.Zero);
            }

            _failures = 0;
            var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
            _sessions[token] = now + IdleTimeout;
            return new(LoginOutcome.Ok, token, TimeSpan.Zero);
        }
    }

    /// <summary>Da li je sesija vazeca. Svaka uspesna provera produzava rok.</summary>
    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_sync)
        {
            var now = clock.Elapsed;
            if (!_sessions.TryGetValue(token, out var expires))
            {
                return false;
            }

            if (now >= expires)
            {
                _sessions.Remove(token);
                return false;
            }

            _sessions[token] = now + IdleTimeout;
            return true;
        }
    }

    public void Logout(string? token)
    {
        if (token is null)
        {
            return;
        }

        lock (_sync)
        {
            _sessions.Remove(token);
        }
    }

    /// <summary>Posle promene PIN-a sve stare sesije prestaju da vaze.</summary>
    public void InvalidateAll()
    {
        lock (_sync)
        {
            _sessions.Clear();
        }
    }
}
