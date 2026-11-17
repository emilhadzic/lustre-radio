using System.Globalization;
using System.Security.Cryptography;
using LustreRadio.Analysis;
using LustreRadio.Audio;
using LustreRadio.Core.Voice;
using LustreRadio.Data;

namespace LustreRadio.Playout;

/// <summary>Stanje najave: Nacrt -> Odobreno -> Arhivirano (spec. 15).</summary>
public enum VoiceStatus
{
    Draft,
    Approved,
    Archived,
}

/// <summary>
/// Licenca AI najave (tip "AI TTS", spec. 15 i D10). Bez svih obaveznih polja najava ne ide u eter.
/// Ovo je zapis koji vlasnik popunjava; program ne proverava da li je plan stvarno placen.
/// </summary>
public sealed record VoiceLicense
{
    /// <summary>Servis (npr. ElevenLabs, Azure).</summary>
    public string Service { get; init; } = string.Empty;

    /// <summary>Model i glas (npr. "Eleven v3, glas Lustre F").</summary>
    public string ModelAndVoice { get; init; } = string.Empty;

    /// <summary>Plan pretplate na dan generisanja (mora biti placen plan za komercijalnu upotrebu).</summary>
    public string Plan { get; init; } = string.Empty;

    /// <summary>Broj fakture ili drugi dokaz placenog plana na dan generisanja.</summary>
    public string PaymentProof { get; init; } = string.Empty;

    /// <summary>Datum generisanja.</summary>
    public string GeneratedOn { get; init; } = string.Empty;

    /// <summary>Gde je sacuvan snimak uslova koriscenja vazecih tog dana (fajl ili veza).</summary>
    public string TermsSnapshot { get; init; } = string.Empty;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Service) && !string.IsNullOrWhiteSpace(ModelAndVoice) && !string.IsNullOrWhiteSpace(Plan) &&
        !string.IsNullOrWhiteSpace(PaymentProof) && !string.IsNullOrWhiteSpace(GeneratedOn) && !string.IsNullOrWhiteSpace(TermsSnapshot);
}

/// <summary>Jedna najava u biblioteci (fajl je u folderu podataka, voice\).</summary>
public sealed record VoiceEntry
{
    public required string Id { get; init; }

    public string Text { get; init; } = string.Empty;

    public VoiceCategory Category { get; init; } = VoiceCategory.Ident;

    public VoiceLanguage Language { get; init; } = VoiceLanguage.Serbian;

    public VoiceKind Voice { get; init; } = VoiceKind.Female;

    /// <summary>Ime fajla u folderu voice\.</summary>
    public required string File { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    public double DurationSeconds { get; init; }

    public double? Lufs { get; init; }

    public double? PeakDbfs { get; init; }

    public VoiceStatus Status { get; init; } = VoiceStatus.Draft;

    public bool Enabled { get; init; } = true;

    public VoiceLicense License { get; init; } = new();

    public DateTimeOffset ImportedAt { get; init; }

    public string? ApprovedBy { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }
}

/// <summary>
/// Biblioteka najava u audio procesu (spec. 15, podela posla: UI pravi i preslusava, Engine obradjuje, cuva i emituje).
/// Uvoz kopira fajl u voice\, meri trajanje i glasnocu istim dekoderom kao muziku i racuna SHA-256.
/// U eter ide samo odobrena, ukljucena najava sa potpunom licencom, ciji fajl ima isti otisak kao pri odobravanju.
/// </summary>
public sealed class VoiceLibrary
{
    public const string SettingsKey = "voice_messages";

    /// <summary>Najava je za ovoliko glasnija od muzike (spec. 15: 1-2 LU).</summary>
    public const double LouderThanMusicLu = 1.5;

    private static readonly string[] AllowedExtensions = [".wav", ".mp3", ".flac", ".ogg", ".m4a"];

    private readonly LustreDatabase _db;
    private readonly string _folder;
    private readonly object _sync = new();
    private readonly Dictionary<string, bool> _hashOk = [];

    public VoiceLibrary(LustreDatabase db, string folder)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _folder = folder;
    }

    public string Folder => _folder;

    public IReadOnlyList<VoiceEntry> All()
    {
        lock (_sync)
        {
            return _db.Settings.GetJson<List<VoiceEntry>>(SettingsKey) ?? [];
        }
    }

    public string PathOf(VoiceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return System.IO.Path.Combine(_folder, entry.File);
    }

    /// <summary>Najave koje smeju u eter, u obliku koji koristi VoiceScheduler.</summary>
    public IReadOnlyList<VoiceMessage> Broadcastable() => All().Select(e => new VoiceMessage
    {
        Id = e.Id,
        Category = e.Category,
        Language = e.Language,
        Voice = e.Voice,
        DurationSeconds = e.DurationSeconds,
        Enabled = e.Enabled,
        Broadcastable = CanBroadcast(e),
    }).Where(m => m.Broadcastable && m.Enabled).ToList();

