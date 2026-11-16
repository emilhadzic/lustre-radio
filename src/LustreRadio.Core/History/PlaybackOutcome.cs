namespace LustreRadio.Core.History;

/// <summary>Konacni status jednog emitovanja (plan faze B, 2.2, odluka D7-1).</summary>
public enum PlaybackStatus
{
    Played,
    Stopped,
    Unconfirmed,
    LegacyRecord,
}

/// <summary>Konacni ishod jednog <c>playback_id</c>, izracunat iz njegovih zapisa u dnevniku.</summary>
public sealed record PlaybackOutcome
{
    public required Guid PlaybackId { get; init; }

    public required long TrackId { get; init; }

    public required PlaybackStatus Status { get; init; }

    public StopReason Reason { get; init; }

    public UnconfirmedKind Unconfirmed { get; init; }

    public bool TransitionReached { get; init; }

    /// <summary>Vreme pocetka; kod NEPOTVRDJENO (a) vreme poslednjeg pouzdanog zapisa.</summary>
    public required DateTime StartedUtc { get; init; }

    public required string LastRid { get; init; }

    public byte Source { get; init; }

    public byte Part { get; init; }

    public byte Tier { get; init; }

    public string? Program { get; init; }

    /// <summary>Tekst statusa u bazi: odsvirana, prekinuta, nepotvrdjena-a/b/c, stari_zapis.</summary>
    public string StatusText => StatusName(Status, Unconfirmed);

    public static string StatusName(PlaybackStatus status, UnconfirmedKind unconfirmed) => status switch
    {
        PlaybackStatus.Played => "odsvirana",
        PlaybackStatus.Stopped => "prekinuta",
        PlaybackStatus.Unconfirmed => "nepotvrdjena-" + (JournalRecord.UnconfirmedName(unconfirmed) ?? "a"),
        _ => "stari_zapis",
    };
}

/// <summary>
/// Sabira zapise jednog emitovanja (ukljucujuci <c>pretapanje</c>, gde se pesma pojavljuje kao A ili kao B).
/// Pravila: status odredjuje SAMO zavrsni zapis (prvi po redosledu rid); <c>pretapanje</c> nije zavrsni;
/// ODSVIRANA samo uz <c>kraj</c>, PREKINUTO samo uz <c>prekinuta</c>; pad nikad ne pravi PREKINUTO.
/// </summary>
public sealed class PlaybackAggregate
{
    public PlaybackAggregate(Guid playbackId) => PlaybackId = playbackId;

    public Guid PlaybackId { get; }

    public Guid Run { get; private set; }

    public long TrackId { get; private set; }

    public bool Prepared { get; private set; }

    public bool Began { get; private set; }

    public bool TransitionReached { get; private set; }

    public DateTime? StartedUtc { get; private set; }

    public DateTime LastUtc { get; private set; }

    public JournalRecord? Terminal { get; private set; }

    /// <summary>Broj zavrsnih zapisa posle prvog (greska programa; prvi vazi).</summary>
    public int ExtraTerminals { get; private set; }

    public byte Source { get; private set; }

    public byte Part { get; private set; }

    public byte Tier { get; private set; }

    public string? Program { get; private set; }

    /// <summary>Primenjuje zapis (zapisi stizu poredjani po rid). <paramref name="asA"/>: pesma je A u <c>pretapanje</c>.</summary>
    public void Apply(in JournalRecord record, bool asA)
    {
        if (Run == Guid.Empty)
        {
            Run = record.Run;
        }

        var time = new DateTime(record.TimeTicks, DateTimeKind.Utc);
        if (time > LastUtc)
        {
            LastUtc = time;
        }

        switch (record.Kind)
        {
            case JournalKind.Prepared:
                Prepared = true;
                TrackId = record.Track;
                Source = record.Source;
                Part = record.Part;
                Tier = record.Tier;
                Program = record.Program;
                break;
            case JournalKind.Started:
                Began = true;
                StartedUtc ??= time;
                TrackId = TrackId != 0 ? TrackId : record.Track;
                break;
            case JournalKind.Crossfade when asA:
                TransitionReached = true;
                TrackId = TrackId != 0 ? TrackId : record.TrackA;
                break;
            case JournalKind.Crossfade:
                Began = true;
                StartedUtc ??= time;
                TrackId = TrackId != 0 ? TrackId : record.Track;
                break;
            case JournalKind.Ended or JournalKind.Stopped or JournalKind.Discarded or JournalKind.Unconfirmed:
                TrackId = TrackId != 0 ? TrackId : record.Track;
                if (Terminal is null)
                {
                    Terminal = record;
                }
                else
                {
                    ExtraTerminals++;
                }

                break;
        }
    }

    /// <summary>Podvrsta za NEPOTVRDJENO, ako zavrsnog zapisa nema.</summary>
    public UnconfirmedKind PendingKind =>
        TransitionReached ? UnconfirmedKind.TailUnknown : Began ? UnconfirmedKind.StartedEndUnknown : UnconfirmedKind.NotStarted;

    /// <summary>Konacni ishod; null ako nema zavrsnog zapisa ili ako je stavka odbacena (u bazu se ne upisuje).</summary>
    public PlaybackOutcome? Outcome()
    {
        if (Terminal is not { } t || t.Kind == JournalKind.Discarded)
        {
            return null;
        }

        var status = t.Kind switch
        {
            JournalKind.Ended => PlaybackStatus.Played,
            JournalKind.Stopped => PlaybackStatus.Stopped,
            _ => PlaybackStatus.Unconfirmed,
        };

        return new PlaybackOutcome
        {
            PlaybackId = PlaybackId,
            TrackId = TrackId,
            Status = status,
            Reason = status == PlaybackStatus.Stopped ? t.Reason : StopReason.None,
            Unconfirmed = status == PlaybackStatus.Unconfirmed ? t.Unconfirmed : UnconfirmedKind.None,
            TransitionReached = TransitionReached,
            StartedUtc = StartedUtc ?? (status == PlaybackStatus.Unconfirmed ? new DateTime(t.Frame > 0 ? t.Frame : t.TimeTicks, DateTimeKind.Utc) : LastUtc),
            LastRid = t.Rid,
            Source = Source,
            Part = Part,
            Tier = Tier,
            Program = Program,
        };
    }
}
