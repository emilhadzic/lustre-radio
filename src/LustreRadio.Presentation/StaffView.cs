using LustreRadio.Ipc;

namespace LustreRadio.Presentation;

public enum LinkStatus
{
    Connecting,
    Connected,
    VersionMismatch,
}

public enum Health
{
    Good,
    Attention,
    Problem,
}

/// <summary>
/// Sve sto ekran za zaposlene prikazuje, izracunato iz stanja koje salje audio proces.
/// Interfejs ne cuva nikakvo svoje stanje radija (spec. 2.6).
/// </summary>
public sealed record StaffView
{
    public required string Title { get; init; }

    public required string Artist { get; init; }

    public required string DateLine { get; init; }

    public required string StatusLine { get; init; }

    public required Health Health { get; init; }

    /// <summary>Program i deo dana; prazno kad ne svira.</summary>
    public required string ProgramLine { get; init; }

    /// <summary>Vreme do gasenja; prazno kad ne svira.</summary>
    public required string ClosesLine { get; init; }

    public required double Progress { get; init; }

    public required string PositionText { get; init; }

    public required string DurationText { get; init; }

    /// <summary>Veza postoji; kad ne postoji, prikaz je zasivljen i kontrole ne rade.</summary>
    public required bool Live { get; init; }

    public required bool CanPlayPause { get; init; }

    public required bool CanNext { get; init; }

    public required bool ShowPlay { get; init; }

    public required double Volume { get; init; }

    public required double VolumeMin { get; init; }

    public required double VolumeMax { get; init; }

    public required string Language { get; init; }

    public static StaffView From(RadioState? state, LinkStatus link, DateTime nowLocal, DateTime nowUtc)
    {
        var language = Texts.Normalize(state?.StaffLanguage);
        var live = link == LinkStatus.Connected && state is not null;
        var playing = state is { OnAir: true, TrackId: not null };

        var position = 0.0;
        var duration = 0.0;
        if (state is not null && playing)
        {
            duration = Math.Max(0, state.DurationSeconds);
            position = state.PositionSeconds;
            if (live && !state.Paused && state.Output == "Playing")
            {
                // izmedju dve poruke pozicija se racuna lokalno (najvise 10 s unapred, da zastoj veze ne "odvede" traku)
                position += Math.Clamp((nowUtc - state.AsOfUtc).TotalSeconds, 0, 10);
            }

            position = duration > 0 ? Math.Clamp(position, 0, duration) : Math.Max(0, position);
        }

        var (status, health) = Status(state, link, language);
        return new StaffView
        {
            Title = playing ? NonEmpty(state!.Title, Texts.Get(language, T.NothingPlaying)) : Texts.Get(language, T.NothingPlaying),
            Artist = playing ? NonEmpty(state!.Artist, Texts.Get(language, T.UnknownArtist)) : string.Empty,
            DateLine = Texts.LongDate(language, nowLocal),
            StatusLine = status,
            Health = health,
            ProgramLine = playing ? ProgramText(state!, language) : string.Empty,
            ClosesLine = state is { OnAir: true, ClosesAtLocal: { } closes } ? ClosesText(closes, state.NowLocal, language) : string.Empty,
            Progress = duration > 0 ? position / duration : 0,
            PositionText = playing ? Clock(position) : string.Empty,
            DurationText = playing && duration > 0 ? Clock(duration) : string.Empty,
            Live = live,
            CanPlayPause = live && playing,
            CanNext = live && playing && !state!.Paused,
            ShowPlay = state?.Paused == true,
            Volume = state?.Volume ?? 0,
            VolumeMin = state?.VolumeMin ?? 0,
            VolumeMax = state is null ? 1 : Math.Max(state.VolumeMin, state.VolumeMax),
            Language = language,
        };
    }

    public static string Clock(double seconds)
    {
        var total = (int)Math.Floor(Math.Max(0, seconds));
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}"
            : $"{total / 60}:{total % 60:00}";
    }

    private static (string Text, Health Health) Status(RadioState? state, LinkStatus link, string language)
    {
        if (link == LinkStatus.VersionMismatch)
        {
            return (Texts.Get(language, T.VersionMismatch), Health.Problem);
        }

        if (link != LinkStatus.Connected || state is null)
        {
            return (Texts.Get(language, T.Connecting), Health.Attention);
        }

        if (!state.OnAir)
        {
            if (state.OpensAtLocal is { } opens)
            {
                return (Texts.Format(language, T.StatusClosedOpensAt, Texts.DayForSentence(language, opens), Texts.Time(opens)), Health.Good);
            }

            // radno vreme je, a ne svira
            return state.BroadcastableTracks == 0
                ? (Texts.Get(language, T.StatusNoTracks), Health.Problem)
                : (Texts.Get(language, T.StatusStarting), Health.Attention);
        }

        if (state.Output == "Disconnected")
        {
            return (Texts.Get(language, T.StatusNoSpeaker), Health.Problem);
        }

        if (state.Paused)
        {
            return (Texts.Get(language, T.StatusPaused), Health.Attention);
        }

        if (state.Output != "Playing")
        {
            return (Texts.Get(language, T.StatusStarting), Health.Attention);
        }

        return state.SafeMode
            ? (Texts.Get(language, T.StatusPlaying) + " · " + Texts.Get(language, T.StatusSafeMode), Health.Attention)
            : (Texts.Get(language, T.StatusPlaying), Health.Good);
    }

    private static string ProgramText(RadioState state, string language)
    {
        var part = state.DayPart switch
        {
            "Morning" => Texts.Get(language, T.PartMorning),
            "Afternoon" => Texts.Get(language, T.PartAfternoon),
            "Evening" => Texts.Get(language, T.PartEvening),
            _ => null,
        };
        var program = string.IsNullOrWhiteSpace(state.Program) ? null : state.Program;
        return (part, program) switch
        {
            (not null, not null) => $"{part} · {program}",
            (not null, null) => part,
            (null, not null) => program,
            _ => string.Empty,
        };
    }

    private static string ClosesText(DateTime closes, DateTime engineNow, string language)
    {
        var minutes = (int)Math.Ceiling(Math.Max(0, (closes - engineNow).TotalMinutes));
        var remaining = minutes >= 60
            ? Texts.Format(language, T.InHoursMinutes, minutes / 60, minutes % 60)
            : Texts.Format(language, T.InMinutes, minutes);
        return Texts.Format(language, T.ClosesAt, Texts.Time(closes), remaining);
    }

    private static string NonEmpty(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text;
}
