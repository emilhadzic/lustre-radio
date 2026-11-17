using LustreRadio.Audio;
using LustreRadio.Core.Scheduling;
using LustreRadio.Core.Transitions;
using LustreRadio.Data;

namespace LustreRadio.Playout;

/// <summary>
/// DJ Mix uzivo (spec. 5.3, odluka vlasnika 17.11.2026.): samo za dve pesme stabilnog tempa sa razlikom do 0,3 %.
/// Pesma B krece od svog prvog cujnog uzorka tako da njen prvi udarac padne tacno na udarac pesme A na granici fraze
/// (16 udaraca, a ako ne stane 4); pretapanje traje do 16 udaraca posle toga. Tempo se ne menja.
/// Svaka nesigurnost (nema analize, nestabilan tempo, razlika veca od 0,3 %, dug uvod bez ritma, nema mesta pred kraj
/// pesme A) vraca null, pa radio koristi Smart Crossfade.
/// </summary>
public static class DjCuePlanner
{
    /// <summary>Uvod pesme B pre prvog udarca duzi od ovoga: bez DJ Mix-a (dug ambijentalni uvod).</summary>
    public const double MaxLeadSeconds = 10;

    /// <summary>DJ prelaz ne pocinje ranije od ovoliko pre kraja pesme A.</summary>
    public const double MaxEarlySeconds = 45;

    private const int PhraseBeats = 16;

    public static DjCue? Plan(TransitionMode mode, TrackRecord a, TrackRecord b, AudioFormat output)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(output);
        var choice = TransitionSelector.Select(mode, Summary(a), Summary(b), new DjMixSettings(), preparedTransitionAvailable: false);
        if (choice.Kind != TransitionKind.DjMixLive)
        {
            return null; // Smart (ili obican) prelaz; pripremljeni prelazi su odlozeni
        }

        // vremena u sekundama fajla
        var rateA = (double)a.SampleRate;
        var rateB = (double)b.SampleRate;
        var periodA = a.BeatPeriodFrames!.Value / rateA;
        var periodB = b.BeatPeriodFrames!.Value / rateB;
        var startA = a.AudioStartFrame!.Value / rateA;
        var endA = a.AudioEndFrame!.Value / rateA;
        var firstBeatA = a.FirstBeatFrame!.Value / rateA;
        var startB = b.AudioStartFrame!.Value / rateB;
        var firstBeatB = b.FirstBeatFrame!.Value / rateB;
        while (firstBeatB < startB)
        {
            firstBeatB += periodB;
        }

        var lead = firstBeatB - startB;
        if (lead > MaxLeadSeconds)
        {
            return null;
        }

        var tail = PhraseBeats * periodA;
        foreach (var step in new[] { PhraseBeats, 4 })
        {
            // poslednji udarac A na granici fraze posle kog stane 16 udaraca pre kraja cujnog dela pesme A
            var k = (long)Math.Floor((endA - tail - firstBeatA) / periodA);
            k -= ((k % step) + step) % step;
            if (k < 0)
            {
                continue;
            }

            var beat = firstBeatA + (k * periodA);
            var bStartsAt = beat - lead; // u vremenu fajla A
            if (bStartsAt < startA + 1 || endA - bStartsAt > MaxEarlySeconds + lead)
            {
                continue;
            }

            return new DjCue
            {
                PreviousTrackId = a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PreviousStartSkip = SmartSkipFrames(a, output),
                StartAtPreviousPosition = (long)Math.Round((bStartsAt - startA) * output.SampleRate),
                FadeFrames = (long)Math.Round((lead + tail) * output.SampleRate),
            };
        }

        return null;
    }

    /// <summary>Isti preskok tisine na pocetku kao u RadioDirector.TryOpen (izlazni frejmovi).</summary>
    public static long SmartSkipFrames(TrackRecord record, AudioFormat output)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(output);
        return record.AudioStartFrame is long start && start > 0 && record.SampleRate > 0
            ? (long)Math.Round(start * (double)output.SampleRate / record.SampleRate)
            : 0;
    }

    private static TrackAnalysisSummary Summary(TrackRecord t) => new()
    {
        HasEdges = t.EdgesVersion > 0 && t.AudioStartFrame is not null && t.AudioEndFrame is not null && t.SampleRate > 0,
        Bpm = t.Bpm,
        HasBeatGrid = t.BeatPeriodFrames is > 0 && t.FirstBeatFrame is not null,
        TempoStable = t.TempoStable,
        BeatConfidence = t.TempoStable ? 1 : 0,
    };
}
