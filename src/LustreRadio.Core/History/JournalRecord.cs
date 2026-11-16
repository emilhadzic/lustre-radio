using System.Globalization;

namespace LustreRadio.Core.History;

/// <summary>Vrste zapisa u dnevniku emitovanja (plan faze B, 2.2).</summary>
public enum JournalKind : byte
{
    None = 0,
    Prepared = 1,
    Started = 2,
    Crossfade = 3,
    Ended = 4,
    Stopped = 5,
    Discarded = 6,
    Shutdown = 7,
    ProgramCrash = 8,
    Unconfirmed = 9,
    Lost = 10,
}

/// <summary>Razlog prekida, odbacivanja ili gasenja.</summary>
public enum StopReason : byte
{
    None = 0,
    Skip = 1,
    FileError = 2,
    FileUnavailable = 3,
    UnexpectedEnd = 4,
    DecoderStall = 5,
    Closing = 6,
    NotLoaded = 7,
    Shutdown = 8,
}

/// <summary>Podvrsta za NEPOTVRDJENO – PREKID PROGRAMA.</summary>
public enum UnconfirmedKind : byte
{
    None = 0,

    /// <summary>(a) nije potvrdjen pocetak.</summary>
    NotStarted = 1,

    /// <summary>(b) krenula, kraj nepoznat.</summary>
    StartedEndUnknown = 2,

    /// <summary>(c) stigla do prelaza, kraj repa nepoznat.</summary>
    TailUnknown = 3,
}

/// <summary>
/// Jedan zapis istorije, stalne velicine i bez teksta osim kratkog naziva programa (crna kutija, 128 bajtova).
/// Isti zapis ide u crnu kutiju i, kao jedan red, u <c>emitovanje.jsonl</c>.
/// Za <c>prekid_programa</c>: <see cref="PlaybackIdA"/> je oznaka pokretanja koje je palo, <see cref="Frame"/> vreme
/// poslednjeg zapisa tog pokretanja (ticks), <see cref="TimeTicks"/> vreme novog pokretanja.
/// Za <c>gubitak</c>: <see cref="Track"/> je broj izgubljenih dogadjaja, <see cref="Frame"/> i <see cref="TimeTicks"/> vreme od–do.
/// </summary>
public struct JournalRecord
{
    /// <summary>Najvise bajtova (UTF-8) za naziv programa u crnoj kutiji.</summary>
    public const int ProgramBytes = 24;

    public Guid Run;
    public long Seq;
    public JournalKind Kind;
    public StopReason Reason;
    public UnconfirmedKind Unconfirmed;

    /// <summary>Izvor programa (+1; 0 = nema).</summary>
    public byte Source;

    /// <summary>Deo dana (+1; 0 = nema).</summary>
    public byte Part;

    /// <summary>Nivo izbora u rotaciji (+1; 0 = nema).</summary>
    public byte Tier;

    public Guid PlaybackId;
    public Guid PlaybackIdA;
    public long Track;
    public long TrackA;
    public long Frame;
    public long TimeTicks;
    public int PositionMs;
    public string? Program;

    /// <summary><c>journal_record_id</c>: oznaka pokretanja + redni broj.</summary>
    public readonly string Rid => Rid(Run, Seq);

    public static string Rid(Guid run, long seq) => run.ToString("N") + ":" + seq.ToString(CultureInfo.InvariantCulture);

    public readonly bool IsTerminal => Kind is JournalKind.Ended or JournalKind.Stopped or JournalKind.Discarded or JournalKind.Unconfirmed;

    public static string KindName(JournalKind kind) => kind switch
    {
        JournalKind.Prepared => "pripremljena",
        JournalKind.Started => "pocetak",
        JournalKind.Crossfade => "pretapanje",
        JournalKind.Ended => "kraj",
        JournalKind.Stopped => "prekinuta",
        JournalKind.Discarded => "odbacena",
        JournalKind.Shutdown => "gasenje",
        JournalKind.ProgramCrash => "prekid_programa",
        JournalKind.Unconfirmed => "nepotvrdjeno",
        JournalKind.Lost => "gubitak",
        _ => "nepoznato",
    };

    public static JournalKind ParseKind(string? name)
    {
        foreach (var kind in Enum.GetValues<JournalKind>())
        {
            if (kind != JournalKind.None && KindName(kind) == name)
            {
                return kind;
            }
        }

        return JournalKind.None;
    }

    public static string? ReasonName(StopReason reason) => reason switch
    {
        StopReason.Skip => "sledeca",
        StopReason.FileError => "greska-fajla",
        StopReason.FileUnavailable => "fajl-nedostupan",
        StopReason.UnexpectedEnd => "neocekivan-kraj",
        StopReason.DecoderStall => "zastoj-dekodera",
        StopReason.Closing => "zatvaranje",
        StopReason.NotLoaded => "nije-ucitana",
        StopReason.Shutdown => "gasenje",
        _ => null,
    };

    public static StopReason ParseReason(string? name)
    {
        foreach (var reason in Enum.GetValues<StopReason>())
        {
            if (reason != StopReason.None && ReasonName(reason) == name)
            {
                return reason;
            }
        }

        return StopReason.None;
    }

    public static string? UnconfirmedName(UnconfirmedKind kind) => kind switch
    {
        UnconfirmedKind.NotStarted => "a",
        UnconfirmedKind.StartedEndUnknown => "b",
        UnconfirmedKind.TailUnknown => "c",
        _ => null,
    };

    public static UnconfirmedKind ParseUnconfirmed(string? name) => name switch
    {
        "a" => UnconfirmedKind.NotStarted,
        "b" => UnconfirmedKind.StartedEndUnknown,
        "c" => UnconfirmedKind.TailUnknown,
        _ => UnconfirmedKind.None,
    };
}
