namespace LustreRadio.Audio;

/// <summary>Pesma u redu za pustanje: izvor zvuka i njena jacina (iz normalizacije).</summary>
public sealed record QueuedTrack(string Id, ISampleSource Source, float Gain = 1f);

/// <summary>Zapis o tome sta je i kada pusteno (pozicija u izlaznom toku, u frejmovima).</summary>
public sealed class TrackEvent
{
    public required string Id { get; init; }

    public required long StartFrame { get; init; }

    public bool Faulted { get; set; }

    public bool EndedEarly { get; set; }

    public bool FailedToLoad { get; set; }
}

/// <summary>
/// Standard Crossfade: dva decka, fiksna duzina prelaza, equal-power kriva.
/// Ako se pesma zavrsi pre vremena ili izvor pukne, odmah krece sledeca.
/// </summary>
public sealed class CrossfadeSequencer : IFrameProvider
{
    private const int MaxChunkFrames = 4096;
    private const int MaxLoadAttempts = 5;

    private readonly Func<QueuedTrack?> _nextTrack;
    private readonly long _crossfadeFrames;
    private readonly long _recoveryFadeFrames;
    private readonly float[] _scratch = new float[MaxChunkFrames * 2];
    private readonly List<TrackEvent> _events = [];

    private Deck _current = new();
    private Deck _incoming = new();
    private TrackEvent? _currentEvent;
    private TrackEvent? _incomingEvent;
    private bool _transition;
    private bool _queueEmpty;
    private readonly long _skipFadeFrames;
    private long _pendingFadeIn;
    private long _outFrames;
    private volatile bool _skipRequested;

