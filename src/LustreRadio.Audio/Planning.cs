namespace LustreRadio.Audio;

/// <summary>Stavka plana (plan faze B, 2.8): izabrana pesma koja jos nije otvorena. Pravi je planer na koordinatoru.</summary>
public sealed class PlannedItem
{
    public required string TrackId { get; init; }

    public Guid PlaybackId { get; init; } = Guid.CreateVersion7();

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public byte Source { get; init; }

    public byte Part { get; init; }

    public byte Tier { get; init; }

    public string? Program { get; init; }

    /// <summary>Putanja fajla (za probu diska ako se otvaranje zaglavi).</summary>
    public string? Path { get; init; }

    /// <summary>Podaci planera (rezervacija u rotaciji, zapis iz baze).</summary>
    public object? Tag { get; init; }

    /// <summary>Oznaka stavke koja je najava, ne pesma.</summary>
    public const string VoicePrefix = "voice:";

    /// <summary>Broj pesme za istoriju; najava dobija -1 (ne ulazi u istoriju pesama ni u rotaciju).</summary>
    public long TrackNumber => TrackId.StartsWith(VoicePrefix, StringComparison.Ordinal) ? -1
        : long.TryParse(TrackId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
}

/// <summary>Sta je u planu (za ekran "Sledeca" i "Proveru sistema"): stvarni broj stavki i razlog kad ih je manje od tri.</summary>
public sealed record PlanSnapshot(IReadOnlyList<PlannedItem> Items, string? ShortReason)
{
    public static readonly PlanSnapshot Empty = new([], null);
}

/// <summary>
/// Planer (B4): bira sledece pesme na niti koordinatora (bez diska i bez baze), a otvara ih radna nit.
/// </summary>
public interface IPlanner
{
    /// <summary>Koliko stavki plan sme da ima (0–3, po broju dozvoljenih pesama; 2.8) i razlog kad je manje od 3.</summary>
    (int Size, string? Reason) Capacity();

    /// <summary>Bira sledecu pesmu posle zadatih (tekuca + plan). Rezervise je u rotaciji. Null ako nema.</summary>
    PlannedItem? Select(IReadOnlyList<string> before);

    /// <summary>Radna nit: otvara pesmu (provera fajla, dekoder). Null ako ne moze.</summary>
    QueuedTrack? Open(PlannedItem item);

    /// <summary>Stavka je odbacena pre nego sto je krenula: rezervacija u rotaciji se ponistava (bez traga).</summary>
    void Release(PlannedItem item);

    /// <summary>Da li stavka i dalje sme: licenca, fajl u katalogu, isti program kao sada.</summary>
    bool IsValid(PlannedItem item);
}

/// <summary>
/// Planer iz jedne funkcije (testovi zvuka, Bench): izbor i otvaranje su jedno (na radnoj niti), plan ima jednu stavku.
/// </summary>
public sealed class FuncPlanner(Func<QueuedTrack?> next) : IPlanner
{
    public (int Size, string? Reason) Capacity() => (1, null);

    public PlannedItem? Select(IReadOnlyList<string> before) => new() { TrackId = "?", PlaybackId = Guid.Empty };

    public QueuedTrack? Open(PlannedItem item) => next();

    public void Release(PlannedItem item)
    {
    }

    public bool IsValid(PlannedItem item) => true;
}
