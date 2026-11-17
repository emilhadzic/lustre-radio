using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LustreRadio.Ipc;

/// <summary>Jedna najava, kako je vidi admin.</summary>
public sealed record VoiceItem
{
    public string Id { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    /// <summary>Ident, Welcome, Long, Seasonal, Holiday, Event.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Serbian ili English.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Female ili Male.</summary>
    public string Voice { get; init; } = string.Empty;

    /// <summary>Draft, Approved, Archived.</summary>
    public string Status { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public bool LicenseComplete { get; init; }

    /// <summary>Sme u eter (odobrena, ukljucena, licenca potpuna, fajl isti).</summary>
    public bool OnAir { get; init; }

    public double DurationSeconds { get; init; }
}

/// <summary>Stanje radija koje interfejs prikazuje. Audio proces je jedini izvor istine.</summary>
public sealed record RadioState
{
    /// <summary>Emitovanje je u toku (unutar radnog vremena i ima muzike).</summary>
    public bool OnAir { get; init; }

    public bool Paused { get; init; }

    public string? TrackId { get; init; }

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public double PositionSeconds { get; init; }

    public double DurationSeconds { get; init; }

    public string? Program { get; init; }

    public string? DayPart { get; init; }

    /// <summary>Glasnoca koju vidi zaposleni, 0-1.</summary>
    public double Volume { get; init; }

    public double VolumeMin { get; init; }

    public double VolumeMax { get; init; }

    /// <summary>Stanje zvucnog izlaza: Starting, Playing, Disconnected, Finished.</summary>
    public string? Output { get; init; }

    public string? Device { get; init; }

    /// <summary>Koliko pesama trenutno sme u eter.</summary>
    public int BroadcastableTracks { get; init; }

    /// <summary>PIN je postavljen (inace interfejs prvo trazi postavljanje).</summary>
    public bool PinIsSet { get; init; }

    public DateTime AsOfUtc { get; init; }

    /// <summary>Brojac pripremljenog zvuka u tekucem emitovanju (za nadzor: dok svira, mora da raste).</summary>
    public long FramesRendered { get; init; }

    /// <summary>ID procesa audio procesa.</summary>
    public int ProcessId { get; init; }

    /// <summary>Radi u sigurnom rezimu (posle vise padova).</summary>
    public bool SafeMode { get; init; }

    /// <summary>Kada se danas zatvara (lokalno vreme), dok radi.</summary>
    public DateTime? ClosesAtLocal { get; init; }

    /// <summary>Kada se sledeci put otvara (lokalno vreme), dok ne radi.</summary>
    public DateTime? OpensAtLocal { get; init; }

    /// <summary>Lokalno vreme audio procesa u trenutku slanja.</summary>
    public DateTime NowLocal { get; init; }

    public List<string> MusicFolders { get; init; } = [];

    /// <summary>Ukupno pesama u biblioteci (i onih koje ne smeju u eter).</summary>
    public int TotalTracks { get; init; }

    /// <summary>Skeniranje biblioteke je u toku.</summary>
    public bool Scanning { get; init; }

    /// <summary>Jezik ekrana za zaposlene: sr ili en.</summary>
    public string StaffLanguage { get; init; } = "sr";

    /// <summary>Jezik admin dela: sr ili en.</summary>
    public string AdminLanguage { get; init; } = "sr";

    public string? EngineVersion { get; init; }

    /// <summary>Organizacije iz izjave vlasnika o kolektivnoj licenci (npr. "SOKOJ, OFPS"); null ako izjave nema.</summary>
    public string? LicenseOrganizations { get; init; }

    /// <summary>Najave u biblioteci (admin, Glas).</summary>
    public List<VoiceItem> VoiceMessages { get; init; } = [];

    public DateTime? LicenseDeclaredLocal { get; init; }

    public const string DatabaseNormal = "normal";

    public const string DatabaseNotUpgraded = "bez-nadogradnje";

    /// <summary>"normal" ili "bez-nadogradnje" (baza nije nadogradjena; izmene su iskljucene).</summary>
    public string DatabaseMode { get; init; } = DatabaseNormal;

    public string? DatabaseModeReason { get; init; }

    /// <summary>Istorija (B2): crna kutija je fajl mapiran u memoriju.</summary>
    public bool HistoryBlackBoxAvailable { get; init; } = true;

    /// <summary>Dnevnik emitovanja trenutno ne moze da se pise.</summary>
    public bool HistoryNotSaved { get; init; }

    /// <summary>Broj izgubljenih dogadjaja (crna kutija je bila puna), sa vremenom od–do.</summary>
    public long HistoryLost { get; init; }

    public DateTime? HistoryLostFromLocal { get; init; }

    public DateTime? HistoryLostToLocal { get; init; }

    /// <summary>Neispravni redovi u dnevniku, zateceni pri pokretanju.</summary>
    public int HistoryInvalidLines { get; init; }

    /// <summary>Na disku dnevnika je manje od 200 MB.</summary>
    public bool HistoryLowSpace { get; init; }

    /// <summary>Zvuk ceka zbog zastoja (B3): "ceka-disk", "ceka-procesor", "uzrok-nije-utvrdjen"; null kad ne ceka.</summary>
    public string? AudioStall { get; init; }

    /// <summary>Sledeca pesma iz plana (B4); null kad plana nema (nista se ne izmislja).</summary>
    public string? NextTitle { get; init; }

    public string? NextArtist { get; init; }

    /// <summary>Stvarni broj stavki u planu (0–3) i razlog kad ih je manje od tri.</summary>
    public int PlannedCount { get; init; }

    public string? PlanReason { get; init; }
}

/// <summary>Jedna poruka izmedju interfejsa i audio procesa.</summary>
public sealed record IpcMessage
{
    /// <summary>hello | cmd | res | state</summary>
    public required string Type { get; init; }

    public int? Protocol { get; init; }

    /// <summary>ID komande; ista komanda poslata dvaput izvrsava se jednom.</summary>
    public string? Id { get; init; }

    public string? Name { get; init; }

    public Dictionary<string, string>? Args { get; init; }

    /// <summary>Admin sesija (za admin komande).</summary>
    public string? Session { get; init; }

    public bool? Ok { get; init; }

    public string? Error { get; init; }

    public Dictionary<string, string>? Data { get; init; }

    public long? Seq { get; init; }

    public RadioState? State { get; init; }

    public static IpcMessage Success(string? id, Dictionary<string, string>? data = null) =>
        new() { Type = "res", Id = id, Ok = true, Data = data };

    public static IpcMessage Failure(string? id, string error, Dictionary<string, string>? data = null) =>
        new() { Type = "res", Id = id, Ok = false, Error = error, Data = data };
}

/// <summary>Obradjuje komande. Poziva se sa jedne niti u jednom trenutku; nikad sa niti koja pusta zvuk.</summary>
public interface IIpcHandler
{
    IpcMessage Handle(IpcMessage command);

    RadioState CurrentState();
}

public sealed class IpcProtocolException(string message) : Exception(message);

/// <summary>
/// Okvir poruke: 4 bajta duzine (little-endian) + JSON u UTF-8. Lokalni imenovani kanal;
/// ne koristi mrezu (spec. 2.6).
/// </summary>
public static class IpcProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 1 << 20;
    public const string DefaultPipeName = "LustreRadio.Engine.v1";

    /// <summary>Okvir je stigao, ali sadrzaj nije ispravna poruka.</summary>
    public const string InvalidType = "invalid";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] Encode(IpcMessage message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    public static async Task WriteAsync(Stream stream, IpcMessage message, CancellationToken cancel)
    {
        await stream.WriteAsync(Encode(message), cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Cita jednu poruku. null = druga strana je zatvorila vezu. Neispravna duzina baca
    /// InvalidDataException (veza se mora zatvoriti); neispravan JSON vraca poruku tipa "invalid".
    /// </summary>
    public static async Task<IpcMessage?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancel).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException($"Neispravna duzina poruke: {length}.");
        }

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, cancel).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var message = JsonSerializer.Deserialize<IpcMessage>(payload, Json);
            return message is { Type.Length: > 0 } ? message : new IpcMessage { Type = InvalidType };
        }
        catch (JsonException)
        {
            return new IpcMessage { Type = InvalidType };
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancel).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