    /// <summary>D10: odobrena, ukljucena, potpuna licenca i isti fajl kao pri odobravanju.</summary>
    public bool CanBroadcast(VoiceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Status == VoiceStatus.Approved && entry.Enabled && entry.License.IsComplete && FileMatches(entry);
    }

    public VoiceEntry? Find(string id) => All().FirstOrDefault(e => e.Id == id);

    /// <summary>Uvoz fajla generisanog u servisu (rucni uvoz, spec. 15). Najava pocinje kao nacrt.</summary>
    public VoiceEntry Import(string sourcePath, string text, VoiceCategory category, VoiceLanguage language, VoiceKind voice, VoiceLicense license, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(license);
        var extension = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!System.IO.Path.IsPathFullyQualified(sourcePath) || !System.IO.File.Exists(sourcePath) || !AllowedExtensions.Contains(extension))
        {
            throw new ArgumentException("Fajl ne postoji ili nije zvucni fajl.", nameof(sourcePath));
        }

        Directory.CreateDirectory(_folder);
        var id = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture)[..12];
        var file = id + extension;
        var target = System.IO.Path.Combine(_folder, file);
        System.IO.File.Copy(sourcePath, target, overwrite: false);
        var (duration, lufs, peak) = Measure(target, format);
        var entry = new VoiceEntry
        {
            Id = id,
            Text = text.Trim(),
            Category = category,
            Language = language,
            Voice = voice,
            File = file,
            Sha256 = Hash(target),
            DurationSeconds = duration,
            Lufs = lufs,
            PeakDbfs = peak,
            License = license,
            ImportedAt = DateTimeOffset.Now,
        };
        Update(list => list.Add(entry));
        return entry;
    }

    /// <summary>Odobravanje: samo uz potpunu licencu; ko i kada se belezi.</summary>
    public bool Approve(string id, string approvedBy)
    {
        var ok = false;
        Update(list =>
        {
            var i = list.FindIndex(e => e.Id == id);
            if (i >= 0 && list[i].License.IsComplete && list[i].Status != VoiceStatus.Archived && FileMatches(list[i]))
            {
                list[i] = list[i] with { Status = VoiceStatus.Approved, ApprovedBy = approvedBy, ApprovedAt = DateTimeOffset.Now };
                ok = true;
            }
        });
        return ok;
    }

    public bool SetEnabled(string id, bool enabled) => Change(id, e => e with { Enabled = enabled });

    /// <summary>Brisanje ide u arhivu (moze da se vrati); fajl ostaje.</summary>
    public bool Archive(string id) => Change(id, e => e with { Status = VoiceStatus.Archived, Enabled = false });

    public bool Restore(string id) => Change(id, e => e.Status == VoiceStatus.Archived ? e with { Status = VoiceStatus.Draft } : e);

    private bool Change(string id, Func<VoiceEntry, VoiceEntry> change)
    {
        var ok = false;
        Update(list =>
        {
            var i = list.FindIndex(e => e.Id == id);
            if (i >= 0)
            {
                list[i] = change(list[i]);
                ok = true;
            }
        });
        return ok;
    }

    private void Update(Action<List<VoiceEntry>> change)
    {
        lock (_sync)
        {
            var list = _db.Settings.GetJson<List<VoiceEntry>>(SettingsKey) ?? [];
            change(list);
            _db.Settings.SetJson(SettingsKey, list);
        }
    }

    private bool FileMatches(VoiceEntry entry)
    {
        var path = PathOf(entry);
        lock (_hashOk)
        {
            var key = $"{entry.Id}|{(System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path).Ticks : 0)}";
            if (!_hashOk.TryGetValue(key, out var ok))
            {
                ok = System.IO.File.Exists(path) && string.Equals(Hash(path), entry.Sha256, StringComparison.OrdinalIgnoreCase);
                _hashOk[key] = ok;
            }

            return ok;
        }
    }

    private static string Hash(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static (double Duration, double? Lufs, double? Peak) Measure(string path, AudioFormat format)
    {
        using var source = Decoders.Open(path, format);
        var meter = new LoudnessMeter(format.SampleRate);
        var buffer = new float[4096 * format.Channels];
        long frames = 0;
        double peak = 0;
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            var span = buffer.AsSpan(0, read * format.Channels);
            meter.Add(span);
            foreach (var v in span)
            {
                peak = Math.Max(peak, Math.Abs(v));
            }

            frames += read;
        }

        return (frames / (double)format.SampleRate, meter.IntegratedLufs(), peak > 0 ? 20 * Math.Log10(peak) : null);
    }
}
