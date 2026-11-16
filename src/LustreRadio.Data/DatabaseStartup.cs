using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LustreRadio.Data;

/// <summary>Kako je Engine otvorio bazu.</summary>
public enum DatabaseMode
{
    /// <summary>Tekuca sema; citanje i upis.</summary>
    Normal,

    /// <summary>Rad bez nadogradnje: baza sema 1 je ucitana u memoriju i zatvorena; u fajl se ne upisuje nista.</summary>
    NotUpgraded,
}

public sealed record DatabaseStartupResult(LustreDatabase Database, DatabaseMode Mode, string? Reason, string? BackupPath);

/// <summary>Engine ne sme da se pokrene: nastavak bi rizikovao bazu (plan faze B, 2.3, N6).</summary>
public sealed class DatabaseStartupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Postojeca baza starije seme otvara se samo kroz DatabaseStartup (kopija, provera, pa nadogradnja).</summary>
public sealed class SchemaUpgradeRequiredException(int version)
    : InvalidOperationException($"Baza je sema {version}; nadogradnja ide samo kroz proveren postupak (kopija, provera, nadogradnja).")
{
    public int Version { get; } = version;
}

/// <summary>Mesta na kojima testovi namerno izazivaju kvar. Samo za testove (internal); nema prekidaca u programu.</summary>
internal sealed class StartupFaults
{
    /// <summary>Poziva se pre svakog imenovanog koraka; moze da baci gresku.</summary>
    public Action<string>? BeforeStep { get; init; }

    /// <summary>Menja privremenu kopiju pre provere (ostecenje, brisanje reda).</summary>
    public Action<string>? TamperCopy { get; init; }

    /// <summary>Zamenjuje vezu za proveru P (npr. veza za upis, da se proveri da P to otkriva).</summary>
    public Func<string, SqliteConnection>? ProbeConnection { get; init; }

    /// <summary>Poziva se pre zavrsne provere bajtova (npr. menja fajl baze).</summary>
    public Action<string>? BeforeByteCheck { get; init; }
}

/// <summary>
/// Otvaranje baze pri pokretanju Engine-a (plan faze B v8, deo 2.3, korak B1).
/// Sema 1 se nadogradjuje na semu 2 tek posle kopije napravljene SQLite Online Backup API-jem preko veze
/// samo za citanje, i posle provere kopije (puna provera ispravnosti, ista sema, isti sadrzaj).
/// Ako kopija ili nadogradnja ne uspe: rad bez nadogradnje (baza se ucita u memoriju i zatvori), ili odbijanje pokretanja.
/// </summary>
public static class DatabaseStartup
{
    private static long _writableOpened;
    private static long _readOnlyOpened;

    /// <summary>Koliko je veza za upis otvoreno na fajlove baza (za test B-159).</summary>
    public static long WritableConnectionsOpened => Interlocked.Read(ref _writableOpened);

    public static long ReadOnlyConnectionsOpened => Interlocked.Read(ref _readOnlyOpened);

    public static DatabaseStartupResult Open(string path, string backupsDirectory, Action<string>? log = null) =>
        Open(path, backupsDirectory, log, faults: null);

    internal static DatabaseStartupResult Open(string path, string backupsDirectory, Action<string>? log, StartupFaults? faults)
    {
        var say = log ?? (_ => { });
        if (!File.Exists(path))
        {
            return new(LustreDatabase.Open(path), DatabaseMode.Normal, null, null);
        }

        // Korak 0: otisak bajtova pre ikakvog otvaranja.
        var bytesBefore = ByteFingerprint.Of(path);

        // Korak 1.1 – 1.3: veza samo za citanje.
        int version;
        string contentBefore;
        try
        {
            using var r1 = OpenConnection(path, readOnly: true);
            version = (int)(long)Scalar(r1, "PRAGMA user_version")!;
            if (version == LustreDatabase.CurrentSchemaVersion)
            {
                r1.Close();
                return new(LustreDatabase.Open(path), DatabaseMode.Normal, null, null);
            }

            if (version > LustreDatabase.CurrentSchemaVersion)
            {
                throw new DatabaseStartupException($"Baza je iz novije verzije programa (sema {version}, podrzano do {LustreDatabase.CurrentSchemaVersion}).");
            }

            if (version != 1)
            {
                throw new DatabaseStartupException($"Nepoznata sema baze: {version}.");
            }

            if (!IntegrityOk(r1))
            {
                throw new DatabaseStartupException("Baza ne prolazi punu proveru ispravnosti; Engine se ne pokrece.");
            }

            contentBefore = ContentFingerprint.Of(r1);
        }
        catch (SqliteException ex)
        {
            throw new DatabaseStartupException("Baza ne moze da se otvori ni samo za citanje: " + ex.Message, ex);
        }

        say("Baza je sema 1: pravim kopiju pre nadogradnje na semu 2.");
        string? backup = null;
        string reason;
        var writableTouched = false;
        try
        {
            backup = MakeVerifiedBackup(path, backupsDirectory, contentBefore, say, faults);
            writableTouched = true;
            Migrate(path, contentBefore, faults);
            say($"Baza nadogradjena na semu 2. Kopija pre nadogradnje: {backup}");
            return new(LustreDatabase.Open(path), DatabaseMode.Normal, null, backup);
        }
        catch (DatabaseStartupException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException or CopyRejectedException)
        {
            reason = ex.Message;
        }

        say("Baza NIJE nadogradjena: " + reason + ". Rad bez nadogradnje.");
        var bytesReference = writableTouched ? ByteFingerprint.Of(path) : bytesBefore;
        var database = OpenWithoutUpgrade(path, bytesReference, say, faults);
        return new(database, DatabaseMode.NotUpgraded, reason, null);
    }

