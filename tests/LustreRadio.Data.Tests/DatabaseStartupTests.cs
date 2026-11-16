using LustreRadio.Data;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Data.Tests;

[CollectionDefinition(nameof(DatabaseStartupCollection), DisableParallelization = true)]
public sealed class DatabaseStartupCollection;

/// <summary>
/// Faza B, korak B1: kopija pre nadogradnje, nadogradnja 1 -> 2 i rad bez nadogradnje (plan v8, deo 2.3).
/// Brojevi testova su iz plana (B-44 ...). Testovi rade sami, jer brojaci otvorenih veza vaze za ceo proces.
/// </summary>
[Collection(nameof(DatabaseStartupCollection))]
public sealed class DatabaseStartupTests : IDisposable
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "db", "sema1-sqlite-3.46.1.db");
    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lustre-start-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly string _backups;

    public DatabaseStartupTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "lustre.db");
        _backups = Path.Combine(_directory, "backups");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // privremeni folder
        }
    }

    [Fact] // B-44, B-167
    public void B44_B167_UpgradesFixture_DataSame_FiveColumns_OldRowsAreStariZapis()
    {
        File.Copy(Fixture, _path);
        var result = DatabaseStartup.Open(_path, _backups);
        using var db = result.Database;

        Assert.Equal(DatabaseMode.Normal, result.Mode);
        Assert.Equal(2, db.SchemaVersion);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));

        var tracks = db.Tracks.GetAll();
        Assert.Equal(5, tracks.Count);
        Assert.Equal("Pesma 1 čćžšđ", tracks[0].Title);
        Assert.Equal(-14.25, tracks[0].Lufs);
        Assert.Equal(5, db.History.GetSince(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)).Count);
        Assert.Equal("PBKDF2$primer", db.Settings.Get("pin"));

        using var raw = Raw(_path, readOnly: true);
        var columns = Strings(raw, "SELECT name FROM pragma_table_info('play_history')");
        foreach (var c in new[] { "playback_id", "status", "stop_reason", "transition_reached", "last_rid", "ended_early", "faulted" })
        {
            Assert.Contains(c, columns);
        }

        Assert.Contains("ux_history_playback", Strings(raw, "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='play_history' AND sql LIKE '%UNIQUE%'"));
        Assert.Equal(["stari_zapis"], Strings(raw, "SELECT DISTINCT status FROM play_history"));
        Assert.Equal(5L, Long(raw, "SELECT count(*) FROM play_history WHERE playback_id IS NULL"));
    }

    [Fact] // B-45 (rad bez nadogradnje je iskljucen: Engine odbija pokretanje, baza ostaje sema 1)
    public void B45_MigrationInterrupted_StaysSchema1_StartRefused()
    {
        File.Copy(Fixture, _path);
        var error = Assert.Throws<DatabaseStartupException>(() => DatabaseStartup.Open(_path, _backups, null, new StartupFaults { BeforeStep = s => { if (s == "migrate") { throw new IOException("prekid usred nadogradnje"); } } }));
        Assert.Contains("prekid", error.Message);
        Assert.Contains("iskljucen", error.Message);

        using var raw = Raw(_path, readOnly: true);
        Assert.Equal(1L, Long(raw, "PRAGMA user_version"));
        Assert.Equal("ok", Strings(raw, "PRAGMA integrity_check")[0]);
    }

    [Fact] // B-47
    public void B47_SecondStart_DoesNothing_NewerSchema_IsRefused()
    {
        File.Copy(Fixture, _path);
        DatabaseStartup.Open(_path, _backups).Database.Dispose();
        var backupsAfterFirst = Directory.GetFiles(_backups).Length;
        var second = DatabaseStartup.Open(_path, _backups);
        second.Database.Dispose();
        Assert.Equal(DatabaseMode.Normal, second.Mode);
        Assert.Null(second.BackupPath);
        Assert.Equal(backupsAfterFirst, Directory.GetFiles(_backups).Length);

        using (var raw = Raw(_path, readOnly: false))
        {
            Exec(raw, "PRAGMA user_version=3");
        }

        var error = Assert.Throws<DatabaseStartupException>(() => DatabaseStartup.Open(_path, _backups));
        Assert.Contains("novije verzije", error.Message);
    }

    [Fact] // B-90
    public void B90_ActiveWal_FileCopyMissesRows_ProcedureBackupHasAll()
    {
        var snapshot = CrashSnapshotWithWalRows(500);
        var dbOnly = Path.Combine(_directory, "samo-db.db");
        File.Copy(snapshot, dbOnly);
        using (var raw = Raw(dbOnly, readOnly: true))
        {
            Assert.Equal(0L, Long(raw, "SELECT count(*) FROM settings WHERE key LIKE 'wal-%'"));
        }

        var result = DatabaseStartup.Open(snapshot, _backups);
        result.Database.Dispose();
        Assert.Equal(DatabaseMode.Normal, result.Mode);
        using var backup = Raw(result.BackupPath!, readOnly: true);
        Assert.Equal(500L, Long(backup, "SELECT count(*) FROM settings WHERE key LIKE 'wal-%'"));
    }

    [Fact] // B-91
    public void B91_LargeWal_BackupComplete()
    {
        var snapshot = CrashSnapshotWithWalRows(5000, valueSize: 1500);
        Assert.True(new FileInfo(snapshot + "-wal").Length > 4_000_000);
        var result = DatabaseStartup.Open(snapshot, _backups);
        result.Database.Dispose();
        using var backup = Raw(result.BackupPath!, readOnly: true);
        Assert.Equal(5000L, Long(backup, "SELECT count(*) FROM settings WHERE key LIKE 'wal-%'"));
    }

    [Fact] // B-92
    public void B92_BackupWhileAnotherConnectionWrites_CopyIsConsistent()
    {
        File.Copy(Fixture, _path);
        using var stop = new CancellationTokenSource();
        var writer = new Thread(() =>
        {
            using var w = Raw(_path, readOnly: false);
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Exec(w, $"INSERT OR REPLACE INTO app_state(key, value) VALUES ('w{i++ % 50}', '{i}')");
                }
                catch (SqliteException)
                {
                    // zauzeto dok traje nadogradnja
                }
            }
        });
        writer.Start();

        Thread.Sleep(200);
        try
        {
            var result = DatabaseStartup.Open(_path, _backups);
            result.Database.Dispose();
            _output.WriteLine($"Rezim: {result.Mode}");
        }
        catch (DatabaseStartupException ex)
        {
            _output.WriteLine("Pokretanje odbijeno: " + ex.Message);
        }
        finally
        {
            stop.Cancel();
            writer.Join();
        }

        foreach (var backup in Directory.GetFiles(_backups, "*.db"))
        {
            using var raw = Raw(backup, readOnly: true);
            Assert.Equal("ok", Strings(raw, "PRAGMA integrity_check")[0]);
            Assert.Equal(1L, Long(raw, "PRAGMA user_version"));
        }
    }

    [Theory] // B-93
    [InlineData("izmenjeni-bajtovi")]
    [InlineData("odsecen-fajl")]
    public void B93_DamagedCopy_IsRejected_NoUpgrade_StartRefused(string damage)
    {
        File.Copy(Fixture, _path);
        var before = ByteFingerprint.Of(_path);
        Assert.Throws<DatabaseStartupException>(() => DatabaseStartup.Open(_path, _backups, null, new StartupFaults
        {
            TamperCopy = copy =>
            {
                using var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite);
                if (damage == "odsecen-fajl")
                {
                    stream.SetLength(stream.Length / 2);
                }
                else
                {
                    stream.Position = stream.Length / 2;
                    stream.Write(Enumerable.Repeat((byte)0xA5, 4096).ToArray());
                }
            },
        }));
        Assert.Empty(Directory.GetFiles(_backups, "*.db"));
        Assert.True(ByteFingerprint.Of(_path).SameDataAs(before));
    }

    [Fact] // B-94 (rad bez nadogradnje je iskljucen: Engine odbija pokretanje, baza netaknuta)
    public void B94_BackupFolderUnavailable_NoUpgrade_StartRefused()
    {
        File.Copy(Fixture, _path);
        File.WriteAllText(_backups, "ovo je fajl, a ne folder");
        var before = ByteFingerprint.Of(_path);
        var error = Assert.Throws<DatabaseStartupException>(() => DatabaseStartup.Open(_path, _backups));
        Assert.Contains("sema 1", error.Message);
        Assert.True(ByteFingerprint.Of(_path).SameDataAs(before));
    }

    [Fact] // B-94, drugi deo: nema mesta na disku
    public void B94_NoSpaceDuringBackup_NoUpgrade()
    {
        File.Copy(Fixture, _path);
        var error = Assert.Throws<DatabaseStartupException>(() => DatabaseStartup.Open(_path, _backups, null, new StartupFaults { BeforeStep = s => { if (s == "flush") { throw new IOException("Nema dovoljno mesta na disku."); } } }));
        Assert.Contains("mesta", error.Message);
    }

    [Fact] // B-95
    public void B95_CopyMissingOneRow_IsRejected()
    {
        File.Copy(Fixture, _path);
        var error = Assert.Throws<DatabaseStartupException>(() => DatabaseStartup.Open(_path, _backups, null, new StartupFaults
        {
            TamperCopy = copy =>
            {
                using var raw = Raw(copy, readOnly: false);
                Exec(raw, "DELETE FROM play_history WHERE id = (SELECT max(id) FROM play_history)");
            },
        }));
        Assert.Contains("sadrzaj", error.Message);
    }

    [Fact] // B-96
    public void B96_Backup_IsOneStandaloneFile()
    {
        File.Copy(Fixture, _path);
        var result = DatabaseStartup.Open(_path, _backups);
        result.Database.Dispose();
        var backup = result.BackupPath!;
        Assert.False(File.Exists(backup + "-wal"));
        Assert.False(File.Exists(backup + "-shm"));
        var elsewhere = Path.Combine(_directory, "drugo-mesto.db");
        File.Copy(backup, elsewhere);
        using var raw = Raw(elsewhere, readOnly: true);
        Assert.Equal(1L, Long(raw, "PRAGMA user_version"));
        Assert.Equal(5L, Long(raw, "SELECT count(*) FROM tracks"));
    }

    [Theory] // B-152, B-158: NALAZ iz B1 - provera P2 ne prolazi (BEGIN IMMEDIATE nije odbijen); bajtovi ostaju isti
    [InlineData("uredno-zatvorena")]
    [InlineData("posle-pada-sa-WAL-om")]
    [InlineData("WAL-obrisan-pre")]
    public void B152_B158_ProbeP2DoesNotPass_StartRefused_BytesUnchanged(string state)
    {
        var path = state switch
        {
            "posle-pada-sa-WAL-om" => CrashSnapshotWithWalRows(200),
            _ => FixtureCopy(),
        };
        File.WriteAllText(_backups, "blokira kopiju"); // kopija ne moze da se napravi -> rad bez nadogradnje
        var before = ByteFingerprint.Of(path);
        var error = Assert.Throws<DatabaseStartupException>(() => DatabaseStartup.Open(path, _backups, null, new StartupFaults { AllowReadOnlyMode = true }));
        Assert.Contains("P2", error.Message);

        var after = ByteFingerprint.Of(path);
        Assert.True(after.SameDataAs(before), $"bajtovi su se promenili ({state})");
        _output.WriteLine($"{state}: WAL pre={before.WalExists}/{before.WalLength}, posle={after.WalExists}/{after.WalLength}");
    }

    [Fact] // B-156
    public void B156_ProbeOnWritableConnection_IsDetected_StartRefused()
    {
        File.Copy(Fixture, _path);
        File.WriteAllText(_backups, "blokira kopiju");
        var before = ByteFingerprint.Of(_path);
        var error = Assert.Throws<DatabaseStartupException>(() => DatabaseStartup.Open(_path, _backups, null, new StartupFaults { AllowReadOnlyMode = true, ProbeConnection = p => Raw(p, readOnly: false) }));
        Assert.Contains("P1", error.Message);
        Assert.True(ByteFingerprint.Of(_path).SameDataAs(before));
    }

    [Fact] // B-159
    public void B159_FailedBackup_NoWritableConnectionToTheFile()
    {
        File.Copy(Fixture, _path);
        File.WriteAllText(_backups, "blokira kopiju");
        var writable = DatabaseStartup.WritableConnectionsOpened;
        Assert.Throws<DatabaseStartupException>(() => DatabaseStartup.Open(_path, _backups));
        Assert.Equal(writable, DatabaseStartup.WritableConnectionsOpened);
    }

    [Fact(Skip = "rad bez nadogradnje je iskljucen (P2 ne prolazi; B1, 16.11.2026.); ceka odluku vlasnika")] // B-180
    public void B180_BytesChangedDuringReadOnlyStart_StartRefused()
    {
        File.Copy(Fixture, _path);
        File.WriteAllText(_backups, "blokira kopiju");
        var error = Assert.Throws<DatabaseStartupException>(() => DatabaseStartup.Open(_path, _backups, null, new StartupFaults
        {
            BeforeByteCheck = p =>
            {
                using var raw = Raw(p, readOnly: false);
                Exec(raw, "PRAGMA journal_mode=DELETE");
                Exec(raw, "INSERT INTO settings(key, value) VALUES ('promena', 'x')");
            },
        }));
        Assert.Contains("V1", error.Message);
    }

    [Fact(Skip = "rad bez nadogradnje je iskljucen (P2 ne prolazi; B1, 16.11.2026.); ceka odluku vlasnika")] // B-181 (izvedba iz B1: kopija u memoriji), B-186
    public void B181_B186_WithoutUpgrade_FileIsClosed_WritesStayInMemory()
    {
        File.Copy(Fixture, _path);
        File.WriteAllText(_backups, "blokira kopiju");
        var before = ByteFingerprint.Of(_path);
        var result = DatabaseStartup.Open(_path, _backups);
        using (var db = result.Database)
        {
            using (var exclusive = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.True(exclusive.Length > 0); // nijedna rucka na fajlu baze nije otvorena
            }

            db.Settings.Set("samo-u-memoriji", "da");
            db.State.Set("snapshot", "{}");
            Assert.Equal("da", db.Settings.Get("samo-u-memoriji"));
        }

        Assert.True(ByteFingerprint.Of(_path).SameDataAs(before));
        using var raw = Raw(_path, readOnly: true);
        Assert.Equal(0L, Long(raw, "SELECT count(*) FROM settings WHERE key = 'samo-u-memoriji'"));
    }

    [Fact] // B-182
    public void B182_LoadedSqlite_Is_3_53_3_FromPackage()
    {
        using var raw = new SqliteConnection("Data Source=:memory:");
        raw.Open();
        var version = Strings(raw, "SELECT sqlite_version()")[0];
        _output.WriteLine($"SQLite {version}; biblioteka {SQLitePCL.raw.GetNativeLibraryName()}");
        Assert.Equal("3.53.3", version);
        Assert.Equal("e_sqlite3", SQLitePCL.raw.GetNativeLibraryName());
    }

    [Theory] // B-183 (stanja 1-6; stanje 7, folder bez prava pravljenja fajlova, nije u ovom testu)
    [InlineData("1-bez-wal-i-shm")]
    [InlineData("2-wal-sa-podacima-i-shm")]
    [InlineData("3-wal-sa-podacima-bez-shm")]
    [InlineData("4-prazan-wal")]
    [InlineData("5-wal-odsecen-poslednji-deo")]
    [InlineData("6-shm-zakljucan")]
    public void B183_ReadOnlyOpen_FileStates(string state)
    {
        string path;
        var expectedRows = 0L;
        switch (state)
        {
            case "1-bez-wal-i-shm":
                path = FixtureCopy();
                break;
            case "2-wal-sa-podacima-i-shm":
                path = CrashSnapshotWithWalRows(100, keepShm: true);
                expectedRows = 100;
                break;
            case "3-wal-sa-podacima-bez-shm":
                path = CrashSnapshotWithWalRows(100);
                expectedRows = 100;
                break;
            case "4-prazan-wal":
                path = FixtureCopy();
                File.WriteAllBytes(path + "-wal", []);
                break;
            case "5-wal-odsecen-poslednji-deo":
                path = CrashSnapshotWithWalRows(100);
                using (var wal = new FileStream(path + "-wal", FileMode.Open, FileAccess.ReadWrite))
                {
                    wal.SetLength(wal.Length - 1000);
                }

                expectedRows = -1; // SQLite koristi samo zavrsene upise; tacan broj zavisi od toga gde je odseceno
                break;
            default:
                path = CrashSnapshotWithWalRows(100, keepShm: true);
                expectedRows = 100;
                break;
        }

        var before = ByteFingerprint.Of(path);
        FileStream? locker = state.StartsWith("6-", StringComparison.Ordinal)
            ? new FileStream(path + "-shm", FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            : null;
        try
        {
            using var raw = Raw(path, readOnly: true);
            var rows = Long(raw, "SELECT count(*) FROM settings WHERE key LIKE 'wal-%'");
            _output.WriteLine($"{state}: otvoreno; redova iz WAL-a {rows}");
            if (expectedRows >= 0)
            {
                Assert.Equal(expectedRows, rows);
            }
        }
        catch (SqliteException ex)
        {
            _output.WriteLine($"{state}: otvaranje nije uspelo ({ex.SqliteErrorCode}: {ex.Message}) -> Engine bi odbio pokretanje");
            Assert.StartsWith("6-", state, StringComparison.Ordinal);
        }
        finally
        {
            locker?.Dispose();
        }

        Assert.True(ByteFingerprint.Of(path).SameDataAs(before), $"bajtovi su se promenili ({state})");
    }

    [Theory] // B-184
    [InlineData("INSERT INTO settings(key, value) VALUES ('x', 'y')")]
    [InlineData("CREATE TABLE proba(a)")]
    [InlineData("PRAGMA user_version=7")]
    [InlineData("PRAGMA journal_mode=DELETE")]
    [InlineData("PRAGMA wal_checkpoint(TRUNCATE)")]
    [InlineData("VACUUM")]
    [InlineData("BEGIN IMMEDIATE")]
    public void B184_WriteAttempts_OnReadOnlyConnection_ChangeNothing(string sql)
    {
        var path = CrashSnapshotWithWalRows(100, keepShm: true);
        var before = ByteFingerprint.Of(path);
        foreach (var queryOnly in new[] { false, true })
        {
            using var raw = Raw(path, readOnly: true);
            if (queryOnly)
            {
                Exec(raw, "PRAGMA query_only=ON");
            }

            try
            {
                using var command = raw.CreateCommand();
                command.CommandText = sql;
                var value = command.ExecuteScalar();
                _output.WriteLine($"{sql} (query_only={queryOnly}): nije odbijeno; vratilo '{value}'");
            }
            catch (SqliteException ex)
            {
                _output.WriteLine($"{sql} (query_only={queryOnly}): odbijeno ({ex.SqliteErrorCode})");
            }

            Assert.Equal(1L, Long(raw, "PRAGMA user_version"));
        }

        Assert.True(ByteFingerprint.Of(path).SameDataAs(before), "bajtovi .db ili -wal su se promenili");
    }

    [Fact] // B-187: samo se belezi; odluka da se immutable ne koristi ne zavisi od ishoda
    public void B187_Immutable_WithRowsOnlyInWal_IsRecorded()
    {
        var path = CrashSnapshotWithWalRows(500);
        using var raw = new SqliteConnection($"Data Source=file:{path.Replace('\\', '/')}?immutable=1;Mode=ReadOnly;Pooling=False");
        try
        {
            raw.Open();
            var rows = Long(raw, "SELECT count(*) FROM settings WHERE key LIKE 'wal-%'");
            _output.WriteLine($"REZULTAT B-187: immutable=1 vidi {rows} od 500 redova koji su samo u WAL-u");
        }
        catch (SqliteException ex)
        {
            _output.WriteLine($"REZULTAT B-187: immutable=1 nije otvoren: {ex.Message}");
        }
    }

    [Fact] // B-188
    public void B188_ShmDeletedWhileClosed_DataSame()
    {
        var path = CrashSnapshotWithWalRows(300, keepShm: true);
        File.Delete(path + "-shm");
        using var raw = Raw(path, readOnly: true);
        Assert.Equal(300L, Long(raw, "SELECT count(*) FROM settings WHERE key LIKE 'wal-%'"));
    }

    [Fact] // promena u B1: baza seme 1 vise ne moze da se otvori direktno za upis
    public void Schema1_CannotBeOpenedForWritingDirectly()
    {
        File.Copy(Fixture, _path);
        Assert.Throws<SchemaUpgradeRequiredException>(() => LustreDatabase.Open(_path).Dispose());
        using var raw = Raw(_path, readOnly: true);
        Assert.Equal(1L, Long(raw, "PRAGMA user_version"));
    }

    private string FixtureCopy()
    {
        var path = Path.Combine(_directory, "baza-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        File.Copy(Fixture, path);
        return path;
    }

    /// <summary>
    /// Stanje posle nasilnog gasenja: redovi su upisani samo u WAL (prenos u bazu iskljucen), a fajlovi se kopiraju
    /// dok je veza jos otvorena. Kopija je ono sto bi ostalo na disku da je proces ubijen.
    /// </summary>
    private string CrashSnapshotWithWalRows(int rows, bool keepShm = false, int valueSize = 10)
    {
        var live = FixtureCopy();
        var snapshot = Path.Combine(_directory, "posle-pada-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using (var w = Raw(live, readOnly: false))
        {
            Exec(w, "PRAGMA journal_mode=WAL");
            Exec(w, "PRAGMA wal_autocheckpoint=0");
            using (var t = w.BeginTransaction())
            {
                for (var i = 0; i < rows; i++)
                {
                    using var c = w.CreateCommand();
                    c.Transaction = t;
                    c.CommandText = "INSERT INTO settings(key, value) VALUES ($k, $v)";
                    c.Parameters.AddWithValue("$k", $"wal-{i}");
                    c.Parameters.AddWithValue("$v", new string('x', valueSize));
                    c.ExecuteNonQuery();
                }

                t.Commit();
            }

            File.Copy(live, snapshot);
            CopyShared(live + "-wal", snapshot + "-wal");
            if (keepShm)
            {
                CopyShared(live + "-shm", snapshot + "-shm");
            }
        }

        return snapshot;
    }

    private static void CopyShared(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = File.Create(to);
        source.CopyTo(target);
    }

    private static SqliteConnection Raw(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Long(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<string> Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}
