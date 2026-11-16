using LustreRadio.Audio;

namespace LustreRadio.Audio.Tests;

// Sekvencer i deck iz faze A (izmena 3db173d), bez izmena u logici. Sluzi samo kao uzor za B-04:
// novi put zvuka (baferi deckova, koordinator) mora dati isti izlaz, uzorak po uzorak.
#pragma warning disable

/// <summary>
/// Standard Crossfade: dva decka, fiksna duzina prelaza, equal-power kriva.
/// Ako se pesma zavrsi pre vremena ili izvor pukne, odmah krece sledeca.
/// </summary>
internal sealed class LegacyCrossfadeSequencer : IFrameProvider
{
    private const int MaxChunkFrames = 4096;
    private const int MaxLoadAttempts = 5;

    private readonly Func<QueuedTrack?> _nextTrack;
    private readonly long _crossfadeFrames;
    private readonly long _recoveryFadeFrames;
    private readonly float[] _scratch = new float[MaxChunkFrames * 2];
    private readonly List<TrackEvent> _events = [];

    private LegacyDeck _current = new();
    private LegacyDeck _incoming = new();
    private TrackEvent? _currentEvent;
    private TrackEvent? _incomingEvent;
    private bool _transition;
    private bool _queueEmpty;
    private readonly long _skipFadeFrames;
    private long _pendingFadeIn;
    private long _outFrames;
    private volatile bool _skipRequested;

    public LegacyCrossfadeSequencer(AudioFormat format, Func<QueuedTrack?> nextTrack, double crossfadeSeconds, double initialFadeInSeconds = 0)
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

    private static void Flag(TrackEvent? trackEvent, LegacyDeck deck)
    {
        if (trackEvent is null)
        {
            return;
        }

        trackEvent.Faulted |= deck.Faulted;
        trackEvent.EndedEarly |= deck.EndedEarly;
    }

    private bool TryLoad(LegacyDeck deck, long fadeInFrames, long fadeStart, out TrackEvent? trackEvent)
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


/// <summary>
/// Jedan "deck": pusta jednu pesmu, primenjuje njenu jacinu i krivu ulaska/izlaska.
/// Greska izvora ne izlazi napolje: deck se oznaci kao neispravan i zavrsen.
/// </summary>
internal sealed class LegacyDeck
{
    private enum Fade
    {
        None,
        In,
        Out,
    }

    private ISampleSource? _source;
    private float _gain = 1f;
    private Fade _fade = Fade.None;
    private long _fadePos;
    private long _fadeLen;

    public bool IsLoaded => _source is not null;

    public bool Finished { get; private set; }

    /// <summary>Izvor je bacio gresku tokom citanja.</summary>
    public bool Faulted { get; private set; }

    /// <summary>Izvor se zavrsio pre duzine koju je najavio.</summary>
    public bool EndedEarly { get; private set; }

    /// <summary>Broj frejmova procitanih iz izvora.</summary>
    public long Position { get; private set; }

    public long? RemainingFrames =>
        _source?.LengthFrames is long length ? Math.Max(0, length - Position) : null;

    public long FadePosition => _fadePos;

    public long FadeLength => _fadeLen;

    public void Load(ISampleSource source, float gain)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Format.Channels != 2)
        {
            throw new ArgumentException("Deck prima samo stereo izvor.", nameof(source));
        }

        Unload();
        _source = source;
        _gain = gain;
    }

    public void Unload()
    {
        try
        {
            _source?.Dispose();
        }
        catch (Exception)
        {
            // zatvaranje neispravnog izvora ne sme da zaustavi reprodukciju
        }

        _source = null;
        _gain = 1f;
        _fade = Fade.None;
        _fadePos = 0;
        _fadeLen = 0;
        Position = 0;
        Finished = false;
        Faulted = false;
        EndedEarly = false;
    }

    public void StartFadeIn(long frames, long startPosition = 0)
    {
        if (frames <= 0)
        {
            _fade = Fade.None;
            return;
        }

        _fade = Fade.In;
        _fadeLen = frames;
        _fadePos = Math.Clamp(startPosition, 0, frames);
    }

    public void StartFadeOut(long frames)
    {
        _fade = Fade.Out;
        _fadeLen = Math.Max(1, frames);
        _fadePos = 0;
    }

    /// <summary>
    /// Cita iz izvora u pomocni bafer i DODAJE u mix. Vraca broj dodatih frejmova.
    /// Manje od trazenog znaci da je deck zavrsio.
    /// </summary>
    public int ReadAdd(Span<float> mix, Span<float> scratch)
    {
        if (_source is null || Finished)
        {
            return 0;
        }

        var wanted = mix.Length / 2;
        var buffer = scratch[..(wanted * 2)];
        var got = 0;

        while (got < wanted)
        {
            int read;
            try
            {
                read = _source.Read(buffer[(got * 2)..]);
            }
            catch (Exception)
            {
                Faulted = true;
                Finished = true;
                break;
            }

            if (read <= 0)
            {
                Finished = true;
                // Dekoderi cesto najave malo vecu duzinu nego sto fajl stvarno ima (izmereno: do ~15 ms).
                // "Ranije zavrsio" se belezi tek kad fali vise od desetinke sekunde.
                var tolerance = _source.Format.SampleRate / 10;
                if (_source.LengthFrames is long length && Position + got < length - tolerance)
                {
                    EndedEarly = true;
                }

                break;
            }

            got += read;
        }

        Position += got;

        var mixed = 0;
        for (; mixed < got; mixed++)
        {
            var g = _gain;
            if (_fade == Fade.In)
            {
                g *= FadeCurve.EqualPowerIn((double)_fadePos / _fadeLen);
                if (++_fadePos >= _fadeLen)
                {
                    _fade = Fade.None;
                }
            }
            else if (_fade == Fade.Out)
            {
                if (_fadePos >= _fadeLen)
                {
                    Finished = true;
                    break;
                }

                g *= FadeCurve.EqualPowerOut((double)_fadePos / _fadeLen);
                _fadePos++;
            }

            mix[mixed * 2] += buffer[mixed * 2] * g;
            mix[(mixed * 2) + 1] += buffer[(mixed * 2) + 1] * g;
        }

        if (_fade == Fade.Out && _fadePos >= _fadeLen)
        {
            Finished = true;
        }

        return mixed;
    }
}

#pragma warning restore
