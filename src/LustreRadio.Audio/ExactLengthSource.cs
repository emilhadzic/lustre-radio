namespace LustreRadio.Audio;

/// <summary>
/// Izvor sa tacno poznatom duzinom (izmerenom pri skeniranju). Dekoderi cesto najave
/// pogresnu duzinu, a od nje zavisi trenutak pocetka prelaza.
/// </summary>
public sealed class ExactLengthSource(ISampleSource inner, long lengthFrames) : ISampleSource
{
    public AudioFormat Format => inner.Format;

    public long? LengthFrames { get; } = Math.Max(0, lengthFrames);

    public int Read(Span<float> buffer) => inner.Read(buffer);

    public void Dispose() => inner.Dispose();
}
