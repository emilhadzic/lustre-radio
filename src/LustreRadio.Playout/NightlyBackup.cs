using System.Globalization;
using LustreRadio.Data;

namespace LustreRadio.Playout;

/// <summary>
/// Automatska nocna kopija baze (FINAL, korak 4; spec. 14). Jednom dnevno posle 03:30 (ili prvi put kad je racunar
/// upaljen posle tog vremena): kopija preko SQLite Online Backup API-ja, provera ispravnosti, pa tek onda konacno ime.
/// Cuva se poslednjih 14 kopija u folderu backups\. Neuspeh se upisuje u dnevnik i ponavlja sledeceg dana; muzika ne staje.
/// </summary>
public sealed class NightlyBackup(LustreDatabase db, string folder, Action<string> log, int keep = 14)
{
    public static readonly TimeOnly At = new(3, 30);

    public const string LastKey = "nightly-backup-last";

    public const string Prefix = "nocna-";

    private DateOnly? _attempted;

    public string Folder => folder;

    public bool Due(DateTime nowLocal)
    {
        var today = DateOnly.FromDateTime(nowLocal);
        return TimeOnly.FromDateTime(nowLocal) >= At && _attempted != today && db.Settings.Get(LastKey) != Key(today);
    }

    /// <summary>Pravi kopiju; vraca putanju ili null ako nije uspelo.</summary>
    public string? Run(DateTime nowLocal)
    {
        var today = DateOnly.FromDateTime(nowLocal);
        _attempted = today;
        var path = Path.Combine(folder, Prefix + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".db");
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(folder);
            File.Delete(temp);
            db.BackupTo(temp);
            if (!LustreDatabase.VerifyFile(temp))
            {
                File.Delete(temp);
                log("Nocna kopija baze nije prosla proveru ispravnosti; pokusava se sutra.");
                return null;
            }

            File.Move(temp, path, overwrite: true);
            db.Settings.Set(LastKey, Key(today));
            foreach (var old in Directory.GetFiles(folder, Prefix + "*.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
            {
                File.Delete(old);
            }

            log($"Nocna kopija baze: {path} ({new FileInfo(path).Length / 1024} KB).");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException or InvalidOperationException)
        {
            log("Nocna kopija baze nije uspela: " + ex.Message);
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // privremeni fajl ostaje; brise se sledeci put
            }

            return null;
        }
    }

    private static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
