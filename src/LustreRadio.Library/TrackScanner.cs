using System.Security.Cryptography;
using LustreRadio.Audio;

namespace LustreRadio.Library;

/// <summary>Sve sto skeniranje utvrdi o jednom fajlu.</summary>
public sealed record ScannedFile
{
    public required string Path { get; init; }

    public long SizeBytes { get; init; }

    public DateTime LastWriteUtc { get; init; }

    /// <summary>SHA-256 celog fajla (mala slova, heksadecimalno).</summary>
    public string ContentHash { get; init; } = string.Empty;

    public AudioKind Kind { get; init; }

    public string? DecoderId { get; init; }

    /// <summary>Ceo fajl je uspesno dekodiran od pocetka do kraja.</summary>
    public bool DecodeOk { get; init; }

    public string? DecodeError { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    /// <summary>Tacan broj frejmova, izmeren dekodiranjem (ne ono sto dekoder najavljuje).</summary>
    public long ExactFrames { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary>Najvisi vrh u dBFS.</summary>
    public double? PeakDbfs { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public string? Album { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];
}

/// <summary>
/// Skenira jedan fajl: hes, oznake, probno dekodiranje celog fajla (tacna duzina i vrh).
/// Nikad ne baca gresku zbog loseg fajla: rezultat tada ima DecodeOk = false i opis greske.
/// Fajl se samo cita; nista se ne menja niti premesta.
/// </summary>
public static class TrackScanner
{
    public static ScannedFile Scan(string path)
    {
        var info = new FileInfo(path);
        var result = new ScannedFile
        {
            Path = path,
            SizeBytes = info.Exists ? info.Length : 0,
            LastWriteUtc = info.Exists ? info.LastWriteTimeUtc : default,
            Title = System.IO.Path.GetFileNameWithoutExtension(path),
        };

        if (!info.Exists)
        {
            return result with { DecodeError = "Fajl ne postoji." };
        }

        try
        {
            result = result with { ContentHash = HashFile(path), Kind = AudioFileSniffer.Detect(path) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result with { DecodeError = "Fajl ne moze da se procita: " + ex.Message };
        }

        result = ReadTags(result);
        return TrialDecode(result);
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static ScannedFile ReadTags(ScannedFile file)
    {
        try
        {
            using var tagged = TagLib.File.Create(file.Path);
            var tag = tagged.Tag;
            return file with
            {
                Title = string.IsNullOrWhiteSpace(tag.Title) ? file.Title : tag.Title.Trim(),
                Artist = (tag.FirstPerformer ?? tag.FirstAlbumArtist ?? string.Empty).Trim(),
                Album = string.IsNullOrWhiteSpace(tag.Album) ? null : tag.Album.Trim(),
                Genres = (tag.Genres ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToArray(),
            };
        }
        catch (Exception)
        {
            // Oznake nisu obavezne (npr. pogresna ekstenzija ili ostecene oznake): ostaje naziv fajla.
            return file;
        }
    }

    private static ScannedFile TrialDecode(ScannedFile file)
    {
        try
        {
            int sourceRate, channels;
            string decoderId;
            using (var probe = Decoders.Open(file.Path, AudioFormat.Stereo44100))
            {
                sourceRate = probe.SourceSampleRate;
                channels = probe.SourceChannels;
                decoderId = probe.DecoderId;
            }

            // dekodiranje u sopstvenom sample rate-u fajla (bez resamplinga), istim dekoderom
            using var source = Decoders.Open(file.Path, new AudioFormat(sourceRate, 2), decoderId);
            var buffer = new float[8192];
            long frames = 0;
            var peak = 0f;
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                frames += read;
                foreach (var sample in buffer.AsSpan(0, read * 2))
                {
                    var magnitude = Math.Abs(sample);
                    if (magnitude > peak)
                    {
                        peak = magnitude;
                    }
                }
            }

            if (frames == 0)
            {
                return file with { DecoderId = decoderId, SampleRate = sourceRate, Channels = channels, DecodeError = "Fajl ne sadrzi zvuk." };
            }

            return file with
            {
                DecodeOk = true,
                DecoderId = decoderId,
                SampleRate = sourceRate,
                Channels = channels,
                ExactFrames = frames,
                DurationSeconds = frames / (double)sourceRate,
                PeakDbfs = peak > 0 ? 20 * Math.Log10(peak) : null,
            };
        }
        catch (Exception ex)
        {
            return file with { DecodeError = ex.GetType().Name + ": " + ex.Message };
        }
    }
}

/// <summary>Sta se promenilo na disku u odnosu na ono sto baza zna.</summary>
public sealed record LibraryDiff
{
    public required IReadOnlyList<string> New { get; init; }

    /// <summary>Ista putanja, ali druga velicina ili datum izmene: treba ponovo skenirati.</summary>
    public required IReadOnlyList<string> Changed { get; init; }

    /// <summary>U bazi postoji, na disku vise ne.</summary>
    public required IReadOnlyList<string> Missing { get; init; }

    public required IReadOnlyList<string> Unchanged { get; init; }
}

public sealed record KnownFile(string Path, long SizeBytes, DateTime LastWriteUtc);

public static class LibraryFiles
{
    public static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus" };

    /// <summary>Svi audio fajlovi u zadatim folderima (i podfolderima). Nedostupni folderi se preskacu.</summary>
    public static IReadOnlyList<string> Enumerate(IEnumerable<string> folders)
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                if (Extensions.Contains(Path.GetExtension(file)))
                {
                    result.Add(file);
                }
            }
        }

        return [.. result];
    }

    public static LibraryDiff Diff(IEnumerable<KnownFile> known, IEnumerable<string> onDisk)
    {
        var knownByPath = known.ToDictionary(k => k.Path, StringComparer.OrdinalIgnoreCase);
        var disk = new HashSet<string>(onDisk, StringComparer.OrdinalIgnoreCase);
        List<string> added = [], changed = [], unchanged = [];

        foreach (var path in disk.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!knownByPath.TryGetValue(path, out var existing))
            {
                added.Add(path);
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length != existing.SizeBytes || info.LastWriteTimeUtc != existing.LastWriteUtc)
            {
                changed.Add(path);
            }
            else
            {
                unchanged.Add(path);
            }
        }

        return new LibraryDiff
        {
            New = added,
            Changed = changed,
            Unchanged = unchanged,
            Missing = knownByPath.Keys.Where(p => !disk.Contains(p)).Order(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }
}
