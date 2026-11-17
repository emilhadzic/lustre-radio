namespace LustreRadio.Data;

internal static class Schema
{
    /// <summary>
    /// Sema 1. Raspored, pravila i ostala podesavanja cuvaju se kao JSON u tabeli settings;
    /// tabele za mikseve, glas i pripremljene prelaze dodaju se narednim semama.
    /// </summary>
    public const string Version1 = """
        CREATE TABLE tracks (
            id              INTEGER PRIMARY KEY,
            path            TEXT NOT NULL UNIQUE,
            content_hash    TEXT NOT NULL,
            size_bytes      INTEGER NOT NULL,
            last_write_utc  TEXT NOT NULL,
            title           TEXT NOT NULL,
            artist          TEXT NOT NULL,
            album           TEXT,
            genres          TEXT NOT NULL DEFAULT '',
            kind            TEXT NOT NULL,
            decoder_id      TEXT,
            decode_ok       INTEGER NOT NULL,
            decode_error    TEXT,
            sample_rate     INTEGER NOT NULL DEFAULT 0,
            channels        INTEGER NOT NULL DEFAULT 0,
            exact_frames    INTEGER NOT NULL DEFAULT 0,
            duration_seconds REAL NOT NULL DEFAULT 0,
            peak_dbfs       REAL,
            lufs            REAL,
            bpm             REAL,
            favorite        INTEGER NOT NULL DEFAULT 0,
            blocked         INTEGER NOT NULL DEFAULT 0,
            fault_count     INTEGER NOT NULL DEFAULT 0,
            missing         INTEGER NOT NULL DEFAULT 0,
            added_utc       TEXT NOT NULL
        );
        CREATE INDEX ix_tracks_hash ON tracks(content_hash);

        CREATE TABLE licenses (
            content_hash        TEXT PRIMARY KEY,
            status              TEXT NOT NULL,
            source              TEXT,
            source_url          TEXT,
            author              TEXT,
            license_type        TEXT,
            downloaded_on       TEXT,
            commercial_use      TEXT NOT NULL,
            public_performance  TEXT NOT NULL,
            modifications       TEXT NOT NULL,
            attribution_required TEXT NOT NULL,
            has_proof           INTEGER NOT NULL,
            proof_path          TEXT,
            has_written_permission INTEGER NOT NULL,
            verified_by         TEXT,
            verified_at         TEXT
        );

        CREATE TABLE license_audit (
            id           INTEGER PRIMARY KEY,
            content_hash TEXT NOT NULL,
            at_utc       TEXT NOT NULL,
            actor        TEXT NOT NULL,
            from_status  TEXT,
            to_status    TEXT NOT NULL,
            note         TEXT
        );

        CREATE TABLE play_history (
            id          INTEGER PRIMARY KEY,
            track_id    INTEGER NOT NULL,
            started_utc TEXT NOT NULL,
            source      TEXT NOT NULL,
            part        TEXT,
            program     TEXT,
            tier        TEXT,
            ended_early INTEGER NOT NULL DEFAULT 0,
            faulted     INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_history_started ON play_history(started_utc);

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE app_state (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    /// <summary>
    /// Sema 2 (faza B, plan v8, odluke N4 i D7-2): pet kolona u istoriji emitovanja.
    /// Postojeci redovi dobijaju status "stari_zapis" i prazan playback_id.
    /// </summary>
    public const string Upgrade1To2 = """
        ALTER TABLE play_history ADD COLUMN playback_id TEXT;
        ALTER TABLE play_history ADD COLUMN status TEXT;
        ALTER TABLE play_history ADD COLUMN stop_reason TEXT;
        ALTER TABLE play_history ADD COLUMN transition_reached INTEGER;
        ALTER TABLE play_history ADD COLUMN last_rid TEXT;
        CREATE UNIQUE INDEX ux_history_playback ON play_history(playback_id);
        UPDATE play_history SET status = 'stari_zapis';
        PRAGMA user_version=2;
        """;

    /// <summary>Kolone za analizu krajeva pesme (Smart Crossfade); dodaju se pri otvaranju ako ne postoje.</summary>
    public static readonly (string Name, string Type)[] AdditiveTrackColumns =
    [
        ("audio_start_frames", "INTEGER"),
        ("audio_end_frames", "INTEGER"),
        ("outro_seconds", "REAL"),
        ("edges_version", "INTEGER NOT NULL DEFAULT 0"),
        ("beat_period_frames", "REAL"),
        ("first_beat_frames", "INTEGER"),
        ("tempo_stable", "INTEGER NOT NULL DEFAULT 0"),
    ];
}
