namespace LustreRadio.Core.Fallback;

/// <summary>Odakle engine uzima sledecu pesmu (spec. deo 10). Veci broj = dublja rezerva.</summary>
public enum PlaybackSource
{
    /// <summary>Normalno: rotacija nad bazom.</summary>
    Rotation = 0,

    /// <summary>Greska u izboru pesme: najduze nepustana iz trenutnog miksa.</summary>
    SimpleFromMix = 3,

    /// <summary>Baza nedostupna: kesirana lista proverenih pesama.</summary>
    CachedList = 4,

    /// <summary>Sve drugo otkazalo: folder "Rezerva".</summary>
    EmergencyFolder = 6,

    /// <summary>Nema nicega sto sme da se pusti.</summary>
    Nothing = 99,
}

public sealed record SourceHealth
{
    public bool DatabaseOk { get; init; }

    public bool RotationOk { get; init; }

    public bool CachedListAvailable { get; init; }

    public bool EmergencyFolderAvailable { get; init; }
}

public static class PlaybackSourceSelector
{
    public static PlaybackSource Choose(SourceHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (health.DatabaseOk)
        {
            return health.RotationOk ? PlaybackSource.Rotation : PlaybackSource.SimpleFromMix;
        }

        if (health.CachedListAvailable)
        {
            return PlaybackSource.CachedList;
        }

        return health.EmergencyFolderAvailable ? PlaybackSource.EmergencyFolder : PlaybackSource.Nothing;
    }
}

/// <summary>
/// Stavka rezervne liste. Upisuje se samo dok je baza ispravna i samo za proverene pesme;
/// nosi dovoljno podataka da se i bez baze utvrdi da je fajl isti (spec. D10).
/// </summary>
public sealed record FallbackEntry
{
    public required string Path { get; init; }

    public required string ContentHash { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTime LastWriteUtc { get; init; }

    /// <summary>Brza provera pre pustanja: ista velicina i isti datum izmene.</summary>
    public bool MatchesFile(long sizeBytes, DateTime lastWriteUtc) =>
        sizeBytes == SizeBytes && lastWriteUtc == LastWriteUtc;
}

/// <summary>Pesma koja tri puta ne moze da se pusti izlazi iz rotacije dok je admin ne pregleda.</summary>
public sealed class TrackFaultTracker(int quarantineAfter = 3)
{
    private readonly Dictionary<string, int> _faults = [];

    public int FaultsOf(string trackId) => _faults.GetValueOrDefault(trackId);

    public bool IsQuarantined(string trackId) => FaultsOf(trackId) >= quarantineAfter;

    /// <summary>Belezi gresku. Vraca true ako je pesma ovim izasla iz rotacije.</summary>
    public bool RecordFault(string trackId)
    {
        var count = FaultsOf(trackId) + 1;
        _faults[trackId] = count;
        return count == quarantineAfter;
    }

    /// <summary>Pesma je uspesno pustena do kraja: brojac se ponistava.</summary>
    public void RecordSuccess(string trackId) => _faults.Remove(trackId);

    public void Release(string trackId) => _faults.Remove(trackId);
}

/// <summary>
/// Vise padova u kratkom vremenu = sigurni rezim (spec. 2.2: 3 pada za 10 minuta).
/// Vreme je sa stoperice, da pomeranje sata ne utice.
/// </summary>
public sealed class CrashLoopDetector(int maxCrashes, TimeSpan window)
{
    private readonly Queue<TimeSpan> _crashes = new();

    public CrashLoopDetector()
        : this(3, TimeSpan.FromMinutes(10))
    {
    }

    /// <summary>Belezi pad. Vraca true ako treba preci u sigurni rezim.</summary>
    public bool RecordCrash(TimeSpan monotonicNow)
    {
        _crashes.Enqueue(monotonicNow);
        while (_crashes.Count > 0 && monotonicNow - _crashes.Peek() > window)
        {
            _crashes.Dequeue();
        }

        return _crashes.Count >= maxCrashes;
    }
}
