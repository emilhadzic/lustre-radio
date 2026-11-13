namespace LustreRadio.Core.Scheduling;

public enum DayPart
{
    Morning,
    Afternoon,
    Evening,
}

public enum TransitionMode
{
    Standard,
    Smart,
    DjMix,
}

/// <summary>Granice dana. Podrazumevano 07:00 / 11:00 / 17:00 / 23:30. Sve unutar istog dana.</summary>
public sealed record DayTimes
{
    public TimeOnly Open { get; init; } = new(7, 0);

    public TimeOnly AfternoonStart { get; init; } = new(11, 0);

    public TimeOnly EveningStart { get; init; } = new(17, 0);

    public TimeOnly Close { get; init; } = new(23, 30);
}

/// <summary>Muzicki program jednog slota (dan x period): stil, tempo, intenzitet, miks, prelazi.</summary>
public sealed record SlotProgram
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> Genres { get; init; }

    public int BpmMin { get; init; } = 60;

    public int BpmMax { get; init; } = 130;

    /// <summary>Intenzitet 1 (najmirnije) do 5 (najzivlje).</summary>
    public int Energy { get; init; } = 3;

    /// <summary>Konkretan miks, ako je izabran; inace se bira po zanrovima.</summary>
    public string? MixId { get; init; }

    public TransitionMode Transition { get; init; } = TransitionMode.Smart;
}

public sealed record DaySchedule
{
    public DayTimes Times { get; init; } = new();

    public required SlotProgram Morning { get; init; }

    public required SlotProgram Afternoon { get; init; }

    public required SlotProgram Evening { get; init; }

    /// <summary>Lokal ne radi tog dana (npr. praznik).</summary>
    public bool Closed { get; init; }

    public SlotProgram ProgramFor(DayPart part) => part switch
    {
        DayPart.Morning => Morning,
        DayPart.Afternoon => Afternoon,
        _ => Evening,
    };
}

/// <summary>Praznik ili poseban dogadjaj: zamenjuje nedeljni raspored za zadate datume.</summary>
public sealed record ScheduleException
{
    public required string Name { get; init; }

    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    public required DaySchedule Schedule { get; init; }
}

public sealed record WeeklySchedule
{
    /// <summary>Svih sedam dana.</summary>
    public required IReadOnlyDictionary<DayOfWeek, DaySchedule> Days { get; init; }

    /// <summary>Ako se dva izuzetka preklapaju, vazi onaj koji je kasnije na spisku.</summary>
    public IReadOnlyList<ScheduleException> Exceptions { get; init; } = [];

    /// <summary>Trajanje stisavanja pri zatvaranju (15-30 s).</summary>
    public int FadeOutSeconds { get; init; } = 20;

    /// <summary>Kopira ceo raspored jednog dana na drugi.</summary>
    public WeeklySchedule WithDayCopied(DayOfWeek from, DayOfWeek to)
    {
        var days = new Dictionary<DayOfWeek, DaySchedule>(Days) { [to] = Days[from] };
        return this with { Days = days };
    }
}

/// <summary>Rucni izbor programa na ogranicen period; posle isteka raspored se sam vraca.</summary>
public sealed record ManualOverride
{
    public required SlotProgram Program { get; init; }

    public required DateTime StartsLocal { get; init; }

    public required DateTime EndsLocal { get; init; }
}

public enum ProgramSource
{
    /// <summary>Van radnog vremena ili zatvoreno.</summary>
    Closed,
    WeeklySlot,
    Exception,
    Override,
}

/// <summary>Odgovor rasporeda na pitanje "sta treba da radi radio u ovom trenutku".</summary>
public sealed record ScheduleDecision
{
    public required bool ShouldPlay { get; init; }

    public required ProgramSource Source { get; init; }

    public DayPart? Part { get; init; }

    public SlotProgram? Program { get; init; }

    /// <summary>Kada se danas zatvara (samo dok radi).</summary>
    public DateTime? ClosesAtLocal { get; init; }

    /// <summary>Sledeci trenutak u kom se odgovor menja (novi period, zatvaranje, otvaranje, kraj rucnog izbora).</summary>
    public required DateTime NextChangeLocal { get; init; }

    public string? ExceptionName { get; init; }
}
