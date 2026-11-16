using LustreRadio.Core.Scheduling;

namespace LustreRadio.Core.Rotation;

/// <summary>Ono sto rotacija treba da zna o pesmi.</summary>
public sealed record TrackInfo
{
    public required string Id { get; init; }

    public required string Artist { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public double? Bpm { get; init; }

    public double DurationSeconds { get; init; } = 240;

    public bool Blocked { get; init; }

    /// <summary>Odgovor kapije licenci za ovu pesmu. Rotacija nikad ne bira pesmu koja nije dozvoljena.</summary>
    public bool Broadcastable { get; init; }
}

public sealed record RotationSettings
{
    /// <summary>Ciljni razmak izmedju dva pustanja iste pesme (podrazumevano 7 dana).</summary>
    public TimeSpan TrackGap { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Razmak izmedju pesama istog izvodjaca.</summary>
    public TimeSpan ArtistGap { get; init; } = TimeSpan.FromHours(2);
}

public enum MatchTier
{
    /// <summary>Zanr i tempo odgovaraju programu slota.</summary>
    Program,

    /// <summary>Srodan zanr, ili isti zanr van zadatog tempa.</summary>
    Compatible,

    /// <summary>Bilo koja dozvoljena pesma (kad nista drugo ne postoji).</summary>
    Any,
}

public sealed record PickResult
{
    public required TrackInfo Track { get; init; }

    public required MatchTier Tier { get; init; }

    /// <summary>Ciljni razmak nije mogao da se postuje.</summary>
    public bool TrackGapRelaxed { get; init; }

    public bool ArtistGapRelaxed { get; init; }

    public TimeSpan? SinceLastPlay { get; init; }
}

/// <summary>Sta je kada pusteno: osnova za razmake i za izbegavanje istih redosleda.</summary>
public sealed class RotationState
{
    private readonly Dictionary<string, DateTimeOffset> _trackLast = [];
    private readonly Dictionary<string, DateTimeOffset> _artistLast = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _followers = [];

    public string? PreviousTrackId { get; private set; }

    /// <summary>Belezi pustanje (ili rezervaciju stavke plana, B4). Vraca podatke za ponistavanje rezervacije.</summary>
    public RotationReservation Record(TrackInfo track, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(track);
        var reservation = new RotationReservation(track.Id, track.Artist, at, LastPlayed(track.Id), LastPlayedArtist(track.Artist), PreviousTrackId,
            PreviousTrackId is not null && !HasFollowedPrevious(track.Id));
        if (PreviousTrackId is not null)
        {
            if (!_followers.TryGetValue(PreviousTrackId, out var set))
            {
                _followers[PreviousTrackId] = set = [];
            }

            set.Add(track.Id);
        }

        _trackLast[track.Id] = at;
        _artistLast[track.Artist] = at;
        PreviousTrackId = track.Id;
        return reservation;
    }

    /// <summary>
    /// Ponistava rezervaciju odbacene stavke plana (B4: "odbacene stavke bez traga u rotaciji"): vraca prethodne vrednosti
    /// ako ih u medjuvremenu nista novije nije promenilo.
    /// </summary>
    public void Unrecord(RotationReservation r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (_trackLast.TryGetValue(r.TrackId, out var t) && t == r.At)
        {
            if (r.PreviousTrackLast is { } pt)
            {
                _trackLast[r.TrackId] = pt;
            }
            else
            {
                _trackLast.Remove(r.TrackId);
            }
        }

        if (_artistLast.TryGetValue(r.Artist, out var a) && a == r.At)
        {
            if (r.PreviousArtistLast is { } pa)
            {
                _artistLast[r.Artist] = pa;
            }
            else
            {
                _artistLast.Remove(r.Artist);
            }
        }

        if (r.AddedFollower && r.PreviousTrackId is not null && _followers.TryGetValue(r.PreviousTrackId, out var set))
        {
            set.Remove(r.TrackId);
        }

        if (PreviousTrackId == r.TrackId)
        {
            PreviousTrackId = r.PreviousTrackId;
        }
    }

    public DateTimeOffset? LastPlayed(string trackId) => _trackLast.TryGetValue(trackId, out var at) ? at : null;

    public DateTimeOffset? LastPlayedArtist(string artist) => _artistLast.TryGetValue(artist, out var at) ? at : null;

    /// <summary>Da li je ova pesma vec nekad isla odmah posle prethodne.</summary>
    public bool HasFollowedPrevious(string trackId) =>
        PreviousTrackId is not null && _followers.TryGetValue(PreviousTrackId, out var set) && set.Contains(trackId);
}

/// <summary>Rezervacija u rotaciji (stavka plana); sluzi za ponistavanje kad se stavka odbaci.</summary>
public sealed record RotationReservation(string TrackId, string Artist, DateTimeOffset At, DateTimeOffset? PreviousTrackLast, DateTimeOffset? PreviousArtistLast, string? PreviousTrackId, bool AddedFollower);

/// <summary>Srodni zanrovi: koriste se kad pesama trazenog zanra nema dovoljno.</summary>
public static class GenreCompatibility
{
    private static readonly string[][] Groups =
    [
        ["lounge", "soft lounge", "smooth jazz", "jazz", "bossa nova", "acoustic", "chillout", "ambient",
         "italian cafe", "french cafe", "italian lounge", "french lounge", "sunday brunch", "elegant chill"],
        ["modern lounge", "nu disco", "chill house", "jazzy house", "deep house", "soft deep house",
         "organic house", "melodic house", "sophisticated lounge", "luxury lounge", "elegant chill",
         "italian lounge", "french lounge"],
    ];

    public static string Normalize(string genre) => genre.Trim().ToLowerInvariant();

    public static bool AreCompatible(string a, string b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        return x == y || Groups.Any(g => g.Contains(x) && g.Contains(y));
    }

    public static MatchTier Classify(TrackInfo track, SlotProgram program)
    {
        var wanted = program.Genres.Select(Normalize).ToHashSet();
        var genres = track.Genres.Select(Normalize).ToList();
        var sameGenre = genres.Any(wanted.Contains);
        var bpmOk = track.Bpm is not double bpm || (bpm >= program.BpmMin && bpm <= program.BpmMax);

        if (sameGenre && bpmOk)
        {
            return MatchTier.Program;
        }

        if (sameGenre || genres.Any(g => wanted.Any(w => AreCompatible(g, w))))
        {
            return MatchTier.Compatible;
        }

        return MatchTier.Any;
    }
}
