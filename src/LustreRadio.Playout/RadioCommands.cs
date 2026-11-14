using System.Globalization;
using LustreRadio.Core.Security;
using LustreRadio.Data;
using LustreRadio.Ipc;

namespace LustreRadio.Playout;

/// <summary>Ono sto komande smeju da urade sa radijem. Ostvaruje ga EngineHost.</summary>
public interface IRadioControl
{
    RadioState GetState();

    void Pause();

    void Resume();

    /// <summary>Sledeca pesma. Ako je zadat ID, preskace samo ako ta pesma jos svira (dupli dodir ne preskace dve).</summary>
    bool Next(string? expectedTrackId);

    /// <summary>Postavlja glasnocu (vec svedenu na dozvoljeni opseg).</summary>
    void SetVolume(double volume);

    void Rescan();

    void SetMusicFolders(IReadOnlyList<string> folders);
}

/// <summary>
/// Izvrsava komande interfejsa. Audio proces ne veruje interfejsu: sam proverava PIN,
/// admin sesiju i dozvoljeni opseg glasnoce (spec. 2.6).
/// </summary>
public sealed class RadioCommandHandler : IIpcHandler
{
    public const string PinKey = "admin-pin";
    public const string VolumeMinKey = "volume-min";
    public const string VolumeMaxKey = "volume-max";

    private static readonly HashSet<string> AdminCommands = ["rescan", "set-music-folders", "set-volume-range", "logout"];

    private readonly IRadioControl _radio;
    private readonly LustreDatabase _db;
    private readonly AdminSessions _sessions;

    public RadioCommandHandler(IRadioControl radio, LustreDatabase db, AdminSessions sessions)
    {
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public static (double Min, double Max) VolumeRange(LustreDatabase db)
    {
        var min = Parse(db.Settings.Get(VolumeMinKey), 0.2);
        var max = Parse(db.Settings.Get(VolumeMaxKey), 1.0);
        return min <= max ? (min, max) : (0.2, 1.0);
    }

    public RadioState CurrentState() => _radio.GetState();

    public IpcMessage Handle(IpcMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var name = command.Name ?? string.Empty;
        var args = command.Args ?? new Dictionary<string, string>();

        if (AdminCommands.Contains(name) && !_sessions.Validate(command.Session))
        {
            return IpcMessage.Failure(command.Id, "admin-required");
        }

        switch (name)
        {
            case "get-state":
                // Namerno pita radio za stanje: ako je glavna petlja zaglavljena, odgovor nece stici
                // i nadzor ce to videti kao zamrznut proces.
                _ = _radio.GetState();
                return IpcMessage.Success(command.Id);

            case "pause":
                _radio.Pause();
                return IpcMessage.Success(command.Id);

            case "play":
                _radio.Resume();
                return IpcMessage.Success(command.Id);

            case "next":
                args.TryGetValue("track", out var expected);
                return IpcMessage.Success(command.Id, new() { ["skipped"] = _radio.Next(expected) ? "true" : "false" });

            case "set-volume":
                if (!args.TryGetValue("value", out var text) ||
                    !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var requested) || !double.IsFinite(requested))
                {
                    return IpcMessage.Failure(command.Id, "bad-argument");
                }

                var (min, max) = VolumeRange(_db);
                var applied = Math.Clamp(requested, min, max);
                _radio.SetVolume(applied);
                return IpcMessage.Success(command.Id, new() { ["value"] = applied.ToString("R", CultureInfo.InvariantCulture) });

            case "set-pin":
                return SetPin(command, args);

            case "login":
                args.TryGetValue("pin", out var pin);
                var login = _sessions.Login(pin);
                return login.Outcome == LoginOutcome.Ok
                    ? IpcMessage.Success(command.Id, new() { ["session"] = login.Token! })
                    : IpcMessage.Failure(command.Id, login.Outcome switch
                    {
                        LoginOutcome.LockedOut => "locked-out",
                        LoginOutcome.NoPinSet => "no-pin",
                        _ => "wrong-pin",
                    }, new() { ["retryAfterSeconds"] = ((int)Math.Ceiling(login.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture) });

            case "logout":
                _sessions.Logout(command.Session);
                return IpcMessage.Success(command.Id);

            case "rescan":
                _radio.Rescan();
                return IpcMessage.Success(command.Id);

            case "set-music-folders":
                var folders = args.Where(a => a.Key.StartsWith("folder", StringComparison.Ordinal))
                    .OrderBy(a => a.Key, StringComparer.Ordinal)
                    .Select(a => a.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList();
                _radio.SetMusicFolders(folders);
                return IpcMessage.Success(command.Id);

            case "set-volume-range":
                if (!args.TryGetValue("min", out var minText) || !args.TryGetValue("max", out var maxText) ||
                    !double.TryParse(minText, NumberStyles.Float, CultureInfo.InvariantCulture, out var newMin) ||
                    !double.TryParse(maxText, NumberStyles.Float, CultureInfo.InvariantCulture, out var newMax) ||
                    newMin < 0 || newMax > 1 || newMin > newMax)
                {
                    return IpcMessage.Failure(command.Id, "bad-argument");
                }

                _db.Settings.Set(VolumeMinKey, newMin.ToString("R", CultureInfo.InvariantCulture));
                _db.Settings.Set(VolumeMaxKey, newMax.ToString("R", CultureInfo.InvariantCulture));
                _radio.SetVolume(Math.Clamp(_radio.GetState().Volume, newMin, newMax));
                return IpcMessage.Success(command.Id);

            default:
                return IpcMessage.Failure(command.Id, "unknown-command");
        }
    }

    /// <summary>Prvo postavljanje PIN-a je slobodno; svaka kasnija promena trazi admin sesiju.</summary>
    private IpcMessage SetPin(IpcMessage command, Dictionary<string, string> args)
    {
        var existing = _db.Settings.Get(PinKey);
        if (!string.IsNullOrEmpty(existing) && !_sessions.Validate(command.Session))
        {
            return IpcMessage.Failure(command.Id, "admin-required");
        }

        if (!args.TryGetValue("pin", out var pin) || !PinVault.IsValidPin(pin))
        {
            return IpcMessage.Failure(command.Id, "bad-pin");
        }

        _db.Settings.Set(PinKey, PinVault.Hash(pin));
        _sessions.InvalidateAll();
        return IpcMessage.Success(command.Id);
    }

    private static double Parse(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 1 ? value : fallback;
}
