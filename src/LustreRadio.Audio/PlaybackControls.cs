namespace LustreRadio.Audio;

/// <summary>
/// Pauza: zvuk se kratko stisa i stane; izvor se dok traje pauza NE cita, pa muzika
/// posle nastavka krece tacno odakle je stala.
/// </summary>
public sealed class PauseGate(IFrameProvider inner, double fadeSeconds = 0.25) : IFrameProvider
{
    private readonly int _fadeFrames = (int)Math.Max(1, inner.Format.FramesFromSeconds(fadeSeconds));
    private volatile bool _paused;

    // 0 = puna jacina, _fadeFrames = tisina. Ceo broj, da kraj stisavanja bude tacan u uzorak.
    private int _fadePos;

    public AudioFormat Format => inner.Format;

    public bool IsPaused => _paused;

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public int Read(Span<float> buffer)
    {
        var frames = buffer.Length / 2;
        if (_paused && _fadePos >= _fadeFrames)
        {
            buffer[..(frames * 2)].Clear();
            return frames;
        }

        var read = inner.Read(buffer);
        for (var i = 0; i < read; i++)
        {
            if (_paused)
            {
                _fadePos = Math.Min(_fadeFrames, _fadePos + 1);
            }
            else if (_fadePos > 0)
            {
                _fadePos--;
            }

            if (_fadePos > 0)
            {
                var level = 1f - ((float)_fadePos / _fadeFrames);
                buffer[i * 2] *= level;
                buffer[(i * 2) + 1] *= level;
            }
        }

        return read;
    }
}

/// <summary>Glasnoca koju podesava zaposleni. Promena je glatka (bez klika). Limiter dolazi posle.</summary>
public sealed class VolumeControl : IFrameProvider
{
    private readonly IFrameProvider _inner;
    private readonly float _coefficient;
    private volatile float _target;
    private float _current;

    public VolumeControl(IFrameProvider inner, double volume)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _coefficient = 1f - (float)Math.Exp(-1.0 / (0.03 * inner.Format.SampleRate));
        _target = _current = GainFor(volume);
    }

    public AudioFormat Format => _inner.Format;

    /// <summary>Glasnoca 0-1 (kvadratna kriva, da klizac deluje ravnomerno).</summary>
    public void SetVolume(double volume) => _target = GainFor(volume);

    public static float GainFor(double volume)
    {
        var v = (float)Math.Clamp(volume, 0.0, 1.0);
        return v * v;
    }

    public int Read(Span<float> buffer)
    {
        var read = _inner.Read(buffer);
        var target = _target;
        if (_current == target && target == 1f)
        {
            return read;
        }

        for (var i = 0; i < read; i++)
        {
            _current += (target - _current) * _coefficient;
            if (Math.Abs(target - _current) < 1e-5f)
            {
                _current = target;
            }

            buffer[i * 2] *= _current;
            buffer[(i * 2) + 1] *= _current;
        }

        return read;
    }
}
