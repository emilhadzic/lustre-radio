namespace LustreRadio.Core.Voice;

/// <summary>
/// Odlucuje da li na granici izmedju dve pesme ide glasovna poruka i koja (spec. deo 15).
/// Poziva se samo na granicama pesama, pa poruka po konstrukciji ne moze da prekine pesmu.
/// Poruka se racuna kao pustena tek kad pozivalac to potvrdi (RecordPlayed).
/// </summary>
public sealed class VoiceScheduler(Random random)
{
    private static readonly VoiceCategory[] SpecialCategories = [VoiceCategory.Seasonal, VoiceCategory.Holiday, VoiceCategory.Event];
    private static readonly VoiceCategory[] LongCategories = [VoiceCategory.Welcome, VoiceCategory.Long];

    private readonly Dictionary<string, DateTime> _lastPlayed = [];
    private readonly List<DateTime> _recentPlays = [];
    private readonly HashSet<string> _failedToday = [];
    private DateOnly? _day;
    private DateTime _identDue;
    private DateTime _longDue;
    private int _defers;
    private bool _voiceOnPreviousBoundary;
    private string? _lastIdentId;
    private string? _lastLongId;
    private VoiceKind? _lastVoice;

    public VoiceDecision OnBoundary(VoiceBoundary boundary, IReadOnlyList<VoiceMessage> messages, VoiceRules rules)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(rules);

        var hadVoiceBefore = _voiceOnPreviousBoundary;
        _voiceOnPreviousBoundary = false;
        var now = boundary.NowLocal;
        var partRules = rules.For(boundary.Part);

        var day = DateOnly.FromDateTime(boundary.OpenedAtLocal);
        if (_day != day)
        {
            _day = day;
            _failedToday.Clear();
            _defers = 0;
            _identDue = boundary.OpenedAtLocal + Between(partRules.IdentMin, partRules.IdentMax);
            _longDue = boundary.OpenedAtLocal + Between(partRules.LongMin, partRules.LongMax);
        }

        if (!rules.Enabled)
        {
            return None("Glasovne poruke su iskljucene.");
        }

        if (now - boundary.OpenedAtLocal < rules.QuietAfterOpen)
        {
            return None("Prvih nekoliko minuta posle otvaranja.");
        }

        if (boundary.ClosesAtLocal - now < rules.QuietBeforeClose)
        {
            return None("Poslednjih nekoliko minuta pre zatvaranja.");
        }

        if (boundary.OverrideActive && rules.SuppressDuringOverride)
        {
            return None("Rucni izbor je aktivan.");
        }

        if (hadVoiceBefore)
        {
            return None("Poruka je isla na prethodnoj granici.");
        }

        _recentPlays.RemoveAll(t => now - t >= TimeSpan.FromHours(1));
        if (_recentPlays.Count >= rules.MaxPerHour)
        {
            return None("Dostignut najveci broj poruka na sat.");
        }

        var longIsDue = _longDue <= now;
        var identIsDue = _identDue <= now;
        if (!longIsDue && !identIsDue)
        {
            return None("Nijedna poruka jos nije dospela.");
        }

        if (boundary.StrongDjPair && _defers < rules.MaxDefersForDjPair)
        {
            _defers++;
            return new VoiceDecision { Deferred = true, Reason = "Pomereno da se ne razbije DJ prelaz." };
        }

        bool Usable(VoiceMessage m) =>
            m.Enabled && m.Broadcastable && !_failedToday.Contains(m.Id) &&
            (m.AllowedParts.Count == 0 || m.AllowedParts.Contains(boundary.Part)) &&
            (m.ValidFrom is not { } from || from <= day) &&
            (m.ValidTo is not { } to || day <= to) &&
            boundary.ClosesAtLocal - now.AddSeconds(m.DurationSeconds) >= rules.QuietBeforeClose;

        var usable = messages.Where(Usable).ToList();

