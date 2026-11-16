using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LustreRadio.Core.History;

namespace LustreRadio.Data;

/// <summary>Kvarovi koje test ubacuje u dnevnik emitovanja. Samo za testove; u programu se ne postavljaju.</summary>
internal sealed class PlayJournalFaults
{
    /// <summary>Pre svakog upisa; izuzetak oponasa disk koji ne moze da se pise ili je pun.</summary>
    public Action? BeforeWrite { get; set; }

    /// <summary>Slobodan prostor na disku umesto stvarnog.</summary>
    public Func<long>? FreeSpace { get; set; }
}

/// <summary>Rezultat citanja dnevnika.</summary>
public sealed record JournalReadResult(IReadOnlyList<JournalRecord> Records, int InvalidLines, bool TornTailTruncated);

/// <summary>
/// <c>journal\emitovanje.jsonl</c> (plan faze B, 2.2 i 4): jedan red po zapisu, sa kontrolnim zbirom.
/// Upis uvek trazi potvrdu diska (<c>FlushFileBuffers</c>) pre nego sto se vrati. Pise ga samo pisar
/// (i oporavak pri pokretanju, pre nego sto pisar krene).
/// </summary>
public sealed class PlayJournal : IDisposable
{
    public const string FileName = "emitovanje.jsonl";
    public const string ArchiveFolder = "arhiva";
    public const long RotateBytes = 1024 * 1024;
    public const long LowSpaceBytes = 200L * 1024 * 1024;
    public static readonly TimeSpan ArchiveRetention = TimeSpan.FromDays(90);

    private const string CrcMarker = ",\"crc\":\"";

    private readonly string _folder;
    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private FileStream? _stream;
    private long _flushes;
    private long _lines;

