namespace LustreRadio.Audio;

/// <summary>
/// Jedan "deck": pusta jednu pesmu, primenjuje njenu jacinu i krivu ulaska/izlaska.
/// Greska izvora ne izlazi napolje: deck se oznaci kao neispravan i zavrsen.
/// <para>Faza B, B3: kad je izvor bafer decka (<see cref="BufferedSource"/>), prazan bafer nije kraj pesme:
/// deck je "bez zvuka" (<see cref="Starved"/>), ne napreduje i ceka da se skupi bar N2 zvuka; nastavlja sa ulazom od 50 ms.</para>
/// </summary>
public sealed class Deck
{
    private enum Fade
    {
        None,
        In,
        Out,
    }

    private ISampleSource? _source;
    private BufferedSource? _buffered;
    private float _gain = 1f;
    private Fade _fade = Fade.None;
    private long _fadePos;
    private long _fadeLen;
    private float _rampFrom = 1f;
    private long _rampPos;
    private long _rampLen;

    public bool IsLoaded => _source is not null;

    public bool Finished { get; private set; }

    /// <summary>Izvor je bacio gresku tokom citanja (ili je bafer decka zavrsen greskom).</summary>
    public bool Faulted { get; private set; }

    /// <summary>Izvor se zavrsio pre duzine koju je najavio.</summary>
    public bool EndedEarly { get; private set; }

    /// <summary>Bafer decka je prazan, a pesma nije zavrsena.</summary>
    public bool Starved { get; private set; }

    /// <summary>Koliko zvuka (frejmova) mora da se skupi u praznom decku pre nastavka (N2).</summary>
    public long ResumeFrames { get; set; }

    /// <summary>Duzina ulaza posle praznog bafera i preuzimanja (50 ms).</summary>
    public long RampFrames { get; set; }

    /// <summary>Duzina pretapanja kad ova pesma izlazi (Smart Crossfade: prema stisavanju na kraju pesme).</summary>
    public long TransitionFrames { get; set; }

    /// <summary>Broj frejmova procitanih iz izvora.</summary>
    public long Position { get; private set; }

    public long? RemainingFrames =>
        _source?.LengthFrames is long length ? Math.Max(0, length - Position) : null;

    public long FadePosition => _fadePos;

    public long FadeLength => _fadeLen;

    public bool FadingOut => _fade == Fade.Out;

    public BufferedSource? Buffered => _buffered;

    public void Load(ISampleSource source, float gain)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Format.Channels != 2)
        {
            throw new ArgumentException("Deck prima samo stereo izvor.", nameof(source));
        }

        Unload();
        _source = source;
        _buffered = source as BufferedSource;
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
        _buffered = null;
        _gain = 1f;
        _fade = Fade.None;
        _fadePos = 0;
        _fadeLen = 0;
        _rampFrom = 1f;
        _rampPos = 0;
        _rampLen = 0;
        Position = 0;
        Finished = false;
        Faulted = false;
        EndedEarly = false;
        Starved = false;
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

    /// <summary>Pretapanje: krivu ulaza pomera na zadato mesto (glasnoca koja odgovara tom trenutku pretapanja).</summary>
    public void SyncFadeIn(long position)
    {
        if (_fade != Fade.In)
        {
            return;
        }

        _fadePos = Math.Min(position, _fadeLen);
        if (_fadePos >= _fadeLen)
        {
            _fade = Fade.None;
        }
    }

    /// <summary>Pravilo "nema povratka", slucaj 1: pesma dolazi na punu glasnocu za zadato vreme (50 ms), od sadasnje.</summary>
    public void RampToFull(long frames)
    {
        var current = _fade == Fade.In && _fadeLen > 0 ? FadeCurve.EqualPowerIn((double)_fadePos / _fadeLen) : 1f;
        _fade = Fade.None;
        StartRamp(Starved ? 0f : current * RampGain(), frames);
    }

    /// <summary>
    /// Cita iz izvora u pomocni bafer i DODAJE u mix. Vraca broj dodatih frejmova.
    /// Manje od trazenog znaci da je deck zavrsio ili da je bafer decka prazan (<see cref="Starved"/>).
    /// </summary>
    public int ReadAdd(Span<float> mix, Span<float> scratch)
    {
        if (_source is null || Finished)
        {
            return 0;
        }

        if (_buffered is { } buffered)
        {
            if (Starved)
            {
                if (buffered.Feed.Ended && buffered.Feed.EndReason == Core.History.StopReason.DecoderStall)
                {
                    // spora ili zaglavljena pesma je napustena dok je deck bio prazan: ostatak manji od N2 se ne pusta (bez "seckanja")
                    Finished = true;
                    return 0;
                }

                if (buffered.Feed.Available < Math.Max(1, ResumeFrames) && !buffered.Feed.Ended)
                {
                    return 0;
                }

                Starved = false;
                StartRamp(0f, RampFrames);
            }

        }

        var wanted = mix.Length / 2;
        if (_buffered is { } tail && tail.Feed.Ended && tail.Feed.EndReason != Core.History.StopReason.None && _fade != Fade.Out)
        {
            // ostavi poslednjih 50 ms za stisavanje (sledeci poziv ga pocinje)
            wanted = (int)Math.Min(wanted, Math.Max(0, tail.Feed.Available - Math.Max(1, RampFrames)));
            if (wanted == 0)
            {
                StartFadeOut(Math.Max(1, tail.Feed.Available));
                wanted = mix.Length / 2;
            }
        }

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
                if (_buffered is { Ended: false })
                {
                    Starved = true;
                    break;
                }

                Finished = true;
                if (_buffered is { } done)
                {
                    Faulted = done.Feed.EndReason is Core.History.StopReason.FileError or Core.History.StopReason.FileUnavailable;
                    EndedEarly = done.Feed.EndReason == Core.History.StopReason.UnexpectedEnd;
                }

                // Dekoderi cesto najave malo vecu duzinu nego sto fajl stvarno ima (izmereno: do ~15 ms).
                // "Ranije zavrsio" se belezi tek kad fali vise od desetinke sekunde.
                var tolerance = _source.Format.SampleRate / 10;
                if (!Faulted && _source.LengthFrames is long length && Position + got < length - tolerance)
                {
                    EndedEarly = true;
                }

                break;
            }

            got += read;
        }

        Position += got;

        // Prestanak dekodiranja: ono sto je ispravno dekodirano odsvira se do kraja, sa stisavanjem od 50 ms.
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

            if (_rampLen > 0)
            {
                g *= RampGain();
                if (++_rampPos >= _rampLen)
                {
                    _rampLen = 0;
                    _rampFrom = 1f;
                }
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

    private float RampGain() => _rampLen <= 0 ? 1f : _rampFrom + ((1f - _rampFrom) * _rampPos / _rampLen);

    private void StartRamp(float from, long frames)
    {
        if (frames <= 0)
        {
            _rampLen = 0;
            _rampFrom = 1f;
            return;
        }

        _rampFrom = from;
        _rampPos = 0;
        _rampLen = frames;
    }
}
