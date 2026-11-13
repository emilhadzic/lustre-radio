using LustreRadio.Data;
using LustreRadio.Library;

namespace LustreRadio.Playout;

public sealed record IndexSummary(int FilesOnDisk, int Added, int Rescanned, int MarkedMissing, int Undecodable);

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

        foreach (var path in diff.New.Concat(diff.Changed))
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
                Genres = scanned.Genres,
                Kind = scanned.Kind.ToString(),
                DecoderId = scanned.DecoderId,
                DecodeOk = scanned.DecodeOk,
                DecodeError = scanned.DecodeError,
                SampleRate = scanned.SampleRate,
                Channels = scanned.Channels,
                ExactFrames = scanned.ExactFrames,
                DurationSeconds = scanned.DurationSeconds,
                PeakDbfs = scanned.PeakDbfs,
            });

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

        var byPath = known.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
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

        return new IndexSummary(onDisk.Count, diff.New.Count, diff.Changed.Count, markedMissing, undecodable);
    }
}
