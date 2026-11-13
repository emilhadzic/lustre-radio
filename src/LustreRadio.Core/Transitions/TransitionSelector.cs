using LustreRadio.Core.Scheduling;

namespace LustreRadio.Core.Transitions;

/// <summary>Sta analiza zna o pesmi, u meri potrebnoj za izbor prelaza.</summary>
public sealed record TrackAnalysisSummary
{
    /// <summary>Poznate su tisina na krajevima i tacke intro/outro.</summary>
    public bool HasEdges { get; init; }

    public double? Bpm { get; init; }

    /// <summary>Pouzdanost tempa i mreze udaraca, 0-1.</summary>
    public double BeatConfidence { get; init; }

    public bool HasBeatGrid { get; init; }

    /// <summary>Tempo je stabilan kroz pesmu (nije "ziv" kao kod jazza ili akustike).</summary>
    public bool TempoStable { get; init; }
}

public sealed record DjMixSettings
{
    /// <summary>Do ove razlike tempa: pravi beatmatching uzivo (0,3 %).</summary>
    public double LiveTolerance { get; init; } = 0.003;

    /// <summary>Do ove razlike: unapred pripremljen prelaz (podrazumevano 2 %, najvise 3 %).</summary>
    public double PreparedTolerance { get; init; } = 0.02;

    public double MinBeatConfidence { get; init; } = 0.8;
}

public enum TransitionKind
{
    Standard,
    Smart,

    /// <summary>DJ Mix uzivo: udarci poravnati, bez promene tempa.</summary>
    DjMixLive,

    /// <summary>DJ Mix iz unapred pripremljenog fajla (blago uskladjen tempo).</summary>
    DjMixPrepared,
}

public sealed record TransitionChoice(TransitionKind Kind, string Reason);

/// <summary>
/// Bira vrstu prelaza po odluci D6. Svaka nesigurnost vodi u sigurniji prelaz:
/// DJ Mix -> Smart -> Standard. Nikad ne vraca nista sto bi ostavilo tisinu.
/// </summary>
public static class TransitionSelector
{
    public static TransitionChoice Select(
        TransitionMode requested,
        TrackAnalysisSummary? outgoing,
        TrackAnalysisSummary? incoming,
        DjMixSettings settings,
        bool preparedTransitionAvailable)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (requested == TransitionMode.Standard)
        {
            return new(TransitionKind.Standard, "Trazen je obican crossfade.");
        }

        if (outgoing is not { HasEdges: true } || incoming is not { HasEdges: true })
        {
            return new(TransitionKind.Standard, "Nema podataka o krajevima pesama.");
        }

        if (requested == TransitionMode.Smart)
        {
            return new(TransitionKind.Smart, "Trazen je Smart Crossfade.");
        }

        foreach (var track in new[] { outgoing, incoming })
        {
            if (track.Bpm is not > 0 || !track.HasBeatGrid)
            {
                return new(TransitionKind.Smart, "Nema tempa ili mreze udaraca.");
            }

            if (!track.TempoStable)
            {
                return new(TransitionKind.Smart, "Tempo nije stabilan.");
            }

            if (track.BeatConfidence < settings.MinBeatConfidence)
            {
                return new(TransitionKind.Smart, "Analiza ritma nije dovoljno pouzdana.");
            }
        }

        var difference = Math.Abs(outgoing.Bpm!.Value - incoming.Bpm!.Value) / outgoing.Bpm.Value;
        if (difference <= settings.LiveTolerance)
        {
            return new(TransitionKind.DjMixLive, "Tempo se poklapa.");
        }

        if (difference <= Math.Min(settings.PreparedTolerance, 0.03))
        {
            return preparedTransitionAvailable
                ? new(TransitionKind.DjMixPrepared, "Koristi se pripremljen prelaz.")
                : new(TransitionKind.Smart, "Pripremljen prelaz ne postoji.");
        }

        return new(TransitionKind.Smart, "Razlika tempa je prevelika za DJ Mix.");
    }
}
