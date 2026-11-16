using LustreRadio.Core.Diagnostics;
using LustreRadio.Core.History;

namespace LustreRadio.Audio;

/// <summary>Pesma u redu za pustanje: izvor zvuka i njena jacina (iz normalizacije).</summary>
public sealed record QueuedTrack(string Id, ISampleSource Source, float Gain = 1f)
{
    /// <summary>Jedno emitovanje (plan faze B, 2.2): nov za svaki izbor, nikad se ne koristi ponovo.</summary>
    public Guid PlaybackId { get; init; }

    /// <summary>Podaci za prikaz (sta svira); prenosi ih koordinator kad pesma stvarno krene.</summary>
    public object? Info { get; init; }
}

/// <summary>Zapis o tome sta je i kada pusteno (pozicija u izlaznom toku, u frejmovima).</summary>
public sealed class TrackEvent
{
    public required string Id { get; init; }

    public required long StartFrame { get; init; }

    public bool Faulted { get; set; }

    public bool EndedEarly { get; set; }

    public bool FailedToLoad { get; set; }

    public Guid PlaybackId { get; init; }

    public long TrackNumber { get; init; }

    /// <summary>Dugme Sledeca je pokrenulo prelaz sa ove pesme.</summary>
    public bool Skipped { get; set; }
}

/// <summary>
/// Nit za mesanje (Standard Crossfade): dva decka, fiksna duzina prelaza, equal-power kriva.
/// <para>Faza B, korak B3: cita SAMO gotov zvuk iz bafera deckova; sledecu pesmu uzima iz mesta za predaju
/// (<see cref="HandoffSlot"/>). Ne poziva dekoder, ne otvara fajlove, ne pise u bazu i ne ceka: kad zvuka nema,
/// salje tisinu. Pravilo "nema povratka" (plan 2.1): od pocetka pretapanja tekuca pesma je B; ako A ostane bez zvuka,
/// pretapanje se zavrsava odmah, A se pusta i vise se ne cuje.</para>
/// </summary>
public sealed class CrossfadeSequencer : IFrameProvider
{
    private const int MaxChunkFrames = 4096;

    private readonly Coordinator _coordinator;
    private readonly bool _ownsCoordinator;
    private readonly HandoffSlot _handoff;
    private readonly long _crossfadeFrames;
    private readonly long _recoveryFadeFrames;
    private readonly long _shortestTransition;
    private readonly float[] _scratch = new float[MaxChunkFrames * 2];
    private readonly List<TrackEvent> _events = [];
    private readonly IPlaybackEvents? _history;
    private readonly long _skipFadeFrames;

    private Deck _current = new();
    private Deck _incoming = new();
    private TrackEvent? _currentEvent;
    private TrackEvent? _incomingEvent;
    private bool _transition;
    private long _transitionElapsed;
    private bool _incomingStarved;
    private long _pendingFadeIn;
    private long _outFrames;
    private volatile bool _skipRequested;
    private long _starvedFrames;
    private long _lateTransitions;
    private long _takeovers;

    /// <summary>Priprema unapred, bez posebnih niti (testovi, offline priprema zvuka).</summary>
    public CrossfadeSequencer(AudioFormat format, Func<QueuedTrack?> nextTrack, double crossfadeSeconds, double initialFadeInSeconds = 0, IPlaybackEvents? history = null)
        : this(format, new Coordinator(format, nextTrack ?? throw new ArgumentNullException(nameof(nextTrack)), null, history), crossfadeSeconds, initialFadeInSeconds, history, ownsCoordinator: true)
    {
    }

    /// <summary>Sa zadatim koordinatorom (u radu programa koordinator i radna nit rade na svojim nitima).</summary>
    public CrossfadeSequencer(AudioFormat format, Coordinator coordinator, double crossfadeSeconds, double initialFadeInSeconds = 0, IPlaybackEvents? history = null)
        : this(format, coordinator, crossfadeSeconds, initialFadeInSeconds, history, ownsCoordinator: false)
    {
    }

