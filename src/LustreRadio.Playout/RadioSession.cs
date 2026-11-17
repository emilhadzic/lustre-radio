using LustreRadio.Audio;
using LustreRadio.Core.History;

namespace LustreRadio.Playout;

/// <summary>Stisava i zavrsava zvuk kad raspored kaze da je radno vreme isteklo (spec.: 23:30, 15-30 s).</summary>
public sealed class ClosingFader(IFrameProvider inner, Func<bool> shouldPlay, double fadeSeconds) : IFrameProvider
{
    private readonly long _fadeFrames = Math.Max(1, inner.Format.FramesFromSeconds(fadeSeconds));
    private volatile bool _shouldPlay = shouldPlay();
    private bool _fading;
    private bool _ended;
    private long _fadePos;

    /// <summary>Ponovo procenjuje raspored. Poziva ga koordinator (ne audio-nit): raspored ima bravu.</summary>
    public void Refresh() => _shouldPlay = shouldPlay();

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

        if (!_fading && !_shouldPlay)
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
    private QueuedTrack? _resume;
    private string? _resumedId;
    private double _resumeOffsetSeconds;
    private bool _everPlayed;

    private readonly IPlaybackEvents? _history;

    private readonly Coordinator _coordinator;
    private readonly bool _realtime;

    /// <param name="realtime">true: koordinator i radna nit rade na svojim nitima (rad programa); false: priprema unapred, bez niti (testovi).</param>
    public RadioSession(RadioDirector director, AudioFormat format, PlaybackSnapshot? snapshot = null, bool realtime = false, Action<string>? log = null, IDiskProbe? diskProbe = null, IThreadInspector? threadInspector = null)
    {
        _realtime = realtime;
        _history = director.History;
        _director = director ?? throw new ArgumentNullException(nameof(director));
        _format = format;
        _resume = director.TryResume(snapshot);
        if (_resume is not null)
        {
            _resumedId = _resume.Id;
            _resumeOffsetSeconds = snapshot!.PositionSeconds;
        }

        director.PreferredNext = snapshot?.NextTrackId;
        var options = realtime ? director.Options.Coordinator : director.Options.Coordinator with { DurableWaitSeconds = 0 };
        _coordinator = new Coordinator(format, new SessionPlanner(this), options, _history, threaded: realtime, log);
        _coordinator.TrackStarted += track => director.NotifyStarted(track.Info);
        _coordinator.DiskProbe = diskProbe;
        _coordinator.ThreadInspector = threadInspector;
        _coordinator.DecodeFailed += failure => _ = director.HandleDecodeFailure(failure);
        _coordinator.RestartRequested += request => RestartRequested?.Invoke(request);
        _sequencer = new CrossfadeSequencer(
            format,
            _coordinator,
            director.Options.CrossfadeSeconds,
            initialFadeInSeconds: _resume is not null ? director.Options.ResumeFadeInSeconds : 0,
            history: _history);
        _coordinator.Start();
        _fader = new ClosingFader(_sequencer, director.ShouldPlayNow, director.Schedule.FadeOutSeconds);
        _coordinator.StepHook = _fader.Refresh;
    }

    public IFrameProvider Output => _fader;

    public CrossfadeSequencer Sequencer => _sequencer;

    /// <summary>Emitovanje je nastavilo pesmu iz snimka stanja.</summary>
    public bool Resumed => _resumedId is not null;

    public bool Ended => _fader.Ended;

    public void Skip() => _sequencer.RequestSkip();

    /// <summary>Kontrolisano ponovno pokretanje Engine-a (posle zaglavljenih poziva dekodera; D7-4).</summary>
    public event Action<RestartRequest>? RestartRequested;

    public Coordinator Coordinator => _coordinator;

    /// <summary>
    /// Kraj emitovanja (zvuk je vec zaustavljen): pesme u deckovima dobijaju PREKINUTO – zatvaranje,
    /// a nastavak koji nije stigao da krene se odbacuje.
    /// </summary>
    public void End(StopReason reason = StopReason.Closing)
    {
        _sequencer.ReportStop(reason);
        _coordinator.Dispose();
        if (_resume is { } resume)
        {
            _resume = null;
            _history?.Discarded(resume.PlaybackId, long.TryParse(resume.Id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0, StopReason.Shutdown);
        }
    }

    /// <summary>Periodicno (npr. jednom u sekundi): prijavljuje greske pesama i cuva snimak stanja.</summary>
    public void Tick(bool saveSnapshot)
    {
        if (saveSnapshot && !_fader.Ended && TryGetPosition(out var id, out var position))
        {
            var plan = _coordinator.Plan.Items;
            _director.SaveSnapshot(id, position, plan.Count > 0 ? plan[0].TrackId : null);
        }
    }

    /// <summary>Koja pesma je poslednja krenula i dokle je stigla (u sekundama pesme).</summary>
    public bool TryGetPosition(out string trackId, out double positionSeconds)
    {
        if (!_sequencer.TryGetLatestEvent(out trackId, out var startFrame))
        {
            positionSeconds = 0;
            return false;
        }

        positionSeconds = Math.Max(0, (_sequencer.OutputFrames - startFrame) / (double)_format.SampleRate);
        if (trackId == _resumedId && startFrame == 0)
        {
            positionSeconds += _resumeOffsetSeconds;
        }

        return true;
    }

    /// <summary>Plan (sledece pesme) za ekran "Sledeca".</summary>
    public PlanSnapshot Plan => _coordinator.Plan;

    /// <summary>Planer sesije: prvo nastavak prekinute pesme (ako postoji), zatim reditelj.</summary>
    private sealed class SessionPlanner(RadioSession session) : IPlanner
    {
        private PlannedItem? _resumeItem;

        public (int Size, string? Reason) Capacity() => session._resume is not null ? (Math.Max(1, session._director.Capacity().Size), null) : session._director.Capacity();

        public PlannedItem? Select(IReadOnlyList<string> before)
        {
            if (session._resume is { } resume && _resumeItem is null)
            {
                _resumeItem = new PlannedItem { TrackId = resume.Id, PlaybackId = resume.PlaybackId, Title = (resume.Info as NowPlaying)?.Title, Artist = (resume.Info as NowPlaying)?.Artist };
                return _resumeItem;
            }

            return session._director.Select(before);
        }

        public QueuedTrack? Open(PlannedItem item)
        {
            if (ReferenceEquals(item, _resumeItem) && session._resume is { } resume)
            {
                session._resume = null;
                session._everPlayed = true;
                return resume;
            }

            session._everPlayed = true;
            return session._director.Open(item);
        }

        public void Release(PlannedItem item)
        {
            if (!ReferenceEquals(item, _resumeItem))
            {
                session._director.Release(item);
            }
        }

        public bool IsValid(PlannedItem item) => ReferenceEquals(item, _resumeItem) || session._director.IsValid(item);
    }

    private QueuedTrack? Next()
    {
        if (_resume is { } resume)
        {
            _resume = null;
            _everPlayed = true;
            return resume;
        }

        // Posle zatvaranja muzika mora da traje jos dok traje stisavanje.
        var queued = _director.NextTrack(closing: _everPlayed && !_fader.Ended);
        _everPlayed |= queued is not null;
        return queued;
    }
}
