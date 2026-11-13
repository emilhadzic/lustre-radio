namespace LustreRadio.Audio;

/// <summary>
/// Kruzni bafer izmedju niti koja priprema zvuk i niti koja ga salje uredjaju.
/// Bez zakljucavanja; ispravan za tacno jednog pisca i jednog citaoca.
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _data;
    private readonly int _mask;
    private long _write;
    private long _read;

    /// <param name="capacitySamples">Kapacitet u uzorcima; zaokruzuje se navise na stepen broja 2.</param>
    public FloatRingBuffer(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacitySamples, 2);
        var capacity = 2;
        while (capacity < capacitySamples)
        {
            capacity <<= 1;
        }

        _data = new float[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _data.Length;

    public int AvailableToRead => (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read));

    public int FreeSpace => Capacity - AvailableToRead;

    /// <summary>Upisuje koliko stane. Vraca broj upisanih uzoraka.</summary>
    public int Write(ReadOnlySpan<float> source)
    {
        var write = Volatile.Read(ref _write);
        var count = Math.Min(source.Length, Capacity - (int)(write - Volatile.Read(ref _read)));
        if (count <= 0)
        {
            return 0;
        }

        var index = (int)(write & _mask);
        var first = Math.Min(count, Capacity - index);
        source[..first].CopyTo(_data.AsSpan(index));
        source[first..count].CopyTo(_data);
        Volatile.Write(ref _write, write + count);
        return count;
    }

    /// <summary>Cita koliko ima. Vraca broj procitanih uzoraka.</summary>
    public int Read(Span<float> destination)
    {
        var read = Volatile.Read(ref _read);
        var count = Math.Min(destination.Length, (int)(Volatile.Read(ref _write) - read));
        if (count <= 0)
        {
            return 0;
        }

        var index = (int)(read & _mask);
        var first = Math.Min(count, Capacity - index);
        _data.AsSpan(index, first).CopyTo(destination);
        _data.AsSpan(0, count - first).CopyTo(destination[first..]);
        Volatile.Write(ref _read, read + count);
        return count;
    }
}