    private CrossfadeSequencer(AudioFormat format, Coordinator coordinator, double crossfadeSeconds, double initialFadeInSeconds, IPlaybackEvents? history, bool ownsCoordinator)
    {
        if (format.Channels != 2)
        {
            throw new ArgumentException("Engine radi samo u stereo formatu.", nameof(format));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(crossfadeSeconds);
        Format = format;
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _ownsCoordinator = ownsCoordinator;
        _handoff = coordinator.Handoff;
        _history = history;
        _crossfadeFrames = format.FramesFromSeconds(crossfadeSeconds);
        _recoveryFadeFrames = format.FramesFromSeconds(0.05);
        _shortestTransition = format.FramesFromSeconds(0.5);
        _skipFadeFrames = format.FramesFromSeconds(Math.Min(2.0, Math.Max(0.05, crossfadeSeconds)));
        _pendingFadeIn = format.FramesFromSeconds(Math.Max(0, initialFadeInSeconds));
        var resume = format.FramesFromSeconds(coordinator.Options.ResumeSeconds);
        foreach (var deck in new[] { _current, _incoming })
        {
            deck.ResumeFrames = resume;
            deck.RampFrames = _recoveryFadeFrames;
        }
    }

    public AudioFormat Format { get; }

    public Coordinator Coordinator => _coordinator;

    public IReadOnlyList<TrackEvent> Events => _events;

    /// <summary>Ukupan broj do sada proizvedenih frejmova.</summary>
    public long OutputFrames => Interlocked.Read(ref _outFrames);

    /// <summary>Frejmovi tisine zbog praznog bafera decka ili nespremne pesme (izmereno).</summary>
    public long StarvedFrames => Interlocked.Read(ref _starvedFrames);

    /// <summary>Prelazi kod kojih sledeca pesma nije bila spremna na vreme.</summary>
    public long LateTransitions => Interlocked.Read(ref _lateTransitions);

    /// <summary>Preuzimanja po pravilu "nema povratka" (A ostala bez zvuka tokom pretapanja).</summary>
    public long Takeovers => Interlocked.Read(ref _takeovers);

    /// <summary>Prelazak na sledecu pesmu odmah, kratkim prelazom (dugme "Next"). Sme sa druge niti.</summary>
    public void RequestSkip() => _skipRequested = true;

    /// <summary>Poslednja pustena pesma i gde je pocela u izlaznom toku. Sme sa druge niti.</summary>
    public bool TryGetLatestEvent(out string id, out long startFrame)
    {
        AudioThreadGuard.Touch(AudioViolation.Lock);
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
        if (!_coordinator.Threaded)
        {
            _coordinator.ServiceInline(_outFrames);
        }

        var total = buffer.Length / 2;
        var done = 0;

        while (done < total)
        {
            if (!_current.IsLoaded)
            {
                if (!TryStart(_current, _pendingFadeIn, 0, out _currentEvent))
                {
                    if (_coordinator.Exhausted)
                    {
                        break; // nema vise nicega: kraj toka
                    }

                    // sledeca jos nije spremna: tisina, bez cekanja; posle tisine pesma ulazi za 50 ms ("zakasneli prelaz")
                    if (_outFrames > 0 && _pendingFadeIn < _recoveryFadeFrames)
                    {
                        _pendingFadeIn = _recoveryFadeFrames;
                        Interlocked.Increment(ref _lateTransitions);
                    }

                    buffer[(done * 2)..].Clear();
                    Interlocked.Add(ref _starvedFrames, total - done);
                    Interlocked.Add(ref _outFrames, total - done);
                    done = total;
                    break;
                }

                _history?.Started(_currentEvent!.PlaybackId, _currentEvent.TrackNumber, _outFrames);
                _pendingFadeIn = 0;
            }

            var chunk = Math.Min(total - done, MaxChunkFrames);
            HandleSkip();
            chunk = PlanTransition(chunk);

            var slice = buffer.Slice(done * 2, chunk * 2);
            slice.Clear();

            var fromCurrent = _current.ReadAdd(slice, _scratch);
            int advance;

            if (_transition)
            {
                var wasStarved = _incomingStarved;
                _incoming.ReadAdd(slice, _scratch);
                _incomingStarved = _incoming.Starved;
                if (wasStarved && !_incomingStarved)
                {
                    // B se ukljucuje na glasnoci koja odgovara ovom trenutku pretapanja (ulaz od 50 ms vodi deck)
                    _incoming.SyncFadeIn(_transitionElapsed);
                }

                advance = chunk;
                _transitionElapsed += chunk;
                if (_incoming.Starved || _current.Starved)
                {
                    Interlocked.Add(ref _starvedFrames, chunk - Math.Min(chunk, fromCurrent));
                }

                if (_incoming.Finished && !_current.Finished)
                {
                    ReplaceIncoming();
                }

                if (_current.Finished)
                {
                    CompleteTransition();
                }
                else if (_current.Starved && _transition)
                {
                    TakeOver(advance);
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
            else if (_current.Starved)
            {
                // prazan bafer: tisina do kraja dela, bez cekanja; ista pesma nastavlja kad se skupi N2
                advance = chunk;
                Interlocked.Add(ref _starvedFrames, chunk - fromCurrent);
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

    /// <summary>
    /// Posle zaustavljanja zvuka (gasenje, zatvaranje): pesme koje su jos u deckovima dobijaju PREKINUTO sa razlogom,
    /// a pripremljene koje nisu krenule se odbacuju. Poziva se kad audio-nit vise ne radi.
    /// </summary>
    public void ReportStop(StopReason reason)
    {
        foreach (var (e, deck) in new[] { (_currentEvent, _current), (_incomingEvent, _incoming) })
        {
            if (e is not null && deck.IsLoaded)
            {
                _history?.Stopped(e.PlaybackId, e.TrackNumber, reason, _outFrames, PositionMs(e));
            }
        }

        _currentEvent = null;
        _incomingEvent = null;
        _coordinator.DiscardPending(StopReason.Shutdown);
        if (_ownsCoordinator)
        {
            _coordinator.Dispose();
        }
    }

    private void HandleSkip()
    {
        if (!_skipRequested)
        {
            return;
        }

        if (_transition)
        {
            // Sledeca tokom repa: rep pesme A se brzo stisa (PREKINUTO – Sledeca), zatim prelaz sa B na sledecu
            if (_currentEvent is { } tail)
            {
                tail.Skipped = true;
            }

            if (!_current.FadingOut || _current.FadeLength - _current.FadePosition > _recoveryFadeFrames)
            {
                _current.StartFadeOut(_recoveryFadeFrames);
            }

            return;
        }

        if (_handoff.Peek() is null)
        {
            return; // sledeca jos nije spremna: zahtev ceka
        }

        _skipRequested = false;
        var left = _current.RemainingFrames ?? _skipFadeFrames;
        BeginTransition(Math.Max(1, Math.Min(_skipFadeFrames, left)));
        if (_transition && _currentEvent is { } skipped)
        {
            skipped.Skipped = true;
        }
    }

    private int PlanTransition(int chunk)
    {
        if (_transition || _crossfadeFrames <= 0 || _current.Starved || _current.RemainingFrames is not long remaining)
        {
            return chunk;
        }

        var untilTransition = remaining - _crossfadeFrames;
        if (untilTransition > 0)
        {
            return (int)Math.Min(chunk, untilTransition);
        }

        if (_handoff.Peek() is null)
        {
            // Sledeca nije spremna kad pretapanje treba da pocne: proverava se cesce; posle 0,5 s pre kraja se ne pretapa.
            return remaining >= _shortestTransition ? (int)Math.Min(chunk, 1024) : chunk;
        }

        if (untilTransition < 0 && remaining < _shortestTransition)
        {
            return chunk; // prekasno za pretapanje: A se zavrsi, B krece sa ulazom od 50 ms
        }

        if (untilTransition < 0)
        {
            Interlocked.Increment(ref _lateTransitions);
        }

        BeginTransition(Math.Max(1, Math.Min(_crossfadeFrames, remaining)));
        return chunk;
    }

    /// <summary>"Nema povratka", slucajevi 1 i 3: A ostala bez zvuka -> pretapanje zavrseno odmah; A se pusta i vise se ne cuje.</summary>
    private void TakeOver(int advance)
    {
        Interlocked.Increment(ref _takeovers);
        if (_currentEvent is { } a)
        {
            _history?.Stopped(a.PlaybackId, a.TrackNumber, StopReason.DecoderStall, _outFrames + advance, PositionMs(a));
        }

        _currentEvent = null;
        _incoming.RampToFull(_recoveryFadeFrames);
        CompleteTransition();
    }

    private void BeginTransition(long fadeFrames)
    {
        if (!TryStart(_incoming, fadeFrames, 0, out _incomingEvent))
        {
            return; // nema sledece: trenutna pesma svira dalje
        }

        _current.StartFadeOut(fadeFrames);
        _transition = true;
        _transitionElapsed = 0;
        _incomingStarved = false;
        if (_incomingEvent is { } b)
        {
            if (_currentEvent is { } a)
            {
                _history?.Crossfade(b.PlaybackId, b.TrackNumber, a.PlaybackId, a.TrackNumber, _outFrames);
            }
            else
            {
                _history?.Started(b.PlaybackId, b.TrackNumber, _outFrames);
            }
        }
    }

    /// <summary>Nova pesma se zavrsila (greska, prerani kraj) usred prelaza: ubaci sledecu na isto mesto u krivoj.</summary>
    private void ReplaceIncoming()
    {
        Flag(_incomingEvent, _incoming);
        Finish(_incomingEvent, _incoming);
        var fadeLength = _current.FadeLength;
        var fadePosition = _current.FadePosition;
        _incoming.Unload();
        _incomingStarved = false;
        if (!TryStart(_incoming, fadeLength, fadePosition, out _incomingEvent))
        {
            _transition = false;
        }
        else if (_incomingEvent is { } replacement)
        {
            _history?.Started(replacement.PlaybackId, replacement.TrackNumber, _outFrames);
        }
    }

    private void CompleteTransition()
    {
        if (_current.Finished)
        {
            CloseCurrent();
        }
        else
        {
            _current.Unload(); // A je pustena (preuzimanje); njen bafer ide nazad koordinatoru
            _currentEvent = null;
        }

        (_current, _incoming) = (_incoming, _current);
        _currentEvent = _incomingEvent;
        _incomingEvent = null;
        _transition = false;
        _incomingStarved = false;
    }

    private void CloseCurrent()
    {
        Flag(_currentEvent, _current);
        Finish(_currentEvent, _current);
        _current.Unload();
        _currentEvent = null;
    }

    /// <summary>Zavrsni zapis: kraj samo ako je pesma odsvirana do poslednjeg uzorka; inace prekinuta sa razlogom.</summary>
    private void Finish(TrackEvent? trackEvent, Deck deck)
    {
        if (trackEvent is null || _history is null)
        {
            return;
        }

        var feedReason = deck.Buffered?.Feed.EndReason ?? StopReason.None;
        var reason = trackEvent.Skipped ? StopReason.Skip
            : feedReason != StopReason.None ? feedReason
            : deck.Faulted ? StopReason.FileError
            : deck.EndedEarly ? StopReason.UnexpectedEnd
            : StopReason.None;
        if (reason == StopReason.None)
        {
            _history.Ended(trackEvent.PlaybackId, trackEvent.TrackNumber, _outFrames, PositionMs(trackEvent));
        }
        else
        {
            _history.Stopped(trackEvent.PlaybackId, trackEvent.TrackNumber, reason, _outFrames, PositionMs(trackEvent));
        }
    }

    private int PositionMs(TrackEvent? e) => e is null ? 0 : (int)Math.Min(int.MaxValue, (_outFrames - e.StartFrame) * 1000 / Format.SampleRate);

    private static void Flag(TrackEvent? trackEvent, Deck deck)
    {
        if (trackEvent is null)
        {
            return;
        }

        trackEvent.Faulted |= deck.Faulted;
        trackEvent.EndedEarly |= deck.EndedEarly;
    }

    /// <summary>Uzima pripremljenu pesmu iz mesta za predaju (bez cekanja). False ako nijedna nije spremna.</summary>
    private bool TryStart(Deck deck, long fadeInFrames, long fadeStart, out TrackEvent? trackEvent)
    {
        var item = _handoff.Take();
        if (item is null)
        {
            trackEvent = null;
            return false;
        }

        trackEvent = new TrackEvent
        {
            Id = item.Track.Id,
            StartFrame = _outFrames,
            PlaybackId = item.Track.PlaybackId,
            TrackNumber = item.TrackNumber,
        };
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_events)
        {
            _events.Add(trackEvent);
        }

        deck.Load(item.Source, item.Track.Gain);
        deck.StartFadeIn(fadeInFrames, fadeStart);
        return true;
    }
}