    public PlayJournal(string folder)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        Directory.CreateDirectory(folder);
    }

    internal PlayJournalFaults? Faults { get; set; }

    public string Path => System.IO.Path.Combine(_folder, FileName);

    /// <summary>Koliko puta je trazena potvrda diska (B-41).</summary>
    public long Flushes => Interlocked.Read(ref _flushes);

    public long LinesWritten => Interlocked.Read(ref _lines);

    public long Length => File.Exists(Path) ? new FileInfo(Path).Length : 0;

    /// <summary>Slobodan prostor na disku dnevnika (u bajtovima), ili -1 ako ne moze da se utvrdi.</summary>
    public long FreeSpace()
    {
        if (Faults?.FreeSpace is { } fake)
        {
            return fake();
        }

        try
        {
            return new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(_folder))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Upisuje zapise i ceka potvrdu diska. Izuzetak znaci da nista od ovoga nije potvrdjeno.</summary>
    public void Append(IReadOnlyList<JournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        Faults?.BeforeWrite?.Invoke();
        _buffer.Clear();
        foreach (var record in records)
        {
            WriteLine(_buffer, record);
        }

        try
        {
            _stream ??= new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _stream.Write(_buffer.WrittenSpan);
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            // Posle greske se fajl ponovo otvara; deo koji je mozda upisan bez potvrde cita se kao presecen ili kao duplikat (isti rid).
            _stream?.Dispose();
            _stream = null;
            throw;
        }

        Interlocked.Increment(ref _flushes);
        Interlocked.Add(ref _lines, records.Count);
    }

    /// <summary>
    /// Cita ceo dnevnik. Neispravan red u sredini se preskace i broji; presecen poslednji red se preskace,
    /// a fajl se skracuje do poslednjeg ispravnog reda.
    /// </summary>
    public JournalReadResult ReadAll()
    {
        CloseStream();
        if (!File.Exists(Path))
        {
            return new JournalReadResult([], 0, false);
        }

        var bytes = File.ReadAllBytes(Path);
        var records = new List<JournalRecord>();
        var invalid = 0;
        var position = 0;
        long lastGoodEnd = 0;
        var torn = false;
        while (position < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', position);
            var end = newline < 0 ? bytes.Length : newline;
            var line = new ReadOnlySpan<byte>(bytes, position, end - position);
            var ok = TryParse(line, out var record);
            if (newline < 0)
            {
                // poslednji red bez kraja reda: upis je presecen
                torn = true;
                break;
            }

            if (ok)
            {
                records.Add(record);
                lastGoodEnd = newline + 1;
            }
            else if (IsLastLine(bytes, newline))
            {
                torn = true;
                break;
            }
            else if (line.Length > 0)
            {
                invalid++;
                lastGoodEnd = newline + 1;
            }

            position = newline + 1;
        }

        if (torn)
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.SetLength(lastGoodEnd);
            stream.Flush(flushToDisk: true);
        }

        return new JournalReadResult(records, invalid, torn);
    }

    /// <summary>
    /// Arhivira dnevnik ako je veci od 1 MB. Pozivalac jamci da svaki playback_id iz fajla ima zavrsni zapis i red u bazi.
    /// Arhive starije od 90 dana se brisu. Vraca putanju arhive ili null.
    /// </summary>
    public string? ArchiveIfLarge(DateTime nowUtc)
    {
        CloseStream();
        var archive = System.IO.Path.Combine(_folder, ArchiveFolder);
        if (Directory.Exists(archive))
        {
            foreach (var old in Directory.EnumerateFiles(archive, "emitovanje-*.jsonl"))
            {
                if (nowUtc - File.GetLastWriteTimeUtc(old) > ArchiveRetention)
                {
                    File.Delete(old);
                }
            }
        }

        if (Length <= RotateBytes)
        {
            return null;
        }

        Directory.CreateDirectory(archive);
        var target = System.IO.Path.Combine(archive, "emitovanje-" + nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
        File.Move(Path, target);
        return target;
    }

    public void Dispose() => CloseStream();

    private void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
    }

    private static bool IsLastLine(byte[] bytes, int newline) => newline == bytes.Length - 1;

    internal static void WriteLine(IBufferWriter<byte> output, in JournalRecord r)
    {
        var body = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(body))
        {
            json.WriteStartObject();
            json.WriteString("rid", r.Rid);
            json.WriteString("t", new DateTime(r.TimeTicks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("vrsta", JournalRecord.KindName(r.Kind));
            if (r.Kind == JournalKind.Crossfade)
            {
                json.WriteString("pid_a", r.PlaybackIdA);
                json.WriteString("pid_b", r.PlaybackId);
                json.WriteNumber("pesma_a", r.TrackA);
                json.WriteNumber("pesma_b", r.Track);
            }
            else if (r.Kind == JournalKind.ProgramCrash)
            {
                json.WriteString("pokretanje", r.PlaybackIdA.ToString("N"));
                json.WriteString("od", new DateTime(r.Frame, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
                json.WriteString("do", new DateTime(r.TimeTicks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
            }
            else if (r.Kind == JournalKind.Lost)
            {
                json.WriteNumber("broj", r.Track);
                json.WriteString("od", new DateTime(r.Frame, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
                json.WriteString("do", new DateTime(r.TimeTicks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
            }
            else if (r.PlaybackId != Guid.Empty)
            {
                json.WriteString("pid", r.PlaybackId);
                json.WriteNumber("pesma", r.Track);
            }

            if (r.Kind is not (JournalKind.ProgramCrash or JournalKind.Lost))
            {
                json.WriteNumber("frejm", r.Frame);
            }

            if (r.PositionMs != 0)
            {
                json.WriteNumber("pozicija_ms", r.PositionMs);
            }

            if (JournalRecord.ReasonName(r.Reason) is { } reason)
            {
                json.WriteString("razlog", reason);
            }

            if (JournalRecord.UnconfirmedName(r.Unconfirmed) is { } sub)
            {
                json.WriteString("podvrsta", sub);
            }

            if (r.Source != 0)
            {
                json.WriteNumber("izvor", r.Source);
            }

            if (r.Part != 0)
            {
                json.WriteNumber("deo", r.Part);
            }

            if (r.Tier != 0)
            {
                json.WriteNumber("nivo", r.Tier);
            }

            if (r.Program is not null)
            {
                json.WriteString("program", r.Program);
            }

            json.WriteEndObject();
        }

        // Kontrolni zbir se racuna nad redom bez zavrsne zagrade, pa se dodaje kao poslednje polje.
        var withoutBrace = body.WrittenSpan[..^1];
        var crc = Crc32.Compute(withoutBrace);
        output.Write(withoutBrace);
        output.Write(Encoding.ASCII.GetBytes(CrcMarker + crc.ToString("x8", CultureInfo.InvariantCulture) + "\"}\n"));
    }

    internal static bool TryParse(ReadOnlySpan<byte> line, out JournalRecord record)
    {
        record = default;
        var marker = line.LastIndexOf(Encoding.ASCII.GetBytes(CrcMarker));
        if (marker <= 0 || line.Length != marker + CrcMarker.Length + 10 || line[^2] != (byte)'"' || line[^1] != (byte)'}')
        {
            return false;
        }

        var hex = Encoding.ASCII.GetString(line.Slice(marker + CrcMarker.Length, 8));
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc) || Crc32.Compute(line[..marker]) != crc)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            var rid = root.GetProperty("rid").GetString()!;
            var colon = rid.IndexOf(':', StringComparison.Ordinal);
            record.Run = Guid.ParseExact(rid[..colon], "N");
            record.Seq = long.Parse(rid[(colon + 1)..], CultureInfo.InvariantCulture);
            record.TimeTicks = DateTime.Parse(root.GetProperty("t").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime().Ticks;
            record.Kind = JournalRecord.ParseKind(root.GetProperty("vrsta").GetString());
            if (record.Kind == JournalKind.None)
            {
                return false;
            }

            if (record.Kind == JournalKind.Crossfade)
            {
                record.PlaybackIdA = root.GetProperty("pid_a").GetGuid();
                record.PlaybackId = root.GetProperty("pid_b").GetGuid();
                record.TrackA = root.GetProperty("pesma_a").GetInt64();
                record.Track = root.GetProperty("pesma_b").GetInt64();
            }
            else if (record.Kind == JournalKind.ProgramCrash)
            {
                record.PlaybackIdA = Guid.ParseExact(root.GetProperty("pokretanje").GetString()!, "N");
                record.Frame = Time(root, "od");
            }
            else if (record.Kind == JournalKind.Lost)
            {
                record.Track = root.GetProperty("broj").GetInt64();
                record.Frame = Time(root, "od");
            }
            else if (root.TryGetProperty("pid", out var pid))
            {
                record.PlaybackId = pid.GetGuid();
                record.Track = root.GetProperty("pesma").GetInt64();
            }

            if (root.TryGetProperty("frejm", out var frame))
            {
                record.Frame = frame.GetInt64();
            }

            record.PositionMs = root.TryGetProperty("pozicija_ms", out var p) ? p.GetInt32() : 0;
            record.Reason = JournalRecord.ParseReason(root.TryGetProperty("razlog", out var r) ? r.GetString() : null);
            record.Unconfirmed = JournalRecord.ParseUnconfirmed(root.TryGetProperty("podvrsta", out var s) ? s.GetString() : null);
            record.Source = root.TryGetProperty("izvor", out var src) ? src.GetByte() : (byte)0;
            record.Part = root.TryGetProperty("deo", out var part) ? part.GetByte() : (byte)0;
            record.Tier = root.TryGetProperty("nivo", out var tier) ? tier.GetByte() : (byte)0;
            record.Program = root.TryGetProperty("program", out var program) ? program.GetString() : null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return false;
        }
    }

    private static long Time(JsonElement root, string name) =>
        DateTime.Parse(root.GetProperty(name).GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime().Ticks;
}