        if (longIsDue)
        {
            // sezonske, praznicne i dogadjajne imaju prednost dok vaze (jednom u svom razmaku)
            var special = usable.Where(m => SpecialCategories.Contains(m.Category) && RepeatGapOk(m, now, rules.LongRepeatGap)).ToList();
            var pool = special.Count > 0 ? special : usable.Where(m => LongCategories.Contains(m.Category)).ToList();
            var chosen = Choose(pool, now, rules.LongRepeatGap, partRules, _lastLongId);
            if (chosen is not null)
            {
                return new VoiceDecision { Message = chosen, Reason = "Dospela duza poruka." };
            }

            // nema nijedne duze poruke: ne blokiraj kratke
            _longDue = now + Between(partRules.LongMin, partRules.LongMax);
        }

        if (identIsDue)
        {
            var chosen = Choose(usable.Where(m => m.Category == VoiceCategory.Ident).ToList(), now, rules.IdentRepeatGap, partRules, _lastIdentId);
            if (chosen is not null)
            {
                return new VoiceDecision { Message = chosen, Reason = "Dospela identifikacija stanice." };
            }

            _identDue = now + Between(partRules.IdentMin, partRules.IdentMax);
        }

        return None("Nema odobrene poruke koja sme sada.");
    }

    /// <summary>Poruka je stvarno pustena.</summary>
    public void RecordPlayed(VoiceMessage message, DateTime nowLocal, VoiceRules rules, Scheduling.DayPart part)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(rules);
        var partRules = rules.For(part);

        _lastPlayed[message.Id] = nowLocal;
        _recentPlays.Add(nowLocal);
        _lastVoice = message.Voice;
        _defers = 0;
        _voiceOnPreviousBoundary = true;

        if (message.Category == VoiceCategory.Ident)
        {
            _lastIdentId = message.Id;
            _identDue = nowLocal + Between(partRules.IdentMin, partRules.IdentMax);
        }
        else
        {
            _lastLongId = message.Id;
            _longDue = nowLocal + Between(partRules.LongMin, partRules.LongMax);

            // duza poruka vec sadrzi ime stanice: kratka ne ide odmah za njom
            var earliestIdent = nowLocal + partRules.IdentMin;
            if (_identDue < earliestIdent)
            {
                _identDue = earliestIdent;
            }
        }
    }

    /// <summary>Fajl poruke nije mogao da se pusti: danas se vise ne bira, muzika ide dalje.</summary>
    public void RecordFailed(VoiceMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _failedToday.Add(message.Id);
    }

    private static VoiceDecision None(string reason) => new() { Reason = reason };

    private bool RepeatGapOk(VoiceMessage message, DateTime now, TimeSpan gap) =>
        !_lastPlayed.TryGetValue(message.Id, out var last) || now - last >= gap;

    private VoiceMessage? Choose(List<VoiceMessage> pool, DateTime now, TimeSpan repeatGap, VoicePartRules partRules, string? lastOfThisKind)
    {
        if (pool.Count == 0)
        {
            return null;
        }

        // Nikad ista poruka dvaput zaredom (u svojoj vrsti), ako postoji druga.
        var others = pool.Where(m => m.Id != lastOfThisKind).ToList();
        var from = others.Count > 0 ? others : pool;

        // Zeljeni razmak; ako ga nijedna ne ispunjava, uzmi najduze nepustane (kao kod pesama).
        var candidates = from.Where(m => RepeatGapOk(m, now, repeatGap)).ToList();
        if (candidates.Count == 0)
        {
            var oldest = from.Min(m => _lastPlayed.GetValueOrDefault(m.Id));
            candidates = from.Where(m => _lastPlayed.GetValueOrDefault(m.Id) == oldest).ToList();
        }

        var wanted = random.NextDouble() < partRules.SerbianShare ? VoiceLanguage.Serbian : VoiceLanguage.English;
        var byLanguage = candidates.Where(m => m.Language == wanted).ToList();
        if (byLanguage.Count > 0)
        {
            candidates = byLanguage;
        }

        var otherVoice = candidates.Where(m => m.Voice != _lastVoice).ToList();
        if (otherVoice.Count > 0)
        {
            candidates = otherVoice;
        }

        return candidates[random.Next(candidates.Count)];
    }

    private TimeSpan Between(TimeSpan min, TimeSpan max) =>
        max <= min ? min : min + TimeSpan.FromSeconds(random.NextDouble() * (max - min).TotalSeconds);
}
