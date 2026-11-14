using LustreRadio.Core.Scheduling;

namespace LustreRadio.Core.Rotation;

/// <summary>
/// Bira sledecu pesmu (spec. deo 8). Redosled popustanja kad pesama nema dovoljno:
///  1. pesma iz programa slota, uz pun razmak za pesmu i izvodjaca;
///  2. srodan zanr, uz pun razmak;
///  3. isto, bez razmaka za izvodjaca;
///  4. tek onda se smanjuje razmak za pesmu: bira se medju onima koje najduze nisu pustane.
/// Ista pesma nikad ne ide dvaput zaredom ako postoji bilo koja druga.
/// Pesme koje kapija licenci ne dozvoljava ili su blokirane ne ulaze u izbor.
/// </summary>
public sealed class RotationEngine(Random random)
{
    public PickResult? Pick(IReadOnlyList<TrackInfo> library, RotationState state, SlotProgram program, DateTimeOffset now, RotationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(settings);

        var tiers = new List<TrackInfo>[] { [], [], [] };
        foreach (var track in library)
        {
            if (track.Broadcastable && !track.Blocked)
            {
                tiers[(int)GenreCompatibility.Classify(track, program)].Add(track);
            }
        }

        bool NotPrevious(TrackInfo t) => t.Id != state.PreviousTrackId;
        bool TrackGapOk(TrackInfo t) => state.LastPlayed(t.Id) is not { } last || now - last >= settings.TrackGap;
        bool ArtistGapOk(TrackInfo t) => state.LastPlayedArtist(t.Artist) is not { } last || now - last >= settings.ArtistGap;

        MatchTier[] styleTiers = [MatchTier.Program, MatchTier.Compatible];

        // 1 i 2: sva pravila
        foreach (var tier in styleTiers)
        {
            var pool = tiers[(int)tier].Where(t => NotPrevious(t) && TrackGapOk(t) && ArtistGapOk(t)).ToList();
            if (pool.Count > 0)
            {
                return Result(Choose(pool, state), tier, state, now, trackGapRelaxed: false, artistGapRelaxed: false);
            }
        }

        // 3: bez razmaka za izvodjaca
        foreach (var tier in styleTiers)
        {
            var pool = tiers[(int)tier].Where(t => NotPrevious(t) && TrackGapOk(t)).ToList();
            if (pool.Count > 0)
            {
                return Result(Choose(pool, state), tier, state, now, trackGapRelaxed: false, artistGapRelaxed: true);
            }
        }

        // 4: redom po nivoima (program, srodni, bilo koja dozvoljena)
        foreach (var tier in Enum.GetValues<MatchTier>())
        {
            var candidates = tiers[(int)tier].Where(NotPrevious).ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            // Pesme koje jos postuju ciljni razmak biraju se nasumicno. Ovo je redovan put za
            // biblioteku bez zanrova u oznakama, gde su sve pesme na nivou "bilo koja".
            var fresh = candidates.Where(TrackGapOk).ToList();
            if (fresh.Count > 0)
            {
                var freshWithArtistGap = fresh.Where(ArtistGapOk).ToList();
                return Result(
                    Choose(freshWithArtistGap.Count > 0 ? freshWithArtistGap : fresh, state), tier, state, now,
                    trackGapRelaxed: false, artistGapRelaxed: freshWithArtistGap.Count == 0);
            }

            // Ciljni razmak ne moze da se postuje: bira se nasumicno medju najduze nepustanima
            // (cetvrtina, a bar dve), da razmak bude sto veci, a redosled ne uvek isti.
            var shuffled = candidates.Select(t => (Track: t, Key: random.Next())).ToList();
            var oldest = shuffled
                .OrderBy(x => state.LastPlayed(x.Track.Id) ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.Key)
                .Take(Math.Max(Math.Min(2, candidates.Count), candidates.Count / 4))
                .Select(x => x.Track)
                .ToList();
            var withArtistGap = oldest.Where(ArtistGapOk).ToList();
            var chosen = Choose(withArtistGap.Count > 0 ? withArtistGap : oldest, state);
            return Result(chosen, tier, state, now, trackGapRelaxed: true, artistGapRelaxed: withArtistGap.Count == 0);
        }

        // postoji samo jedna dozvoljena pesma: bolje ona ponovo nego tisina
        var only = tiers.SelectMany(t => t).FirstOrDefault();
        return only is null ? null : Result(only, GenreCompatibility.Classify(only, program), state, now, true, true);
    }

