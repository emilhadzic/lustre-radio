namespace LustreRadio.Audio;

/// <summary>
/// Vestacka "pesma": cist ton poznate frekvencije i jacine, sa opcionom tisinom na krajevima.
/// Koristi se u testovima i merenjima, gde mora tacno da se zna sta treba da se cuje.
/// </summary>
public sealed class ToneSource : ISampleSource
{
    private readonly double _phaseStep;
    private readonly float _amplitude;
    private readonly long _length;
    private readonly long _leadSilence;
    private readonly long _tailSilence;
    private long _position;

    public ToneSource(AudioFormat format, double frequencyHz, float amplitude, long lengthFrames, long leadSilenceFrames = 0, long tailSilenceFrames = 0)
    {
        if (format.Channels != 2)
        {
            throw new ArgumentException("ToneSource pravi samo stereo signal.", nameof(format));
        }

        Format = format;
        _phaseStep = 2.0 * Math.PI * frequencyHz / format.SampleRate;
        _amplitude = amplitude;
        _length = lengthFrames;
        _leadSilence = leadSilenceFrames;
        _tailSilence = tailSilenceFrames;
    }

    public AudioFormat Format { get; }

    public long? LengthFrames => _length;

    public int Read(Span<float> buffer)
    {
        var frames = (int)Math.Min(buffer.Length / 2, _length - _position);
        for (var i = 0; i < frames; i++)
        {
            var n = _position + i;
            var silent = n < _leadSilence || n >= _length - _tailSilence;
            var value = silent ? 0f : (float)(_amplitude * Math.Sin(_phaseStep * (n - _leadSilence)));
            buffer[i * 2] = value;
            buffer[(i * 2) + 1] = value;
        }

        _position += frames;
        return frames;
    }

    public void Dispose()
    {
    }
}