    internal static void CountOpened(bool readOnly)
    {
        if (readOnly)
        {
            Interlocked.Increment(ref _readOnlyOpened);
        }
        else
        {
            Interlocked.Increment(ref _writableOpened);
        }
    }

    private static string MakeVerifiedBackup(string path, string backupsDirectory, string contentBefore, Action<string> say, StartupFaults? faults)
    {
        faults?.BeforeStep?.Invoke("backup");
        Directory.CreateDirectory(backupsDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var final = Path.Combine(backupsDirectory, $"pre-sema2-{stamp}.db");
        var temp = final + ".tmp";
        DeleteWithCompanions(temp);

        // 1.4: Online Backup API iz veze samo za citanje (vidi i ono sto je jos u WAL-u)
        using (var source = OpenConnection(path, readOnly: true))
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);

            // kopija je jedan samostalan fajl: bez -wal i -shm
            Scalar(destination, "PRAGMA journal_mode=DELETE");
        }

        faults?.BeforeStep?.Invoke("flush");
        using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Flush(flushToDisk: true);
        }

        faults?.TamperCopy?.Invoke(temp);

        // 1.6: provera kopije
        faults?.BeforeStep?.Invoke("verify");
        try
        {
            using var copy = OpenConnection(temp, readOnly: true);
            if (!IntegrityOk(copy))
            {
                throw new CopyRejectedException("kopija ne prolazi punu proveru ispravnosti");
            }

            if ((long)Scalar(copy, "PRAGMA user_version")! != 1)
            {
                throw new CopyRejectedException("kopija nema semu 1");
            }

            if (ContentFingerprint.Of(copy) != contentBefore)
            {
                throw new CopyRejectedException("sadrzaj kopije se razlikuje od baze");
            }
        }
        catch (Exception ex) when (ex is SqliteException or CopyRejectedException)
        {
            DeleteWithCompanions(temp);
            throw ex as CopyRejectedException ?? new CopyRejectedException("kopija ne moze da se otvori: " + ex.Message);
        }

        // 1.7: konacno ime; putanja i kontrolni zbir u dnevnik
        faults?.BeforeStep?.Invoke("rename");
        File.Move(temp, final);
        say($"Kopija baze proverena: {final}, SHA-256 {ByteFingerprint.Sha256(final)[..16]}");
        return final;
    }

    private static void Migrate(string path, string contentBefore, StartupFaults? faults)
    {
        faults?.BeforeStep?.Invoke("open-writable");
        using var connection = OpenConnection(path, readOnly: false);
        Scalar(connection, "PRAGMA busy_timeout=5000");
        Scalar(connection, "PRAGMA synchronous=FULL");
        if (ContentFingerprint.Of(connection) != contentBefore)
        {
            throw new InvalidOperationException("sadrzaj baze se promenio posle kopije");
        }

        using (var transaction = connection.BeginTransaction())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Schema.Upgrade1To2;
            command.ExecuteNonQuery();
            faults?.BeforeStep?.Invoke("migrate");
            transaction.Commit();
        }

        if (!IntegrityOk(connection))
        {
            throw new DatabaseStartupException("Baza ne prolazi proveru posle nadogradnje; Engine se ne pokrece. Kopija pre nadogradnje postoji (vidi POVRATAK).");
        }
    }

    private static LustreDatabase OpenWithoutUpgrade(string path, ByteFingerprint reference, Action<string> say, StartupFaults? faults)
    {
        // Korak P: provera da upis nije moguc, na zasebnoj privremenoj vezi.
        try
        {
            using var probe = faults?.ProbeConnection?.Invoke(path) ?? OpenConnection(path, readOnly: true);
            if (SQLitePCL.raw.sqlite3_db_readonly(probe.Handle, "main") != 1)
            {
                throw new DatabaseStartupException("Provera P1: veza nije samo za citanje; Engine se ne pokrece.");
            }

            try
            {
                Scalar(probe, "BEGIN IMMEDIATE");
                Scalar(probe, "ROLLBACK");
                throw new DatabaseStartupException("Provera P2: zahtev za upis nije odbijen; Engine se ne pokrece.");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 8)
            {
                // SQLITE_READONLY: ocekivano
            }
        }
        catch (SqliteException ex)
        {
            throw new DatabaseStartupException("Baza ne moze da se otvori samo za citanje (provera P): " + ex.Message, ex);
        }

        // Korak O: ucitavanje u memoriju, pa zatvaranje fajla.
        LustreDatabase database;
        try
        {
            var memory = new SqliteConnection("Data Source=:memory:;Pooling=False");
            memory.Open();
            using (var r2 = OpenConnection(path, readOnly: true))
            {
                Scalar(r2, "PRAGMA query_only=ON");
                r2.BackupDatabase(memory);
            }

            database = LustreDatabase.FromMemoryCopy(memory, path);
        }
        catch (SqliteException ex)
        {
            throw new DatabaseStartupException("Ucitavanje baze u memoriju nije uspelo: " + ex.Message, ex);
        }

        // Korak V: bajtovi baze i WAL-a moraju biti isti.
        faults?.BeforeByteCheck?.Invoke(path);
        var after = ByteFingerprint.Of(path);
        if (!after.SameDataAs(reference))
        {
            database.Dispose();
            throw new DatabaseStartupException("Provera V1: fajl baze ili WAL se promenio tokom rada samo za citanje; Engine se ne pokrece.");
        }

        say("Rad bez nadogradnje: baza je ucitana u memoriju i zatvorena; provere P i V su prosle.");
        return database;
    }

    private static SqliteConnection OpenConnection(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        CountOpened(readOnly);
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    internal static bool IntegrityOk(SqliteConnection connection) =>
        string.Equals(Scalar(connection, "PRAGMA integrity_check") as string, "ok", StringComparison.OrdinalIgnoreCase);

    private static void DeleteWithCompanions(string file)
    {
        foreach (var f in new[] { file, file + "-wal", file + "-shm", file + "-journal" })
        {
            if (File.Exists(f))
            {
                File.Delete(f);
            }
        }
    }

    private sealed class CopyRejectedException(string message) : Exception(message);
}