    public CrossfadeSequencer(AudioFormat format, Func<QueuedTrack?> nextTrack, double crossfadeSeconds, double initialFadeInSeconds = 0)
    {
        if (format.Channels != 2)
        {
            throw new ArgumentException("Engine radi samo u stereo formatu.", nameof(format));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(crossfadeSeconds);
        Format = format;
        _nextTrack = nextTrack ?? throw new ArgumentNullException(nameof(nextTrack));
        _crossfadeFrames = format.FramesFromSeconds(crossfadeSeconds);
        _recoveryFadeFrames = format.FramesFromSeconds(0.05);
        _skipFadeFrames = format.FramesFromSeconds(Math.Min(2.0, Math.Max(0.05, crossfadeSeconds)));
        _pendingFadeIn = format.FramesFromSeconds(Math.Max(0, initialFadeInSeconds));
    }

    public AudioFormat Format { get; }

    public IReadOnlyList<TrackEvent> Events => _events;

    /// <summary>Ukupan broj do sada proizvedenih frejmova.</summary>
    public long OutputFrames => Interlocked.Read(ref _outFrames);

    /// <summary>Prelazak na sledecu pesmu odmah, kratkim prelazom (dugme "Next"). Sme sa druge niti.</summary>
    public void RequestSkip() => _skipRequested = true;

    /// <summary>Poslednja pustena pesma i gde je pocela u izlaznom toku. Sme sa druge niti.</summary>
    public bool TryGetLatestEvent(out string id, out long startFrame)
    {
        lock (_events)
        {
            for (var i = _events.Count - 1; i >= 0; i--)
            {
                if (!_events[i].FailedToLoad)
                {
                    id = _events[i].Id;
                    startFrame = _events[i].StartFrame;
                    return true;
                }
            }
        }

        id = string.Empty;
        startFrame = 0;
        return false;
    }

    public int Read(Span<float> buffer)
    {
        var total = buffer.Length / 2;
        var done = 0;

        while (done < total)
        {
            if (!_current.IsLoaded)
            {
                if (!TryLoad(_current, _pendingFadeIn, 0, out _currentEvent))
                {
                    break;
                }

                _pendingFadeIn = 0;
                _queueEmpty = false;
            }

            var chunk = Math.Min(total - done, MaxChunkFrames);

            if (_skipRequested)
            {
                _skipRequested = false;
                if (!_transition)
                {
                    var left = _current.RemainingFrames ?? _skipFadeFrames;
                    BeginTransition(Math.Max(1, Math.Min(_skipFadeFrames, left)));
                    _queueEmpty = false; // ako sledece nema, obican tok se nastavlja
                }
            }

            if (!_transition && !_queueEmpty && _crossfadeFrames > 0 && _current.RemainingFrames is long remaining)
            {
                var untilTransition = remaining - _crossfadeFrames;
                if (untilTransition <= 0)
                {
                    BeginTransition(Math.Max(1, Math.Min(_crossfadeFrames, remaining)));
                }
                else
                {
                    chunk = (int)Math.Min(chunk, untilTransition);
                }
            }

            var slice = buffer.Slice(done * 2, chunk * 2);
            slice.Clear();

            var fromCurrent = _current.ReadAdd(slice, _scratch);
            int advance;

            if (_transition)
            {
                _incoming.ReadAdd(slice, _scratch);
                advance = chunk;

                if (_incoming.Finished && !_current.Finished)
                {
                    ReplaceIncoming();
                }

                if (_current.Finished)
                {
                    CompleteTransition();
                }
            }
            else if (_current.Finished)
            {
                // Kraj bez prelaza: pesma pukla, zavrsila ranije, nema sledece ili je prelaz iskljucen.
                advance = fromCurrent;
                var abnormal = _current.Faulted || _current.EndedEarly;
                CloseCurrent();
                _pendingFadeIn = abnormal ? _recoveryFadeFrames : 0;
            }
            else
            {
                advance = fromCurrent > 0 ? fromCurrent : chunk;
            }

            done += advance;
            Interlocked.Add(ref _outFrames, advance);
        }

        return done;
    }

    private void BeginTransition(long fadeFrames)
    {
        if (!TryLoad(_incoming, fadeFrames, 0, out _incomingEvent))
        {
            _queueEmpty = true; // nema sledece: trenutna pesma svira do svog kraja, bez stisavanja
            return;
        }

        _current.StartFadeOut(fadeFrames);
        _transition = true;
    }

    /// <summary>Nova pesma je pukla usred prelaza: ubaci sledecu na isto mesto u krivoj.</summary>
    private void ReplaceIncoming()
    {
        Flag(_incomingEvent, _incoming);
        var fadeLength = _current.FadeLength;
        var fadePosition = _current.FadePosition;
        _incoming.Unload();
        if (!TryLoad(_incoming, fadeLength, fadePosition, out _incomingEvent))
        {
            _transition = false;
            _queueEmpty = true;
        }
    }

    private void CompleteTransition()
    {
        CloseCurrent();
        (_current, _incoming) = (_incoming, _current);
        _currentEvent = _incomingEvent;
        _incomingEvent = null;
        _transition = false;
    }

    private void CloseCurrent()
    {
        Flag(_currentEvent, _current);
        _current.Unload();
        _currentEvent = null;
    }

    private static void Flag(TrackEvent? trackEvent, Deck deck)
    {
        if (trackEvent is null)
        {
            return;
        }

        trackEvent.Faulted |= deck.Faulted;
        trackEvent.EndedEarly |= deck.EndedEarly;
    }

    private bool TryLoad(Deck deck, long fadeInFrames, long fadeStart, out TrackEvent? trackEvent)
    {
        for (var attempt = 0; attempt < MaxLoadAttempts; attempt++)
        {
            var track = _nextTrack();
            if (track is null)
            {
                break;
            }

            trackEvent = new TrackEvent { Id = track.Id, StartFrame = _outFrames };
            lock (_events)
            {
                _events.Add(trackEvent);
            }

            try
            {
                if (track.Source.Format != Format)
                {
                    throw new InvalidOperationException("Format izvora se razlikuje od formata engine-a.");
                }

                deck.Load(track.Source, track.Gain);
            }
            catch (Exception)
            {
                trackEvent.FailedToLoad = true;
                try
                {
                    track.Source.Dispose();
                }
                catch (Exception)
                {
                    // neispravan izvor
                }

                continue;
            }

            deck.StartFadeIn(fadeInFrames, fadeStart);
            return true;
        }

        trackEvent = null;
        return false;
    }
}
