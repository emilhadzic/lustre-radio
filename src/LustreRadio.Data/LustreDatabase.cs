using Microsoft.Data.Sqlite;

namespace LustreRadio.Data;

/// <summary>
/// Baza LUSTRE RADIO (SQLite, WAL). Jedan proces upisuje (Engine); interfejs otvara bazu samo za citanje.
/// Sve operacije idu kroz jednu vezu i zakljucavaju se, pa je bezbedno zvati ih sa vise niti.
/// </summary>
public sealed class LustreDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    private LustreDatabase(SqliteConnection connection, bool readOnly, string path)
    {
        _connection = connection;
        IsReadOnly = readOnly;
        FilePath = path;
        Tracks = new TrackStore(this);
        Licenses = new LicenseStore(this);
        History = new HistoryStore(this);
        Settings = new KeyValueStore(this, "settings");
        State = new KeyValueStore(this, "app_state");
    }

    public bool IsReadOnly { get; }

    public string FilePath { get; }

    public TrackStore Tracks { get; }

    public LicenseStore Licenses { get; }

    public HistoryStore History { get; }

    /// <summary>Podesavanja koja menja admin (raspored, pravila, glasnoca...).</summary>
    public KeyValueStore Settings { get; }

    /// <summary>Stanje koje program sam pamti (sta svira, pozicija...).</summary>
    public KeyValueStore State { get; }

    public static LustreDatabase Open(string path, bool readOnly = false)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            var database = new LustreDatabase(connection, readOnly, path);
            database.Execute("PRAGMA busy_timeout=5000");
            database.Execute("PRAGMA foreign_keys=ON");
            database.Execute("PRAGMA cache_size=-2000");
            if (readOnly)
            {
                database.Execute("PRAGMA query_only=ON");
            }
            else
            {
                database.Scalar<string>("PRAGMA journal_mode=WAL");
                database.Execute("PRAGMA synchronous=FULL");
                database.Migrate();
            }

            return database;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public int SchemaVersion => (int)Scalar<long>("PRAGMA user_version");

    /// <summary>Verzija SQLite-a koja je stvarno ucitana (npr. "3.53.3"); za proveru posle zamene paketa.</summary>
    public string SqliteVersion => Scalar<string>("SELECT sqlite_version()") ?? string.Empty;

    public string JournalMode => Scalar<string>("PRAGMA journal_mode") ?? string.Empty;

    /// <summary>Brza provera ispravnosti baze (radi se pri svakom startu).</summary>
    public bool QuickCheck()
    {
        try
        {
            return string.Equals(Scalar<string>("PRAGMA quick_check"), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Pravi konzistentnu kopiju baze dok je ona u upotrebi.</summary>
    public void BackupTo(string destinationPath)
    {
        lock (_gate)
        {
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString());
            destination.Open();
            _connection.BackupDatabase(destination);
        }
    }

    /// <summary>Da li je fajl ispravna baza koju ova verzija programa moze da otvori.</summary>
    public static bool VerifyFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var database = Open(path, readOnly: true);
            return database.QuickCheck() && database.SchemaVersion is > 0 and <= CurrentSchemaVersion;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    internal int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            return command.ExecuteNonQuery();
        }
    }

    internal T? Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            var value = command.ExecuteScalar();
            return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    internal List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var result = new List<T>();
            while (reader.Read())
            {
                result.Add(map(reader));
            }

            return result;
        }
    }

    /// <summary>Vise upisa kao jedna celina: ili svi ili nijedan.</summary>
    internal void InTransaction(Action action)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            action();
            transaction.Commit();
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private void Migrate()
    {
        var version = SchemaVersion;
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Baza je iz novije verzije programa (sema {version}, podrzano do {CurrentSchemaVersion}).");
        }

        if (version < 1)
        {
            InTransaction(() =>
            {
                Execute(Schema.Version1);
                Execute("PRAGMA user_version=1");
            });
        }
    }
}

internal static class Db
{
    public static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    public static DateTime ParseUtc(string text) =>
        DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public static string? Text(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static long Long(this SqliteDataReader reader, string column) => reader.GetInt64(reader.GetOrdinal(column));

    public static bool Flag(this SqliteDataReader reader, string column) => reader.GetInt64(reader.GetOrdinal(column)) != 0;

    public static double? Real(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }
}
