namespace LustreRadio.Audio;

/// <summary>Pusta samo pocetni deo izvora (npr. prvih 25 s pesme u probnom slusanju).</summary>
public sealed class LimitedSource : ISampleSource
{
    private readonly ISampleSource _inner;
    private readonly long _maxFrames;
    private long _delivered;

    public LimitedSource(ISampleSource inner, long maxFrames)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxFrames = Math.Max(0, maxFrames);
    }

    public AudioFormat Format => _inner.Format;

    public long? LengthFrames => _inner.LengthFrames is long length ? Math.Min(length, _maxFrames) : _maxFrames;

    public int Read(Span<float> buffer)
    {
        var frames = (int)Math.Min(buffer.Length / 2, _maxFrames - _delivered);
        if (frames <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer[..(frames * 2)]);
        _delivered += read;
        return read;
    }

    public void Dispose() => _inner.Dispose();
}
