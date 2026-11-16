using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LustreRadio.Core.History;
using LustreRadio.Core.Licensing;
using LustreRadio.Core.Rotation;
using Microsoft.Data.Sqlite;

namespace LustreRadio.Data;

/// <summary>Pesma kako je zapisana u bazi.</summary>
public sealed record TrackRecord
{
    public long Id { get; init; }

    public required string Path { get; init; }

    public required string ContentHash { get; init; }

    public long SizeBytes { get; init; }

    public DateTime LastWriteUtc { get; init; }

    public required string Title { get; init; }

    /// <summary>Prazno ako izvodjac nije poznat.</summary>
    public string Artist { get; init; } = string.Empty;

    public string? Album { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public string Kind { get; init; } = string.Empty;

    public string? DecoderId { get; init; }

    public bool DecodeOk { get; init; }

    public string? DecodeError { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    /// <summary>Tacna duzina u frejmovima (izmerena dekodiranjem, u sample rate-u fajla).</summary>
    public long ExactFrames { get; init; }

    public double DurationSeconds { get; init; }

    public double? PeakDbfs { get; init; }

    public double? Lufs { get; init; }

    public double? Bpm { get; init; }

    public bool Favorite { get; init; }

    public bool Blocked { get; init; }

    public int FaultCount { get; init; }

    /// <summary>Fajl vise nije na disku.</summary>
    public bool Missing { get; init; }

    public DateTime AddedUtc { get; init; }
}

public sealed class TrackStore
{
    private readonly LustreDatabase _db;

    internal TrackStore(LustreDatabase db) => _db = db;

    /// <summary>
    /// Upisuje ili osvezava pesmu po putanji. Oznake "omiljena" i "blokirana" i datum dodavanja se cuvaju.
    /// Ako se sadrzaj fajla promenio, brisu se rezultati analize i brojac gresaka.
    /// </summary>
    public long Upsert(TrackRecord track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return _db.Scalar<long>(
            """
            INSERT INTO tracks (path, content_hash, size_bytes, last_write_utc, title, artist, album, genres, kind, decoder_id,
                                decode_ok, decode_error, sample_rate, channels, exact_frames, duration_seconds, peak_dbfs, lufs, added_utc)
            VALUES ($path, $hash, $size, $write, $title, $artist, $album, $genres, $kind, $decoder,
                    $ok, $error, $rate, $channels, $frames, $duration, $peak, $lufs, $added)
            ON CONFLICT(path) DO UPDATE SET
                lufs = COALESCE(excluded.lufs, CASE WHEN tracks.content_hash = excluded.content_hash THEN tracks.lufs ELSE NULL END),
                bpm = CASE WHEN tracks.content_hash = excluded.content_hash THEN tracks.bpm ELSE NULL END,
                fault_count = CASE WHEN tracks.content_hash = excluded.content_hash THEN tracks.fault_count ELSE 0 END,
                content_hash = excluded.content_hash, size_bytes = excluded.size_bytes, last_write_utc = excluded.last_write_utc,
                title = excluded.title, artist = excluded.artist, album = excluded.album, genres = excluded.genres,
                kind = excluded.kind, decoder_id = excluded.decoder_id, decode_ok = excluded.decode_ok,
                decode_error = excluded.decode_error, sample_rate = excluded.sample_rate, channels = excluded.channels,
                exact_frames = excluded.exact_frames, duration_seconds = excluded.duration_seconds,
                peak_dbfs = excluded.peak_dbfs, missing = 0
            RETURNING id
            """,
            ("$path", track.Path), ("$hash", track.ContentHash), ("$size", track.SizeBytes), ("$write", Db.Iso(track.LastWriteUtc)),
            ("$title", track.Title), ("$artist", track.Artist), ("$album", track.Album), ("$genres", string.Join('|', track.Genres)),
            ("$kind", track.Kind), ("$decoder", track.DecoderId), ("$ok", track.DecodeOk ? 1 : 0), ("$error", track.DecodeError),
            ("$rate", track.SampleRate), ("$channels", track.Channels), ("$frames", track.ExactFrames),
            ("$duration", track.DurationSeconds), ("$peak", track.PeakDbfs), ("$lufs", track.Lufs),
            ("$added", Db.Iso(track.AddedUtc == default ? DateTime.UtcNow : track.AddedUtc)));
    }

    public TrackRecord? Get(long id) => _db.Query("SELECT * FROM tracks WHERE id = $id", Map, ("$id", id)).FirstOrDefault();

    public TrackRecord? GetByPath(string path) => _db.Query("SELECT * FROM tracks WHERE path = $path", Map, ("$path", path)).FirstOrDefault();

    public IReadOnlyList<TrackRecord> GetAll() => _db.Query("SELECT * FROM tracks ORDER BY id", Map);

    /// <summary>Pesme sa istim sadrzajem (duplikati na razlicitim putanjama).</summary>
    public IReadOnlyList<TrackRecord> FindByHash(string contentHash) =>
        _db.Query("SELECT * FROM tracks WHERE content_hash = $hash ORDER BY id", Map, ("$hash", contentHash));

    public void SetMissing(long id, bool missing) => _db.Execute("UPDATE tracks SET missing = $m WHERE id = $id", ("$m", missing ? 1 : 0), ("$id", id));

    public void SetBlocked(long id, bool blocked) => _db.Execute("UPDATE tracks SET blocked = $b WHERE id = $id", ("$b", blocked ? 1 : 0), ("$id", id));

    public void SetFavorite(long id, bool favorite) => _db.Execute("UPDATE tracks SET favorite = $f WHERE id = $id", ("$f", favorite ? 1 : 0), ("$id", id));

    /// <summary>Belezi gresku pustanja. Vraca novi broj gresaka.</summary>
    public int RecordFault(long id) =>
        (int)_db.Scalar<long>("UPDATE tracks SET fault_count = fault_count + 1 WHERE id = $id RETURNING fault_count", ("$id", id));

    public void ResetFaults(long id) => _db.Execute("UPDATE tracks SET fault_count = 0 WHERE id = $id", ("$id", id));

    public void SetLoudness(long id, double? lufs) => _db.Execute("UPDATE tracks SET lufs = $l WHERE id = $id", ("$l", lufs), ("$id", id));

    public void Remove(long id) => _db.Execute("DELETE FROM tracks WHERE id = $id", ("$id", id));

    private static TrackRecord Map(SqliteDataReader r) => new()
    {
        Id = r.Long("id"),
        Path = r.Text("path")!,
        ContentHash = r.Text("content_hash")!,
        SizeBytes = r.Long("size_bytes"),
        LastWriteUtc = Db.ParseUtc(r.Text("last_write_utc")!),
        Title = r.Text("title")!,
        Artist = r.Text("artist")!,
        Album = r.Text("album"),
        Genres = (r.Text("genres") ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries),
        Kind = r.Text("kind")!,
        DecoderId = r.Text("decoder_id"),
        DecodeOk = r.Flag("decode_ok"),
        DecodeError = r.Text("decode_error"),
        SampleRate = (int)r.Long("sample_rate"),
        Channels = (int)r.Long("channels"),
        ExactFrames = r.Long("exact_frames"),
        DurationSeconds = r.Real("duration_seconds") ?? 0,
        PeakDbfs = r.Real("peak_dbfs"),
        Lufs = r.Real("lufs"),
        Bpm = r.Real("bpm"),
        Favorite = r.Flag("favorite"),
        Blocked = r.Flag("blocked"),
        FaultCount = (int)r.Long("fault_count"),
        Missing = r.Flag("missing"),
        AddedUtc = Db.ParseUtc(r.Text("added_utc")!),
    };
}

public sealed record LicenseAuditEntry(string ContentHash, DateTime AtUtc, string Actor, LicenseStatus? From, LicenseStatus To, string? Note);

public sealed class LicenseStore
{
    private readonly LustreDatabase _db;

    internal LicenseStore(LustreDatabase db) => _db = db;

    public LicenseRecord? Get(string contentHash) =>
        _db.Query("SELECT * FROM licenses WHERE content_hash = $h", Map, ("$h", contentHash)).FirstOrDefault();

    public IReadOnlyDictionary<string, LicenseRecord> GetAll() =>
        _db.Query("SELECT * FROM licenses", Map).ToDictionary(l => l.ContentHash, StringComparer.OrdinalIgnoreCase);

    /// <summary>Svaki novi fajl dobija evidenciju sa statusom "Uvezeno (neprovereno)".</summary>
    public LicenseRecord EnsureImported(string contentHash, string actor)
    {
        var existing = Get(contentHash);
        if (existing is not null)
        {
            return existing;
        }

        var created = new LicenseRecord { ContentHash = contentHash };
        Save(created, actor, "uvoz");
        return created;
    }

    /// <summary>
    /// Cuva evidenciju. Promena statusa mora biti dozvoljena, a status "Provereno" prolazi
    /// samo ako nema nijedne prepreke; svaka promena statusa se upisuje u dnevnik.
    /// </summary>
    public void Save(LicenseRecord license, string actor, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(license);
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("Mora se znati ko menja licencu.", nameof(actor));
        }

        var existing = Get(license.ContentHash);
        if (existing is null && license.Status != LicenseStatus.Imported)
        {
            throw new InvalidOperationException("Nova evidencija mora poceti statusom 'Imported'.");
        }

        if (existing is not null && existing.Status != license.Status && !LicenseGate.IsTransitionAllowed(existing.Status, license.Status))
        {
            throw new InvalidOperationException($"Promena statusa '{existing.Status}' -> '{license.Status}' nije dozvoljena.");
        }

        if (license.Status == LicenseStatus.Verified)
        {
            var blockers = LicenseGate.VerificationBlockers(license);
            if (blockers.Count > 0 || string.IsNullOrWhiteSpace(license.VerifiedBy) || license.VerifiedAt is null)
            {
                throw new InvalidOperationException("Status 'Verified' nije dozvoljen: " + string.Join(" ", blockers));
            }
        }

        _db.InTransaction(() =>
        {
            _db.Execute(
                """
                INSERT INTO licenses (content_hash, status, source, source_url, author, license_type, downloaded_on, commercial_use,
                                      public_performance, modifications, attribution_required, has_proof, has_written_permission,
                                      verified_by, verified_at)
                VALUES ($h, $status, $source, $url, $author, $type, $downloaded, $commercial, $public, $mods, $attribution, $proof,
                        $permission, $by, $at)
                ON CONFLICT(content_hash) DO UPDATE SET
                    status = excluded.status, source = excluded.source, source_url = excluded.source_url, author = excluded.author,
                    license_type = excluded.license_type, downloaded_on = excluded.downloaded_on,
                    commercial_use = excluded.commercial_use, public_performance = excluded.public_performance,
                    modifications = excluded.modifications, attribution_required = excluded.attribution_required,
                    has_proof = excluded.has_proof, has_written_permission = excluded.has_written_permission,
                    verified_by = excluded.verified_by, verified_at = excluded.verified_at
                """,
                ("$h", license.ContentHash), ("$status", license.Status.ToString()), ("$source", license.Source),
                ("$url", license.SourceUrl), ("$author", license.Author), ("$type", license.LicenseType),
                ("$downloaded", license.DownloadedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$commercial", license.CommercialUse.ToString()), ("$public", license.PublicPerformance.ToString()),
                ("$mods", license.Modifications.ToString()), ("$attribution", license.AttributionRequired.ToString()),
                ("$proof", license.HasProof ? 1 : 0), ("$permission", license.HasWrittenPermission ? 1 : 0),
                ("$by", license.VerifiedBy), ("$at", license.VerifiedAt?.ToString("O", CultureInfo.InvariantCulture)));

            if (existing is null || existing.Status != license.Status)
            {
                _db.Execute(
                    "INSERT INTO license_audit (content_hash, at_utc, actor, from_status, to_status, note) VALUES ($h, $at, $actor, $from, $to, $note)",
                    ("$h", license.ContentHash), ("$at", Db.Iso(DateTime.UtcNow)), ("$actor", actor),
                    ("$from", existing?.Status.ToString()), ("$to", license.Status.ToString()), ("$note", note));
            }
        });
    }

    /// <summary>Dodeljuje status "Provereno" kroz kapiju licenci.</summary>
    public LicenseRecord Verify(string contentHash, string actor, DateTimeOffset at)
    {
        var license = Get(contentHash) ?? throw new InvalidOperationException("Nema evidencije licence.");
        var verified = LicenseGate.Verify(license, actor, at);
        Save(verified, actor, "potvrda");
        return verified;
    }

    /// <summary>
    /// Potvrdjuje pesmu na osnovu izjave vlasnika o kolektivnoj licenci. Vec proverene pesme se ne diraju.
    /// Vraca true ako je status ovim postao "Provereno".
    /// </summary>
    public bool VerifyCollective(string contentHash, CollectiveLicense declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var license = EnsureImported(contentHash, declaration.DeclaredBy);
        if (license.Status == LicenseStatus.Verified)
        {
            return false;
        }

        if (license.Status != LicenseStatus.UnderReview)
        {
            license = license with { Status = LicenseStatus.UnderReview };
            Save(license, declaration.DeclaredBy, "kolektivna licenca");
        }

        Save(
            license with
            {
                Source = declaration.Organizations,
                LicenseType = LicenseGate.CollectiveType,
                CommercialUse = Answer.Yes,
                PublicPerformance = Answer.Yes,
                HasProof = !string.IsNullOrWhiteSpace(declaration.ProofPath),
            },
            declaration.DeclaredBy,
            "kolektivna licenca");
        Verify(contentHash, declaration.DeclaredBy, declaration.DeclaredAt);
        return true;
    }

    /// <summary>Putanja do sacuvanog dokaza (PDF ili snimak ekrana).</summary>
    public void AttachProof(string contentHash, string proofPath, string actor)
    {
        var license = Get(contentHash) ?? throw new InvalidOperationException("Nema evidencije licence.");
        Save(license with { HasProof = true }, actor);
        _db.Execute("UPDATE licenses SET proof_path = $p WHERE content_hash = $h", ("$p", proofPath), ("$h", contentHash));
    }

    public string? ProofPath(string contentHash) =>
        _db.Scalar<string>("SELECT proof_path FROM licenses WHERE content_hash = $h", ("$h", contentHash));

    public IReadOnlyList<LicenseAuditEntry> Audit(string contentHash) => _db.Query(
        "SELECT * FROM license_audit WHERE content_hash = $h ORDER BY id",
        r => new LicenseAuditEntry(
            r.Text("content_hash")!,
            Db.ParseUtc(r.Text("at_utc")!),
            r.Text("actor")!,
            r.Text("from_status") is { } from ? Enum.Parse<LicenseStatus>(from) : null,
            Enum.Parse<LicenseStatus>(r.Text("to_status")!),
            r.Text("note")),
        ("$h", contentHash));

    private static LicenseRecord Map(SqliteDataReader r) => new()
    {
        ContentHash = r.Text("content_hash")!,
        Status = Enum.Parse<LicenseStatus>(r.Text("status")!),
        Source = r.Text("source"),
        SourceUrl = r.Text("source_url"),
        Author = r.Text("author"),
        LicenseType = r.Text("license_type"),
        DownloadedOn = r.Text("downloaded_on") is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        CommercialUse = Enum.Parse<Answer>(r.Text("commercial_use")!),
        PublicPerformance = Enum.Parse<Answer>(r.Text("public_performance")!),
        Modifications = Enum.Parse<Answer>(r.Text("modifications")!),
        AttributionRequired = Enum.Parse<Answer>(r.Text("attribution_required")!),
        HasProof = r.Flag("has_proof"),
        HasWrittenPermission = r.Flag("has_written_permission"),
        VerifiedBy = r.Text("verified_by"),
        VerifiedAt = r.Text("verified_at") is { } at ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
    };
}

public sealed record PlayRow
{
    public long Id { get; init; }

    public required long TrackId { get; init; }

    public required DateTime StartedUtc { get; init; }

    /// <summary>Odakle je pesma dosla: raspored, rucni izbor, rezerva...</summary>
    public required string Source { get; init; }

    public string? Part { get; init; }

    public string? Program { get; init; }

    public string? Tier { get; init; }

    public bool EndedEarly { get; init; }

    /// <summary>Status iz seme 2: odsvirana, prekinuta, nepotvrdjena-a/b/c, stari_zapis. Svaki upit vraca i status (B-162).</summary>
    public string Status { get; init; } = "stari_zapis";

    public string? StopReason { get; init; }

    public string? PlaybackId { get; init; }

    public bool TransitionReached { get; init; }

    public bool Faulted { get; init; }
}

public sealed class HistoryStore
{
    private readonly LustreDatabase _db;

    internal HistoryStore(LustreDatabase db) => _db = db;

    /// <summary>
    /// Upis po starom nacinu (u trenutku pocetka, bez pouzdanog ishoda). Do koraka B2 status je "stari_zapis";
    /// u B2 istoriju preuzima dnevnik emitovanja.
    /// </summary>
    public long Add(PlayRow play)
    {
        ArgumentNullException.ThrowIfNull(play);
        return _db.Scalar<long>(
            """
            INSERT INTO play_history (track_id, started_utc, source, part, program, tier, ended_early, faulted, status)
            VALUES ($track, $started, $source, $part, $program, $tier, $early, $faulted, 'stari_zapis') RETURNING id
            """,
            ("$track", play.TrackId), ("$started", Db.Iso(play.StartedUtc)), ("$source", play.Source), ("$part", play.Part),
            ("$program", play.Program), ("$tier", play.Tier), ("$early", play.EndedEarly ? 1 : 0), ("$faulted", play.Faulted ? 1 : 0));
    }

    public void MarkOutcome(long id, bool endedEarly, bool faulted) => _db.Execute(
        "UPDATE play_history SET ended_early = $e, faulted = $f WHERE id = $id", ("$e", endedEarly ? 1 : 0), ("$f", faulted ? 1 : 0), ("$id", id));

    /// <summary>Pustanja od zadatog trenutka, najstarije prvo.</summary>
    public IReadOnlyList<PlayRow> GetSince(DateTime sinceUtc) => _db.Query(
        "SELECT * FROM play_history WHERE started_utc >= $since ORDER BY started_utc, id",
        r => new PlayRow
        {
            Id = r.Long("id"),
            TrackId = r.Long("track_id"),
            StartedUtc = Db.ParseUtc(r.Text("started_utc")!),
            Source = r.Text("source")!,
            Part = r.Text("part"),
            Program = r.Text("program"),
            Tier = r.Text("tier"),
            EndedEarly = r.Flag("ended_early"),
            Faulted = r.Flag("faulted"),
            Status = r.Text("status") ?? "stari_zapis",
            StopReason = r.Text("stop_reason"),
            PlaybackId = r.Text("playback_id"),
            TransitionReached = r.Text("transition_reached") == "1",
        },
        ("$since", Db.Iso(sinceUtc)));

    /// <summary>
    /// Upis konacnog ishoda jednog emitovanja, jednom ("ubaci ili preskoci" po playback_id). Vraca true ako je red upisan.
    /// </summary>
    public bool InsertOutcome(PlaybackOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var stopped = outcome.Status == PlaybackStatus.Stopped;
        return _db.Execute(
            """
            INSERT INTO play_history (track_id, started_utc, source, part, program, tier, ended_early, faulted,
                                      playback_id, status, stop_reason, transition_reached, last_rid)
            VALUES ($track, $started, $source, $part, $program, $tier, $early, $faulted, $pid, $status, $reason, $transition, $rid)
            ON CONFLICT(playback_id) DO NOTHING
            """,
            ("$track", outcome.TrackId), ("$started", Db.Iso(outcome.StartedUtc)),
            ("$source", Names.Source(outcome.Source)), ("$part", Names.Part(outcome.Part)), ("$program", outcome.Program),
            ("$tier", Names.Tier(outcome.Tier)), ("$early", stopped ? 1 : 0),
            ("$faulted", stopped && outcome.Reason == Core.History.StopReason.FileError ? 1 : 0),
            ("$pid", outcome.PlaybackId.ToString()), ("$status", outcome.StatusText),
            ("$reason", stopped ? JournalRecord.ReasonName(outcome.Reason) : null),
            ("$transition", outcome.TransitionReached ? 1 : 0), ("$rid", outcome.LastRid)) == 1;
    }

    /// <summary>Svi playback_id koji vec imaju red u bazi (za oporavak pri pokretanju).</summary>
    public HashSet<string> PlaybackIds() =>
        [.. _db.Query("SELECT playback_id FROM play_history WHERE playback_id IS NOT NULL", r => r.Text("playback_id")!)];

    /// <summary>Broj redova po statusu, odvojeno (B-162, B-168). "Odsvirane" sadrze samo POTVRDJENO – ODSVIRANA.</summary>
    public HistoryCounts CountByStatus(DateTime fromUtc, DateTime toUtc)
    {
        var rows = _db.Query(
            "SELECT status, count(*) AS n FROM play_history WHERE started_utc >= $from AND started_utc < $to GROUP BY status",
            r => (Status: r.Text("status") ?? "stari_zapis", Count: r.Long("n")),
            ("$from", Db.Iso(fromUtc)), ("$to", Db.Iso(toUtc)));
        return new HistoryCounts(
            rows.Where(x => x.Status == "odsvirana").Sum(x => x.Count),
            rows.Where(x => x.Status == "prekinuta").Sum(x => x.Count),
            rows.Where(x => x.Status.StartsWith("nepotvrdjena", StringComparison.Ordinal)).Sum(x => x.Count),
            rows.Where(x => x.Status == "stari_zapis").Sum(x => x.Count));
    }

    /// <summary>Kratki brojevi iz zapisa crne kutije pretvoreni nazad u nazive (izvor, deo dana, nivo).</summary>
    public static class Names
    {
        public static string Source(byte value) => value == 0 ? "nepoznato" : ((Core.Scheduling.ProgramSource)(value - 1)).ToString();

        public static string? Part(byte value) => value == 0 ? null : ((Core.Scheduling.DayPart)(value - 1)).ToString();

        public static string? Tier(byte value) => value == 0 ? null : ((Core.Rotation.MatchTier)(value - 1)).ToString();
    }
}

/// <summary>Cetiri odvojena broja; ne sabiraju se osim kad izvestaj to izricito kaze.</summary>
public sealed record HistoryCounts(long Played, long Stopped, long Unconfirmed, long Legacy);

/// <summary>Jednostavna tabela kljuc-vrednost; slozene vrednosti se cuvaju kao JSON.</summary>
public sealed class KeyValueStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly LustreDatabase _db;
    private readonly string _table;

    internal KeyValueStore(LustreDatabase db, string table)
    {
        _db = db;
        _table = table;
    }

    public string? Get(string key) => _db.Scalar<string>($"SELECT value FROM {_table} WHERE key = $k", ("$k", key));

    public void Set(string key, string value) => _db.Execute(
        $"INSERT INTO {_table} (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    public void Remove(string key) => _db.Execute($"DELETE FROM {_table} WHERE key = $k", ("$k", key));

    public T? GetJson<T>(string key)
        where T : class
    {
        var text = Get(key);
        return text is null ? null : JsonSerializer.Deserialize<T>(text, Json);
    }

    public void SetJson<T>(string key, T value)
        where T : class => Set(key, JsonSerializer.Serialize(value, Json));
}

/// <summary>Pretvara zapise iz baze u ono sto rotacija koristi, uz odgovor kapije licenci za svaku pesmu.</summary>
public static class TrackCatalog
{
    public const int QuarantineAfterFaults = 3;

    public static IReadOnlyList<TrackInfo> Build(IEnumerable<TrackRecord> tracks, IReadOnlyDictionary<string, LicenseRecord> licenses)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(licenses);
        var result = new List<TrackInfo>();
        foreach (var track in tracks)
        {
            licenses.TryGetValue(track.ContentHash, out var license);
            result.Add(new TrackInfo
            {
                Id = track.Id.ToString(CultureInfo.InvariantCulture),

                // nepoznat izvodjac ne sme da se racuna kao "isti izvodjac" za sve takve pesme
                Artist = string.IsNullOrWhiteSpace(track.Artist) ? "?" + track.Id.ToString(CultureInfo.InvariantCulture) : track.Artist,
                Genres = track.Genres,
                Bpm = track.Bpm,
                DurationSeconds = track.DurationSeconds > 0 ? track.DurationSeconds : 240,
                Blocked = track.Blocked,
                Broadcastable = track.DecodeOk && !track.Missing && track.FaultCount < QuarantineAfterFaults &&
                                LicenseGate.CanBroadcast(license, track.ContentHash).Allowed,
            });
        }

        return result;
    }
}