/// <summary>Otisak sadrzaja: za svaku tabelu broj redova i kontrolni zbir svih redova (citano kroz SQLite, dakle i iz WAL-a).</summary>
internal static class ContentFingerprint
{
    public static string Of(SqliteConnection connection)
    {
        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var result = new StringBuilder();
        foreach (var table in tables)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long rows = 0;
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows++;
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    var text = value switch
                    {
                        DBNull => "\u0000N",
                        byte[] blob => "\u0000B" + Convert.ToBase64String(blob),
                        IFormattable f => "\u0000V" + f.ToString(null, CultureInfo.InvariantCulture),
                        _ => "\u0000T" + value,
                    };
                    hash.AppendData(Encoding.UTF8.GetBytes(text));
                }

                hash.AppendData("\u0001"u8);
            }

            result.Append(table).Append(':').Append(rows).Append(':').Append(Convert.ToHexString(hash.GetHashAndReset())).Append(';');
        }

        return result.ToString();
    }
}

/// <summary>Otisak bajtova fajla baze i njenog WAL-a (fajl -shm se ne racuna: pomocni indeks, nije podatak).</summary>
internal sealed record ByteFingerprint(string Database, bool WalExists, string? Wal, long WalLength)
{
    public static ByteFingerprint Of(string path)
    {
        var wal = path + "-wal";
        var walExists = File.Exists(wal);
        return new(Sha256(path), walExists, walExists ? Sha256(wal) : null, walExists ? new FileInfo(wal).Length : 0);
    }

    public static string Sha256(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Isti .db; isti WAL ako je postojao; ako nije postojao, sme da nastane prazan.</summary>
    public bool SameDataAs(ByteFingerprint before)
    {
        if (Database != before.Database)
        {
            return false;
        }

        return before.WalExists ? WalExists && Wal == before.Wal : !WalExists || WalLength == 0;
    }
}
