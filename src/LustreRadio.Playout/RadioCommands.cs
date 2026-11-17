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

    /// <summary>Podesavanje je promenjeno; posalji novo stanje svim interfejsima.</summary>
    void StateChanged();

    /// <summary>Biblioteka najava; null ako je nema.</summary>
    VoiceLibrary? Voice => null;
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

    public const string StaffLanguageKey = "language-staff";
    public const string AdminLanguageKey = "language-admin";

    private static readonly HashSet<string> AdminCommands = ["rescan", "set-music-folders", "set-volume-range", "logout", "set-language", "touch", "declare-collective-license",
        "voice-import", "voice-approve", "voice-enable", "voice-disable", "voice-archive", "voice-restore"];

    /// <summary>Komande koje menjaju bazu: dok baza nije nadogradjena, odbijaju se (plan faze B, 2.3).</summary>
    public static readonly IReadOnlySet<string> BlockedWithoutUpgrade = new HashSet<string> { "rescan", "set-music-folders", "set-volume-range", "set-language", "declare-collective-license", "set-pin" };

    public const string DatabaseNotUpgradedError = "db-not-upgraded";

    private readonly IRadioControl _radio;
    private readonly LustreDatabase _db;
    private readonly AdminSessions _sessions;
    private readonly Action<string>? _log;
    private long _lastVolumeLog = long.MinValue / 2;

    public RadioCommandHandler(IRadioControl radio, LustreDatabase db, AdminSessions sessions, Action<string>? log = null)
    {
        _log = log;
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

    /// <summary>
    /// Izvrsava komandu i belezi je u dnevnik (da se proba moze proveriti i bez pitanja ko je sta pritisnuo).
    /// PIN i ostali argumenti se nikad ne upisuju; upiti stanja i produzavanje sesije se ne beleze.
    /// </summary>
    public IpcMessage Handle(IpcMessage command)
    {
        var response = Execute(command);
        var name = command.Name ?? string.Empty;
        if (_log is not null && name is not ("get-state" or "touch") && !IsVolumeRepeat(name))
        {
            var detail = name switch
            {
                "set-volume" when command.Args?.GetValueOrDefault("value") is { } value => " " + value,
                "set-language" => $" {command.Args?.GetValueOrDefault("screen")}={command.Args?.GetValueOrDefault("lang")}",
                _ => string.Empty,
            };
            _log($"Komanda: {name}{detail} -> {(response.Ok == true ? "ok" : response.Error)}");
        }

        return response;
    }

    /// <summary>Dok se klizac glasnoce vuce stize mnogo komandi: u dnevnik ide najvise jedna u sekundi.</summary>
    private bool IsVolumeRepeat(string name)
    {
        if (name != "set-volume")
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now - _lastVolumeLog < 1000)
        {
            return true;
        }

        _lastVolumeLog = now;
        return false;
    }

    private IpcMessage Execute(IpcMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var name = command.Name ?? string.Empty;
        var args = command.Args ?? new Dictionary<string, string>();

        if (AdminCommands.Contains(name) && !_sessions.Validate(command.Session))
        {
            return IpcMessage.Failure(command.Id, "admin-required");
        }

        if (_db.IsDetachedCopy && BlockedWithoutUpgrade.Contains(name))
        {
            return IpcMessage.Failure(command.Id, DatabaseNotUpgradedError);
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

            case "touch":
                // produzava admin sesiju dok admin nesto radi na ekranu (Validate iznad je vec to uradio)
                return IpcMessage.Success(command.Id);

            case "declare-collective-license":
                // Izjava vlasnika da placa naknadu organizacijama (odluka od 14.11.2026.). Belezi se ko i kada.
                if (!args.TryGetValue("organizations", out var organizations) || string.IsNullOrWhiteSpace(organizations) || organizations.Length > 200)
                {
                    return IpcMessage.Failure(command.Id, "bad-argument");
                }

                _db.Settings.SetJson(LibraryIndexer.CollectiveLicenseKey, new Core.Licensing.CollectiveLicense
                {
                    Organizations = organizations.Trim(),
                    DeclaredBy = "admin (PIN)",
                    DeclaredAt = DateTimeOffset.Now,
                    Note = "Izjava data u podesavanjima programa.",
                });
                _radio.Rescan();
                return IpcMessage.Success(command.Id);

            case "set-language":
                args.TryGetValue("screen", out var screen);
                args.TryGetValue("lang", out var lang);
                if (screen is not ("staff" or "admin") || lang is not ("sr" or "en"))
                {
                    return IpcMessage.Failure(command.Id, "bad-argument");
                }

                _db.Settings.Set(screen == "staff" ? StaffLanguageKey : AdminLanguageKey, lang);
                _radio.StateChanged();
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
                var missing = folders.FirstOrDefault(f => !Path.IsPathFullyQualified(f) || !Directory.Exists(f));
                if (missing is not null)
                {
                    return IpcMessage.Failure(command.Id, "folder-not-found", new() { ["folder"] = missing });
                }

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
                _radio.StateChanged();
                return IpcMessage.Success(command.Id);

            case "voice-import":
                return VoiceImport(command, args);

            case "voice-approve" or "voice-enable" or "voice-disable" or "voice-archive" or "voice-restore":
                if (_radio.Voice is not { } voice || !args.TryGetValue("id", out var voiceId))
                {
                    return IpcMessage.Failure(command.Id, "bad-argument");
                }

                var done = name switch
                {
                    "voice-approve" => voice.Approve(voiceId, "admin (PIN)"),
                    "voice-enable" => voice.SetEnabled(voiceId, true),
                    "voice-disable" => voice.SetEnabled(voiceId, false),
                    "voice-archive" => voice.Archive(voiceId),
                    _ => voice.Restore(voiceId),
                };
                if (!done)
                {
                    return IpcMessage.Failure(command.Id, name == "voice-approve" ? "voice-license-incomplete" : "bad-argument");
                }

                _log?.Invoke($"Najava {voiceId}: {name}.");
                _radio.StateChanged();
                return IpcMessage.Success(command.Id);

            default:
                return IpcMessage.Failure(command.Id, "unknown-command");
        }
    }

    /// <summary>Rucni uvoz najave (fajl generisan u servisu) sa podacima licence; pocinje kao nacrt.</summary>
    private IpcMessage VoiceImport(IpcMessage command, Dictionary<string, string> args)
    {
        if (_radio.Voice is not { } voice || !args.TryGetValue("path", out var path) ||
            !Enum.TryParse<Core.Voice.VoiceCategory>(args.GetValueOrDefault("category"), out var category) ||
            !Enum.TryParse<Core.Voice.VoiceLanguage>(args.GetValueOrDefault("language"), out var language) ||
            !Enum.TryParse<Core.Voice.VoiceKind>(args.GetValueOrDefault("voice"), out var kind))
        {
            return IpcMessage.Failure(command.Id, "bad-argument");
        }

        var license = new VoiceLicense
        {
            Service = args.GetValueOrDefault("service", string.Empty).Trim(),
            ModelAndVoice = args.GetValueOrDefault("model", string.Empty).Trim(),
            Plan = args.GetValueOrDefault("plan", string.Empty).Trim(),
            PaymentProof = args.GetValueOrDefault("payment", string.Empty).Trim(),
            GeneratedOn = args.GetValueOrDefault("generated", string.Empty).Trim(),
            TermsSnapshot = args.GetValueOrDefault("terms", string.Empty).Trim(),
        };
        try
        {
            var entry = voice.Import(path, args.GetValueOrDefault("text", string.Empty), category, language, kind, license, Audio.AudioFormat.Stereo44100);
            _log?.Invoke($"Uvezena najava {entry.Id} ({entry.Category}, {entry.Language}, {entry.DurationSeconds:F1} s).");
            _radio.StateChanged();
            return IpcMessage.Success(command.Id, new() { ["id"] = entry.Id });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            _log?.Invoke($"Uvoz najave nije uspeo: {ex.Message}");
            return IpcMessage.Failure(command.Id, "voice-import-failed");
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
        _radio.StateChanged();
        return IpcMessage.Success(command.Id);
    }

    public static string Language(LustreDatabase db, string key) => db.Settings.Get(key) == "en" ? "en" : "sr";

    private static double Parse(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 1 ? value : fallback;
}
