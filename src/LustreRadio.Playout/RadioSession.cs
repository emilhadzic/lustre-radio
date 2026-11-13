using LustreRadio.Audio;

namespace LustreRadio.Playout;

/// <summary>Stisava i zavrsava zvuk kad raspored kaze da je radno vreme isteklo (spec.: 23:30, 15-30 s).</summary>
public sealed class ClosingFader(IFrameProvider inner, Func<bool> shouldPlay, double fadeSeconds) : IFrameProvider
{
    private readonly long _fadeFrames = Math.Max(1, inner.Format.FramesFromSeconds(fadeSeconds));
    private bool _fading;
    private bool _ended;
    private long _fadePos;

    public AudioFormat Format => inner.Format;

    public bool Ended => _ended;

    public int Read(Span<float> buffer)
    {
        if (_ended)
        {
            return 0;
        }

        var frames = inner.Read(buffer);
        if (frames == 0)
        {
            _ended = true;
            return 0;
        }

        if (!_fading && !shouldPlay())
        {
            _fading = true;
        }

        if (!_fading)
        {
            return frames;
        }

        for (var i = 0; i < frames; i++)
        {
            if (_fadePos >= _fadeFrames)
            {
                _ended = true;
                return i;
            }

            var gain = FadeCurve.EqualPowerOut((double)_fadePos / _fadeFrames);
            buffer[i * 2] *= gain;
            buffer[(i * 2) + 1] *= gain;
            _fadePos++;
        }

        return frames;
    }
}

/// <summary>
/// Jedno neprekidno emitovanje (od pocetka sviranja do zatvaranja): reditelj -> prelazi -> stisavanje na kraju.
/// Izlaz (Output) se predaje ili zvucnom uredjaju ili offline rendereru.
/// </summary>
public sealed class RadioSession
{
    private readonly RadioDirector _director;
    private readonly CrossfadeSequencer _sequencer;
    private readonly ClosingFader _fader;
    private readonly AudioFormat _format;
    private readonly HashSet<TrackEvent> _reported = [];
    private QueuedTrack? _resume;
    private string? _resumedId;
    private double _resumeOffsetSeconds;

    public RadioSession(RadioDirector director, AudioFormat format, PlaybackSnapshot? snapshot = null)
    {
        _director = director ?? throw new ArgumentNullException(nameof(director));
        _format = format;
        _resume = director.TryResume(snapshot);
        if (_resume is not null)
        {
            _resumedId = _resume.Id;
            _resumeOffsetSeconds = snapshot!.PositionSeconds;
        }

        _sequencer = new CrossfadeSequencer(
            format,
            Next,
            director.Options.CrossfadeSeconds,
            initialFadeInSeconds: _resume is not null ? director.Options.ResumeFadeInSeconds : 0);
        _fader = new ClosingFader(_sequencer, director.ShouldPlayNow, director.Schedule.FadeOutSeconds);
    }

    public IFrameProvider Output => _fader;

    public CrossfadeSequencer Sequencer => _sequencer;

    /// <summary>Emitovanje je nastavilo pesmu iz snimka stanja.</summary>
    public bool Resumed => _resumedId is not null;

    public bool Ended => _fader.Ended;

    public void Skip() => _sequencer.RequestSkip();

    /// <summary>Periodicno (npr. jednom u sekundi): prijavljuje greske pesama i cuva snimak stanja.</summary>
    public void Tick(bool saveSnapshot)
    {
        TrackEvent[] events;
        lock (_sequencer.Events)
        {
            events = [.. _sequencer.Events];
        }

        foreach (var e in events)
        {
            if ((e.Faulted || e.FailedToLoad) && _reported.Add(e))
            {
                _director.ReportFault(e.Id);
            }
        }

        if (saveSnapshot && !_fader.Ended && _sequencer.TryGetLatestEvent(out var id, out var startFrame))
        {
            var position = (_sequencer.OutputFrames - startFrame) / (double)_format.SampleRate;
            if (id == _resumedId && startFrame == 0)
            {
                position += _resumeOffsetSeconds;
            }

            _director.SaveSnapshot(id, Math.Max(0, position));
        }
    }

    private QueuedTrack? Next()
    {
        if (_resume is { } resume)
        {
            _resume = null;
            return resume;
        }

        return _director.NextTrack();
    }
}
