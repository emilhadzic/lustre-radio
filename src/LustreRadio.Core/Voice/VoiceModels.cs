using LustreRadio.Core.Scheduling;

namespace LustreRadio.Core.Voice;

public enum VoiceCategory
{
    /// <summary>Kratka identifikacija stanice (2-5 s).</summary>
    Ident,

    /// <summary>Dobrodoslica (10-20 s).</summary>
    Welcome,

    /// <summary>Duza poruka (20-40 s).</summary>
    Long,
    Seasonal,
    Holiday,
    Event,
}

public enum VoiceLanguage
{
    Serbian,
    English,
}

public enum VoiceKind
{
    Female,
    Male,
}

public sealed record VoiceMessage
{
    public required string Id { get; init; }

    public required VoiceCategory Category { get; init; }

    public required VoiceLanguage Language { get; init; }

    public required VoiceKind Voice { get; init; }

    public double DurationSeconds { get; init; } = 5;

    /// <summary>Periodi dana u kojima sme da ide. Prazno = svi.</summary>
    public IReadOnlyCollection<DayPart> AllowedParts { get; init; } = [];

    public DateOnly? ValidFrom { get; init; }

    public DateOnly? ValidTo { get; init; }

    /// <summary>Ukljucena u administraciji.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Odgovor kapije licenci. Poruka koja nije dozvoljena nikad se ne bira.</summary>
    public bool Broadcastable { get; init; }
}

/// <summary>Intervali za jedan period dana.</summary>
public sealed record VoicePartRules
{
    public TimeSpan IdentMin { get; init; } = TimeSpan.FromMinutes(45);

    public TimeSpan IdentMax { get; init; } = TimeSpan.FromMinutes(60);

    public TimeSpan LongMin { get; init; } = TimeSpan.FromHours(2);

    public TimeSpan LongMax { get; init; } = TimeSpan.FromHours(3);

    /// <summary>Udeo poruka na srpskom (ostalo engleski), kad postoje obe.</summary>
    public double SerbianShare { get; init; } = 0.6;
}

public sealed record VoiceRules
{
    public bool Enabled { get; init; } = true;

    public VoicePartRules Morning { get; init; } = new();

    public VoicePartRules Afternoon { get; init; } = new();

    public VoicePartRules Evening { get; init; } = new() { SerbianShare = 0.5 };

    public int MaxPerHour { get; init; } = 2;

    public TimeSpan QuietAfterOpen { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan QuietBeforeClose { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Zeljeni razmak pre ponavljanja iste kratke poruke.</summary>
    public TimeSpan IdentRepeatGap { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Zeljeni razmak pre ponavljanja iste duze poruke.</summary>
    public TimeSpan LongRepeatGap { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Koliko puta se poruka pomera da ne razbije dobar DJ prelaz.</summary>
    public int MaxDefersForDjPair { get; init; } = 2;

    public bool SuppressDuringOverride { get; init; } = true;

    public VoicePartRules For(DayPart part) => part switch
    {
        DayPart.Morning => Morning,
        DayPart.Afternoon => Afternoon,
        _ => Evening,
    };
}

/// <summary>Granica izmedju dve pesme: jedino mesto gde poruka sme da udje.</summary>
public sealed record VoiceBoundary
{
    public required DateTime NowLocal { get; init; }

    public required DayPart Part { get; init; }

    public required DateTime OpenedAtLocal { get; init; }

    public required DateTime ClosesAtLocal { get; init; }

    public bool OverrideActive { get; init; }

    /// <summary>Sledeci prelaz je dobar DJ par (poravnati udarci, visoka kompatibilnost).</summary>
    public bool StrongDjPair { get; init; }
}

public sealed record VoiceDecision
{
    public VoiceMessage? Message { get; init; }

    /// <summary>Poruka je dospela, ali je pomerena da ne razbije DJ par.</summary>
    public bool Deferred { get; init; }

    public required string Reason { get; init; }
}
