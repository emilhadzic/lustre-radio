using LustreRadio.Core.Licensing;
using LustreRadio.Core.Scheduling;
using LustreRadio.Data;
using Xunit;

namespace LustreRadio.Data.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lustre-db-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public DatabaseTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "lustre.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // fajl jos zakljucan; privremeni folder ce ocistiti sistem
        }
    }

    private static TrackRecord Track(string path, string hash = "aa11") => new()
    {
        Path = path, ContentHash = hash, SizeBytes = 1234, LastWriteUtc = new DateTime(2026, 11, 13, 19, 0, 0, DateTimeKind.Utc),
        Title = "Naslov", Artist = "Izvodjac", Album = "Album", Genres = ["lounge", "deep house"], Kind = "Mp3", DecoderId = "mf",
        DecodeOk = true, SampleRate = 44100, Channels = 2, ExactFrames = 441000, DurationSeconds = 10, PeakDbfs = -1.5,
    };

    private static LicenseRecord CompleteLicense(string hash) => new()
    {
        ContentHash = hash, Status = LicenseStatus.UnderReview, Source = "Pixabay Music", SourceUrl = "https://example.com/p",
        Author = "Autor", LicenseType = "CC BY 4.0", DownloadedOn = new DateOnly(2026, 11, 13),
        CommercialUse = Answer.Yes, PublicPerformance = Answer.Yes, Modifications = Answer.Yes, AttributionRequired = Answer.Yes, HasProof = true,
    };

    [Fact]
    public void NewDatabase_IsCreatedInWalMode_WithCurrentSchema_AndPassesCheck()
    {
        using var db = LustreDatabase.Open(_path);
        Assert.Equal(LustreDatabase.CurrentSchemaVersion, db.SchemaVersion);
        Assert.Equal("wal", db.JournalMode.ToLowerInvariant());
        Assert.True(db.QuickCheck());
    }

    [Fact]
    public void Reopen_KeepsData_AndMigrationRunsOnlyOnce()
    {
        long id;
        using (var db = LustreDatabase.Open(_path))
        {
            id = db.Tracks.Upsert(Track(@"D:\Muzika\a.mp3"));
        }

        using (var db = LustreDatabase.Open(_path))
        {
            var track = db.Tracks.Get(id);
            Assert.NotNull(track);
            Assert.Equal(@"D:\Muzika\a.mp3", track.Path);
            Assert.Equal(["lounge", "deep house"], track.Genres);
            Assert.Equal(441000, track.ExactFrames);
            Assert.Equal(-1.5, track.PeakDbfs);
            Assert.Equal(new DateTime(2026, 11, 13, 19, 0, 0, DateTimeKind.Utc), track.LastWriteUtc);
            Assert.True(track.DecodeOk);
        }
    }

    [Fact]
    public void Upsert_SamePath_KeepsIdFlagsAndAnalysis_UnlessContentChanged()
    {
        using var db = LustreDatabase.Open(_path);
        var id = db.Tracks.Upsert(Track(@"D:\a.mp3"));
        db.Tracks.SetFavorite(id, true);
        db.Tracks.SetBlocked(id, true);
        db.Tracks.SetLoudness(id, -14.2);
        Assert.Equal(1, db.Tracks.RecordFault(id));
        db.Tracks.SetMissing(id, true);

        Assert.Equal(id, db.Tracks.Upsert(Track(@"D:\a.mp3") with { Title = "Novi naslov" }));
        var same = db.Tracks.Get(id)!;
        Assert.Equal("Novi naslov", same.Title);
        Assert.True(same.Favorite && same.Blocked);
        Assert.Equal(-14.2, same.Lufs);
        Assert.Equal(1, same.FaultCount);
        Assert.False(same.Missing);

        Assert.Equal(id, db.Tracks.Upsert(Track(@"D:\a.mp3", hash: "bb22")));
        var changed = db.Tracks.Get(id)!;
        Assert.Equal("bb22", changed.ContentHash);
        Assert.Null(changed.Lufs);
        Assert.Equal(0, changed.FaultCount);
        Assert.True(changed.Favorite);
        Assert.Single(db.Tracks.GetAll());
    }

    [Fact]
    public void Duplicates_AreFoundByContentHash()
    {
        using var db = LustreDatabase.Open(_path);
        db.Tracks.Upsert(Track(@"D:\a.mp3", "isti"));
        db.Tracks.Upsert(Track(@"D:\kopija\a.mp3", "isti"));
        db.Tracks.Upsert(Track(@"D:\b.mp3", "drugi"));

        Assert.Equal(2, db.Tracks.FindByHash("isti").Count);
        Assert.Single(db.Tracks.FindByHash("drugi"));
        Assert.NotNull(db.Tracks.GetByPath(@"D:\b.mp3"));
        Assert.Null(db.Tracks.GetByPath(@"D:\nema.mp3"));
    }

    [Fact]
    public void License_FullFlow_IsStoredAndAudited()
    {
        using var db = LustreDatabase.Open(_path);
        var imported = db.Licenses.EnsureImported("h1", "skener");
        Assert.Equal(LicenseStatus.Imported, imported.Status);
        Assert.Same(db.Licenses.EnsureImported("h1", "skener").GetType(), imported.GetType());

        db.Licenses.Save(CompleteLicense("h1") with { HasProof = false }, "admin", "popunjeno");
        db.Licenses.AttachProof("h1", @"C:\ProgramData\LustreRadio\licenses\h1.png", "admin");
        var verified = db.Licenses.Verify("h1", "admin", new DateTimeOffset(2026, 11, 13, 21, 0, 0, TimeSpan.FromHours(1)));

        var stored = db.Licenses.Get("h1")!;
        Assert.Equal(LicenseStatus.Verified, stored.Status);
        Assert.Equal(verified, stored);
        Assert.Equal(@"C:\ProgramData\LustreRadio\licenses\h1.png", db.Licenses.ProofPath("h1"));
        Assert.True(LicenseGate.CanBroadcast(stored, "h1").Allowed);

        var audit = db.Licenses.Audit("h1");
        Assert.Equal([LicenseStatus.Imported, LicenseStatus.UnderReview, LicenseStatus.Verified], audit.Select(a => a.To));
        Assert.Equal([null, LicenseStatus.Imported, LicenseStatus.UnderReview], audit.Select(a => a.From));
        Assert.All(audit, a => Assert.False(string.IsNullOrEmpty(a.Actor)));
    }

    [Fact] // T-LIC-01 za bazu: status "Provereno" ne moze da se upise mimo pravila
    public void Verified_CannotBeWrittenAroundTheRules()
    {
        using var db = LustreDatabase.Open(_path);
        var verifiedNow = CompleteLicense("h1") with { Status = LicenseStatus.Verified, VerifiedBy = "admin", VerifiedAt = DateTimeOffset.Now };

        // nova evidencija ne moze odmah biti proverena
        Assert.Throws<InvalidOperationException>(() => db.Licenses.Save(verifiedNow, "admin"));

        // iz "Uvezeno" se ne moze preskociti u "Provereno"
        db.Licenses.EnsureImported("h1", "skener");
        Assert.Throws<InvalidOperationException>(() => db.Licenses.Save(verifiedNow, "admin"));
        Assert.Throws<InvalidOperationException>(() => db.Licenses.Verify("h1", "admin", DateTimeOffset.Now));

        // na proveri, ali nepotpuna: ne moze
        db.Licenses.Save(CompleteLicense("h1") with { PublicPerformance = Answer.Unknown }, "admin");
        Assert.Throws<InvalidOperationException>(() => db.Licenses.Verify("h1", "admin", DateTimeOffset.Now));
        Assert.Throws<InvalidOperationException>(() => db.Licenses.Save(verifiedNow with { PublicPerformance = Answer.Unknown }, "admin"));

        // potpuna, ali bez podatka ko je potvrdio: ne moze
        Assert.Throws<InvalidOperationException>(() => db.Licenses.Save(verifiedNow with { VerifiedBy = null }, "admin"));

        Assert.Equal(LicenseStatus.UnderReview, db.Licenses.Get("h1")!.Status);
        Assert.Throws<ArgumentException>(() => db.Licenses.Save(CompleteLicense("h1"), " "));
    }

    [Fact]
    public void Catalog_MarksOnlyVerifiedUndamagedPresentTracksAsBroadcastable()
    {
        using var db = LustreDatabase.Open(_path);
        long Add(string name, string hash, Func<TrackRecord, TrackRecord>? change = null) =>
            db.Tracks.Upsert((change ?? (t => t))(Track(@"D:\" + name, hash)));

        var ok = Add("ok.mp3", "h-ok");
        var unverified = Add("neproverena.mp3", "h-ne");
        var noLicense = Add("bez-evidencije.mp3", "h-bez");
        var broken = Add("pokvarena.mp3", "h-ok", t => t with { DecodeOk = false });
        var missing = Add("nestala.mp3", "h-ok");
        var faulty = Add("greske.mp3", "h-ok");
        var noArtist = Add("bez-izvodjaca.mp3", "h-ok", t => t with { Artist = string.Empty });
        db.Tracks.SetMissing(missing, true);
        for (var i = 0; i < 3; i++)
        {
            db.Tracks.RecordFault(faulty);
        }

        db.Licenses.EnsureImported("h-ok", "skener");
        db.Licenses.Save(CompleteLicense("h-ok"), "admin");
        db.Licenses.Verify("h-ok", "admin", DateTimeOffset.Now);
        db.Licenses.EnsureImported("h-ne", "skener");

        var catalog = TrackCatalog.Build(db.Tracks.GetAll(), db.Licenses.GetAll()).ToDictionary(t => long.Parse(t.Id));

        Assert.True(catalog[ok].Broadcastable);
        Assert.True(catalog[noArtist].Broadcastable);
        Assert.False(catalog[unverified].Broadcastable);
        Assert.False(catalog[noLicense].Broadcastable);
        Assert.False(catalog[broken].Broadcastable);
        Assert.False(catalog[missing].Broadcastable);
        Assert.False(catalog[faulty].Broadcastable);
        Assert.NotEqual(catalog[ok].Artist, catalog[noArtist].Artist);
        Assert.StartsWith("?", catalog[noArtist].Artist);
    }

    [Fact]
    public void History_IsReturnedInOrder_FromGivenMoment()
    {
        using var db = LustreDatabase.Open(_path);
        var start = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            db.History.Add(new PlayRow { TrackId = 10 + i, StartedUtc = start.AddMinutes(4 * i), Source = "Rotation", Part = "Morning", Program = "p", Tier = "Program" });
        }

        var last = db.History.Add(new PlayRow { TrackId = 99, StartedUtc = start.AddMinutes(30), Source = "Override" });
        db.History.MarkOutcome(last, endedEarly: true, faulted: true);

        var since = db.History.GetSince(start.AddMinutes(8));
        Assert.Equal([12L, 13L, 14L, 99L], since.Select(p => p.TrackId));
        Assert.True(since[^1].EndedEarly && since[^1].Faulted);
        Assert.False(since[0].Faulted);
        Assert.Equal(6, db.History.GetSince(DateTime.MinValue.ToUniversalTime()).Count);
    }

    [Fact]
    public void Settings_StoreScheduleAsJson_AndReadItBackIdentically()
    {
        var schedule = DefaultSchedule.Create() with
        {
            Exceptions =
            [
                new ScheduleException
                {
                    Name = "Dan lokala", From = new DateOnly(2026, 12, 31), To = new DateOnly(2027, 1, 1),
                    Schedule = DefaultSchedule.Create().Days[DayOfWeek.Saturday] with { Times = new DayTimes { Close = new TimeOnly(23, 45) } },
                },
            ],
        };

        using (var db = LustreDatabase.Open(_path))
        {
            db.Settings.SetJson("schedule", schedule);
            db.Settings.Set("volume", "0.6");
            db.State.Set("now-playing", "17");
            db.State.Remove("now-playing");
        }

        using (var db = LustreDatabase.Open(_path))
        {
            var loaded = db.Settings.GetJson<WeeklySchedule>("schedule")!;
            Assert.Empty(ScheduleValidator.Validate(loaded));
            Assert.Equal(7, loaded.Days.Count);
            Assert.Equal("Dan lokala", loaded.Exceptions.Single().Name);

            // isti odgovori rasporeda pre i posle cuvanja, kroz celu nedelju i izuzetak
            for (var t = new DateTime(2026, 11, 16); t < new DateTime(2026, 11, 23); t = t.AddMinutes(30))
            {
                Assert.Equal(ScheduleEngine.Evaluate(schedule, null, t).Program?.Name, ScheduleEngine.Evaluate(loaded, null, t).Program?.Name);
            }

            Assert.True(ScheduleEngine.Evaluate(loaded, null, new DateTime(2026, 12, 31, 23, 40, 0)).ShouldPlay);
            Assert.Equal("0.6", db.Settings.Get("volume"));
            Assert.Null(db.State.Get("now-playing"));
            Assert.Null(db.Settings.GetJson<WeeklySchedule>("nema"));
        }
    }

    [Fact]
    public void ReadOnlyConnection_ReadsWhileWriterIsOpen_AndCannotWrite()
    {
        using var writer = LustreDatabase.Open(_path);
        writer.Tracks.Upsert(Track(@"D:\a.mp3"));

        using var reader = LustreDatabase.Open(_path, readOnly: true);
        Assert.Single(reader.Tracks.GetAll());

        writer.Tracks.Upsert(Track(@"D:\b.mp3"));
        Assert.Equal(2, reader.Tracks.GetAll().Count);
        Assert.ThrowsAny<Exception>(() => reader.Tracks.Upsert(Track(@"D:\c.mp3")));
        Assert.Equal(2, writer.Tracks.GetAll().Count);
    }

    [Fact] // T-BAK-03 (osnova): kopija se pravi dok baza radi i proverava se
    public void Backup_IsConsistentCopy_AndIsVerified()
    {
        var backup = Path.Combine(_directory, "backup.db");
        using (var db = LustreDatabase.Open(_path))
        {
            for (var i = 0; i < 200; i++)
            {
                db.Tracks.Upsert(Track($@"D:\{i}.mp3", "h" + i));
            }

            db.BackupTo(backup);
            db.Tracks.Upsert(Track(@"D:\posle.mp3"));
        }

        Assert.True(LustreDatabase.VerifyFile(backup));
        using var restored = LustreDatabase.Open(backup);
        Assert.Equal(200, restored.Tracks.GetAll().Count);
    }

    [Fact] // T-BAK-02 (osnova): ostecena baza se prepoznaje, program ne pada
    public void DamagedOrForeignFile_IsDetected()
    {
        var garbage = Path.Combine(_directory, "smece.db");
        var bytes = new byte[8192];
        new Random(5).NextBytes(bytes);
        File.WriteAllBytes(garbage, bytes);

        var empty = Path.Combine(_directory, "prazno.db");
        File.WriteAllBytes(empty, []);

        Assert.False(LustreDatabase.VerifyFile(garbage));
        Assert.False(LustreDatabase.VerifyFile(empty));
        Assert.False(LustreDatabase.VerifyFile(Path.Combine(_directory, "nema.db")));
        Assert.ThrowsAny<Exception>(() => LustreDatabase.Open(garbage).Dispose());
    }

    [Fact] // GHSA-2m69-gcr7-jv3q / CVE-2025-6965: ranjive su verzije SQLite-a pre 3.50.2
    public void LoadedSqlite_IsNotTheVulnerableVersion()
    {
        using var db = LustreDatabase.Open(_path);
        var version = Version.Parse(db.SqliteVersion);
        Assert.True(version >= new Version(3, 50, 2), "Ucitan je SQLite " + db.SqliteVersion);
    }

    [Fact]
    public void DatabaseWrittenByPreviousVersion_OpensUnchanged_AndStaysWritable()
    {
        // Baza koju je napravio ovaj program dok je koristio SQLite 3.46.1 (izmena add369c, 15.11.2026.).
        var source = Path.Combine(AppContext.BaseDirectory, "db", "sema1-sqlite-3.46.1.db");
        File.Copy(source, _path);

        using (var db = LustreDatabase.Open(_path))
        {
            Assert.True(db.QuickCheck());
            Assert.Equal(1, db.SchemaVersion);

            var tracks = db.Tracks.GetAll();
            Assert.Equal(5, tracks.Count);
            Assert.Equal("Pesma 1 čćžšđ", tracks[0].Title);
            Assert.Equal(-14.25, tracks[0].Lufs);
            Assert.Null(tracks[1].Lufs);
            Assert.True(tracks[1].Favorite);
            Assert.True(tracks[2].Blocked);
            Assert.Equal(["Vece"], tracks[3].Genres);
            Assert.Equal(441000 * 5, tracks[4].ExactFrames);

            Assert.Equal(LicenseStatus.Verified, db.Licenses.Get("hash01")!.Status);
            Assert.Equal(LicenseStatus.Imported, db.Licenses.Get("hash02")!.Status);
            Assert.Equal(5, db.History.GetSince(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)).Count);
            Assert.Equal("PBKDF2$primer", db.Settings.Get("pin"));
            Assert.Equal([@"D:\Muzika\Vece"], db.Settings.GetJson<List<string>>("music-folders"));
            Assert.Equal("{\"TrackId\":\"1\",\"PositionSeconds\":42.5}", db.State.Get("snapshot"));

            // i dalje se u nju normalno pise
            db.Tracks.SetLoudness(tracks[1].Id, -9.5);
            db.Settings.Set("proba", "novo");
        }

        using var again = LustreDatabase.Open(_path);
        Assert.True(again.QuickCheck());
        Assert.Equal(-9.5, again.Tracks.GetAll()[1].Lufs);
        Assert.Equal("novo", again.Settings.Get("proba"));
        Assert.Equal(5, again.Tracks.GetAll().Count);
    }
}