    private TrackInfo Choose(List<TrackInfo> pool, RotationState state)
    {
        // izbegavaj par koji je vec isao istim redom, ako postoji izbor
        var fresh = pool.Where(t => !state.HasFollowedPrevious(t.Id)).ToList();
        var from = fresh.Count > 0 ? fresh : pool;
        return from[random.Next(from.Count)];
    }

    private static PickResult Result(TrackInfo track, MatchTier tier, RotationState state, DateTimeOffset now, bool trackGapRelaxed, bool artistGapRelaxed) => new()
    {
        Track = track,
        Tier = tier,
        TrackGapRelaxed = trackGapRelaxed,
        ArtistGapRelaxed = artistGapRelaxed,
        SinceLastPlay = state.LastPlayed(track.Id) is { } last ? now - last : null,
    };
}

/// <summary>Jedan red ekrana "Zdravlje biblioteke".</summary>
public sealed record SlotHealth
{
    public required DayOfWeek Day { get; init; }

    public required DayPart Part { get; init; }

    public required string ProgramName { get; init; }

    /// <summary>Dozvoljene pesme koje odgovaraju programu slota.</summary>
    public required int MatchingTracks { get; init; }

    /// <summary>Dodatne dozvoljene pesme srodnih zanrova.</summary>
    public required int CompatibleTracks { get; init; }

    /// <summary>Koliko pesama stane u jedno trajanje slota.</summary>
    public required int PlaysPerOccurrence { get; init; }

    /// <summary>Koliko pesama treba da se ciljni razmak postuje samo pesmama iz programa.</summary>
    public required int NeededForTargetGap { get; init; }

    public required int Missing { get; init; }
}

/// <summary>
/// Procena po slotu: koliko pesama fali do ciljnog razmaka. Pojednostavljenje: racuna svaki slot
/// za sebe, kao da njegove pesme ne sviraju u drugim slotovima; stvarni razmak moze biti manji
/// ako iste pesme odgovaraju vecem broju slotova.
/// </summary>
public static class LibraryHealth
{
    public static IReadOnlyList<SlotHealth> Compute(WeeklySchedule schedule, IReadOnlyList<TrackInfo> library, RotationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(settings);

        var allowed = library.Where(t => t.Broadcastable && !t.Blocked).ToList();
        var averageSeconds = allowed.Count > 0 ? allowed.Average(t => t.DurationSeconds) : 240.0;
        var occurrencesWithinGap = Math.Max(1, (int)Math.Ceiling(settings.TrackGap.TotalDays / 7.0));
        var result = new List<SlotHealth>();

        foreach (var dayOfWeek in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            var day = schedule.Days[dayOfWeek];
            foreach (var part in Enum.GetValues<DayPart>())
            {
                var (start, end) = part switch
                {
                    DayPart.Morning => (day.Times.Open, day.Times.AfternoonStart),
                    DayPart.Afternoon => (day.Times.AfternoonStart, day.Times.EveningStart),
                    _ => (day.Times.EveningStart, day.Times.Close),
                };
                var program = day.ProgramFor(part);
                var plays = day.Closed ? 0 : (int)Math.Ceiling((end - start).TotalSeconds / averageSeconds);
                var needed = plays * occurrencesWithinGap;
                var matching = allowed.Count(t => GenreCompatibility.Classify(t, program) == MatchTier.Program);
                var compatible = allowed.Count(t => GenreCompatibility.Classify(t, program) == MatchTier.Compatible);

                result.Add(new SlotHealth
                {
                    Day = dayOfWeek,
                    Part = part,
                    ProgramName = program.Name,
                    MatchingTracks = matching,
                    CompatibleTracks = compatible,
                    PlaysPerOccurrence = plays,
                    NeededForTargetGap = needed,
                    Missing = Math.Max(0, needed - matching),
                });
            }
        }

        return result;
    }
}
