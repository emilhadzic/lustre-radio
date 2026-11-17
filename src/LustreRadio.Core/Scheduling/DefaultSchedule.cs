namespace LustreRadio.Core.Scheduling;

/// <summary>
/// Pocetni nedeljni raspored po zahtevu vlasnika (21 slot). Zanrovi su iz zahteva;
/// BPM rasponi i intenziteti su pocetni predlog i menjaju se u administraciji.
/// </summary>
public static class DefaultSchedule
{
    public static WeeklySchedule Create() => new()
    {
        Days = new Dictionary<DayOfWeek, DaySchedule>
        {
            [DayOfWeek.Monday] = Day(
                "Ponedeljak",
                P(["soft lounge", "acoustic", "chillout", "ambient"], 60, 100, 1),
                P(["lounge", "chillout", "bossa nova", "acoustic"], 70, 110, 2),
                P(["elegant chill", "lounge", "organic house"], 85, 115, 2)),
            [DayOfWeek.Tuesday] = Day(
                "Utorak",
                P(["smooth jazz", "acoustic", "soft lounge"], 60, 105, 1),
                P(["smooth jazz", "lounge", "jazzy house"], 80, 115, 2),
                P(["sophisticated lounge", "smooth jazz", "elegant chill"], 85, 118, 3)),
            [DayOfWeek.Wednesday] = Day(
                "Sreda",
                P(["soft lounge", "chillout", "bossa nova"], 65, 105, 2),
                P(["modern lounge", "chill house", "soft deep house"], 95, 120, 3),
                P(["modern lounge", "deep house", "organic house"], 105, 122, 3)),
            [DayOfWeek.Thursday] = Day(
                "Cetvrtak",
                P(["italian cafe", "french cafe", "bossa nova"], 65, 110, 2),
                P(["italian lounge", "french lounge", "nu disco"], 95, 120, 3),
                P(["nu disco", "italian lounge", "luxury lounge"], 105, 122, 4)),
            [DayOfWeek.Friday] = Day(
                "Petak",
                P(["soft lounge", "smooth jazz", "chillout"], 70, 110, 2),
                P(["modern lounge", "deep house", "nu disco"], 100, 122, 4),
                P(["deep house", "melodic house", "modern lounge"], 110, 124, 5)),
            [DayOfWeek.Saturday] = Day(
                "Subota",
                P(["soft lounge", "bossa nova", "acoustic"], 70, 110, 2),
                P(["modern lounge", "nu disco", "chill house"], 100, 122, 4),
                P(["melodic house", "organic house", "luxury lounge"], 110, 124, 5)),
            [DayOfWeek.Sunday] = Day(
                "Nedelja",
                P(["sunday brunch", "bossa nova", "jazz"], 60, 105, 1),
                P(["bossa nova", "jazz", "lounge"], 70, 112, 2),
                P(["lounge", "elegant chill", "smooth jazz"], 80, 115, 2)),
        },
    };

    private static SlotProgram P(string[] genres, int bpmMin, int bpmMax, int energy) => new()
    {
        Name = string.Empty,
        Genres = genres,
        BpmMin = bpmMin,
        BpmMax = bpmMax,
        Energy = energy,
        Transition = TransitionMode.DjMix, // DJ Mix samo gde analiza dozvoljava (stabilan tempo, razlika do 0,3 %); inace Smart
    };

    private static DaySchedule Day(string name, SlotProgram morning, SlotProgram afternoon, SlotProgram evening) => new()
    {
        Morning = morning with { Name = name + " - jutro" },
        Afternoon = afternoon with { Name = name + " - popodne" },
        Evening = evening with { Name = name + " - vece" },
    };
}
