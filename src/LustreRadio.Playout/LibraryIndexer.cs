using LustreRadio.Analysis;
using LustreRadio.Core.Licensing;
using LustreRadio.Data;
using LustreRadio.Library;

namespace LustreRadio.Playout;

public sealed record IndexSummary(int FilesOnDisk, int Added, int Rescanned, int MarkedMissing, int Undecodable)
{
    /// <summary>Koliko je pesama u ovom prolazu potvrdjeno po izjavi o kolektivnoj licenci.</summary>
    public int VerifiedCollectively { get; init; }

    /// <summary>Koliko je ranije skeniranih pesama u ovom prolazu dobilo izmerenu glasnocu.</summary>
    public int LoudnessMeasured { get; init; }
}

/// <summary>
/// Uskladjuje bazu sa folderima: nove i izmenjene fajlove skenira, nestale oznacava.
/// Svaki novi sadrzaj dobija evidenciju licence sa statusom "Uvezeno (neprovereno)".
/// </summary>
public static class LibraryIndexer
{
    public static IndexSummary Run(LustreDatabase db, IReadOnlyList<string> folders, Action<string>? log = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(folders);

        var onDisk = LibraryFiles.Enumerate(folders);
        var known = db.Tracks.GetAll();
        var diff = LibraryFiles.Diff(known.Select(t => new KnownFile(t.Path, t.SizeBytes, t.LastWriteUtc)), onDisk);
        var undecodable = 0;

        var byPath = known.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);

        // Pesme skenirane pre nego sto je program umeo da meri glasnocu: izmeri ih sada.
        // (Fajl koji je sav tisina ostaje bez vrednosti i proverava se ponovo pri svakom skeniranju.)
        // Isto za analizu krajeva (Smart Crossfade): pesme bez nje ili sa starijom verzijom se dekodiraju jos jednom.
        var unmeasured = diff.Unchanged.Where(p => byPath[p] is { DecodeOk: true } t && (t.Lufs is null || t.EdgesVersion < EdgeAnalyzer.Version))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var measured = 0;

        foreach (var path in diff.New.Concat(diff.Changed).Concat(unmeasured))
        {
            cancel.ThrowIfCancellationRequested();
            var scanned = TrackScanner.Scan(path);
            db.Tracks.Upsert(new TrackRecord
            {
                Path = scanned.Path,
                ContentHash = scanned.ContentHash,
                SizeBytes = scanned.SizeBytes,
                LastWriteUtc = scanned.LastWriteUtc,
                Title = scanned.Title,
                Artist = scanned.Artist,
                Album = scanned.Album,
                Genres = scanned.Genres.Count > 0 ? scanned.Genres : GenresFromFolder(path, folders),
                Kind = scanned.Kind.ToString(),
                DecoderId = scanned.DecoderId,
                DecodeOk = scanned.DecodeOk,
                DecodeError = scanned.DecodeError,
                SampleRate = scanned.SampleRate,
                Channels = scanned.Channels,
                ExactFrames = scanned.ExactFrames,
                DurationSeconds = scanned.DurationSeconds,
                PeakDbfs = scanned.PeakDbfs,
                Lufs = scanned.Lufs,
                AudioStartFrame = scanned.Edges?.AudioStartFrame,
                AudioEndFrame = scanned.Edges?.AudioEndFrame,
                OutroSeconds = scanned.Edges?.OutroSeconds,
                EdgesVersion = scanned.DecodeOk ? EdgeAnalyzer.Version : 0,
            });

            if (scanned.Lufs is not null && unmeasured.Contains(path) && byPath[path].Lufs is null)
            {
                measured++;
            }

            if (scanned.ContentHash.Length > 0)
            {
                db.Licenses.EnsureImported(scanned.ContentHash, "skener");
            }

            if (!scanned.DecodeOk)
            {
                undecodable++;
                log?.Invoke($"Ne moze da se dekodira: {path} ({scanned.DecodeError})");
            }
        }

        var markedMissing = 0;
        foreach (var path in diff.Missing)
        {
            if (!byPath[path].Missing)
            {
                db.Tracks.SetMissing(byPath[path].Id, true);
                markedMissing++;
            }
        }

        foreach (var path in diff.Unchanged)
        {
            if (byPath[path].Missing)
            {
                db.Tracks.SetMissing(byPath[path].Id, false);
            }
        }

        return new IndexSummary(onDisk.Count, diff.New.Count, diff.Changed.Count, markedMissing, undecodable)
        {
            VerifiedCollectively = ApplyCollectiveLicense(db),
            LoudnessMeasured = measured,
        };
    }

    /// <summary>
    /// Kad fajl nema zanr u oznakama, zanr je naziv podfoldera u kom se nalazi
    /// (npr. muzika\Deep House\pesma.mp3 -> "Deep House"). Fajl direktno u folderu sa muzikom nema zanr.
    /// </summary>
    public static IReadOnlyList<string> GenresFromFolder(string path, IReadOnlyList<string> folders)
    {
        foreach (var folder in folders)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = full[root.Length..];
            var separator = relative.IndexOf(Path.DirectorySeparatorChar);
            return separator > 0 ? [relative[..separator].Trim()] : [];
        }

        return [];
    }

    public const string CollectiveLicenseKey = "collective-license";

    /// <summary>
    /// Ako je vlasnik dao izjavu o kolektivnoj licenci sa automatskom primenom, sve ispravne
    /// pesme koje jos nisu proverene dobijaju status "Provereno" po toj izjavi.
    /// </summary>
    public static int ApplyCollectiveLicense(LustreDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var declaration = db.Settings.GetJson<CollectiveLicense>(CollectiveLicenseKey);
        if (declaration is not { AutoApply: true })
        {
            return 0;
        }

        var count = 0;
        foreach (var hash in db.Tracks.GetAll().Where(t => t.DecodeOk && !t.Missing && t.ContentHash.Length > 0).Select(t => t.ContentHash).Distinct())
        {
            // Odbijene pesme ostaju odbijene: to je pojedinacna odluka admina.
            if (db.Licenses.Get(hash) is { Status: LicenseStatus.Rejected })
            {
                continue;
            }

            if (db.Licenses.VerifyCollective(hash, declaration))
            {
                count++;
            }
        }

        return count;
    }
}
