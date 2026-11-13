namespace LustreRadio.Core.Scheduling;

/// <summary>
/// Raspored se uvek racuna iz trenutnog vremena, nikad iz "okidaca" koji mogu da se propuste.
/// Zato isto radi posle restarta, nestanka struje ili pomeranja sata (spec. deo 7).
/// Prioritet: rucni izbor > praznik/dogadjaj > nedeljni slot.
/// </summary>
public static class ScheduleEngine
{
    public static ScheduleDecision Evaluate(WeeklySchedule schedule, ManualOverride? manualOverride, DateTime nowLocal)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var date = DateOnly.FromDateTime(nowLocal);
        var time = TimeOnly.FromDateTime(nowLocal);
        var (day, exception) = Resolve(schedule, date);

        if (day.Closed || time < day.Times.Open || time >= day.Times.Close)
        {
            return new ScheduleDecision
            {
                ShouldPlay = false,
                Source = ProgramSource.Closed,
                NextChangeLocal = NextOpening(schedule, nowLocal),
                ExceptionName = exception?.Name,
            };
        }

        var part = time < day.Times.AfternoonStart ? DayPart.Morning
            : time < day.Times.EveningStart ? DayPart.Afternoon
            : DayPart.Evening;
        var closes = date.ToDateTime(day.Times.Close);
        var next = part switch
        {
            DayPart.Morning => date.ToDateTime(day.Times.AfternoonStart),
            DayPart.Afternoon => date.ToDateTime(day.Times.EveningStart),
            _ => closes,
        };
        var program = day.ProgramFor(part);
        var source = exception is null ? ProgramSource.WeeklySlot : ProgramSource.Exception;

        if (manualOverride is not null)
        {
            if (manualOverride.StartsLocal <= nowLocal && nowLocal < manualOverride.EndsLocal)
            {
                program = manualOverride.Program;
                source = ProgramSource.Override;
                next = manualOverride.EndsLocal < closes ? manualOverride.EndsLocal : closes;
            }
            else if (manualOverride.StartsLocal > nowLocal && manualOverride.StartsLocal < next)
            {
                next = manualOverride.StartsLocal;
            }
        }

        return new ScheduleDecision
        {
            ShouldPlay = true,
            Source = source,
            Part = part,
            Program = program,
            ClosesAtLocal = closes,
            NextChangeLocal = next,
            ExceptionName = exception?.Name,
        };
    }

    /// <summary>Rucni izbor koji traje do sledece promene u rasporedu (kraj perioda ili zatvaranje).</summary>
    public static ManualOverride OverrideUntilNextChange(WeeklySchedule schedule, SlotProgram program, DateTime nowLocal) => new()
    {
        Program = program,
        StartsLocal = nowLocal,
        EndsLocal = Evaluate(schedule, null, nowLocal).NextChangeLocal,
    };

    private static (DaySchedule Day, ScheduleException? Exception) Resolve(WeeklySchedule schedule, DateOnly date)
    {
        for (var i = schedule.Exceptions.Count - 1; i >= 0; i--)
        {
            var exception = schedule.Exceptions[i];
            if (exception.From <= date && date <= exception.To)
            {
                return (exception.Schedule, exception);
            }
        }

        return (schedule.Days[date.DayOfWeek], null);
    }

    private static DateTime NextOpening(WeeklySchedule schedule, DateTime nowLocal)
    {
        var date = DateOnly.FromDateTime(nowLocal);
        for (var i = 0; i <= 366; i++)
        {
            var candidate = date.AddDays(i);
            var (day, _) = Resolve(schedule, candidate);
            if (day.Closed)
            {
                continue;
            }

            var open = candidate.ToDateTime(day.Times.Open);
            if (open > nowLocal)
            {
                return open;
            }
        }

        return DateTime.MaxValue; // zatvoreno vise od godinu dana unapred
    }
}

/// <summary>Provera rasporeda pre cuvanja. Vraca spisak gresaka (prazan = ispravno).</summary>
public static class ScheduleValidator
{
    public static IReadOnlyList<string> Validate(WeeklySchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var errors = new List<string>();

        foreach (var dayOfWeek in Enum.GetValues<DayOfWeek>())
        {
            if (!schedule.Days.TryGetValue(dayOfWeek, out var day))
            {
                errors.Add($"Nedostaje dan: {dayOfWeek}.");
                continue;
            }

            ValidateDay(day, dayOfWeek.ToString(), errors);
        }

        foreach (var exception in schedule.Exceptions)
        {
            if (exception.From > exception.To)
            {
                errors.Add($"Izuzetak '{exception.Name}': pocetak je posle kraja.");
            }

            ValidateDay(exception.Schedule, $"Izuzetak '{exception.Name}'", errors);
        }

        if (schedule.FadeOutSeconds is < 15 or > 30)
        {
            errors.Add("Stisavanje pri zatvaranju mora biti izmedju 15 i 30 sekundi.");
        }

        return errors;
    }

    private static void ValidateDay(DaySchedule day, string label, List<string> errors)
    {
        var t = day.Times;
        if (!(t.Open < t.AfternoonStart && t.AfternoonStart < t.EveningStart && t.EveningStart < t.Close))
        {
            errors.Add($"{label}: vremena moraju ici redom (otvaranje < popodne < vece < zatvaranje), unutar istog dana.");
        }

        foreach (var part in Enum.GetValues<DayPart>())
        {
            var program = day.ProgramFor(part);
            if (program.Genres.Count == 0 && program.MixId is null)
            {
                errors.Add($"{label}, {part}: nema ni zanrova ni miksa.");
            }

            if (program.BpmMin >= program.BpmMax)
            {
                errors.Add($"{label}, {part}: BPM raspon nije ispravan.");
            }

            if (program.Energy is < 1 or > 5)
            {
                errors.Add($"{label}, {part}: intenzitet mora biti 1-5.");
            }
        }
    }
}
