namespace LustreRadio.Audio;

/// <summary>
/// Jedan "deck": pusta jednu pesmu, primenjuje njenu jacinu i krivu ulaska/izlaska.
/// Greska izvora ne izlazi napolje: deck se oznaci kao neispravan i zavrsen.
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
