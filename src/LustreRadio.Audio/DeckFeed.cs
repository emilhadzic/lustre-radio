using LustreRadio.Core.History;

namespace LustreRadio.Audio;

/// <summary>
/// Bafer decka (plan faze B, 3): gotov zvuk jedne pesme, 5 s, napravljen jednom.
/// Jedan pisac (koordinator) i jedan citalac (nit za mesanje), bez brave: svaka strana pomera samo svoj brojac,
/// a brojac se objavljuje posle podataka (<c>Volatile.Write</c>) i cita pre podataka (<c>Volatile.Read</c>).
/// </summary>
public sealed class DeckFeed
{
    private readonly float[] _ring;
    private readonly int _capacityFrames;
    private long _written;
    private long _read;
    private volatile bool _ended;
    private volatile bool _released;
    private volatile int _endReason;

    public DeckFeed(int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 16);
        _capacityFrames = capacityFrames;
        _ring = new float[capacityFrames * 2];
    }

    public int CapacityFrames => _capacityFrames;

    /// <summary>Koliko gotovih frejmova ceka citaoca.</summary>
    public long Available => Volatile.Read(ref _written) - Volatile.Read(ref _read);

    public long FreeFrames => _capacityFrames - Available;

    /// <summary>Izvor je zavrsen: posle onoga sto je u baferu nema vise zvuka.</summary>
    public bool Ended => _ended;

    /// <summary>Razlog kraja; <see cref="StopReason.None"/> = uredan kraj pesme.</summary>
    public StopReason EndReason => (StopReason)_endReason;

    /// <summary>Nit za mesanje je pustila ovaj deck (koordinator ga sme ponovo upotrebiti).</summary>
    public bool Released => _released;

    /// <summary>Broj generacije (menja ga samo koordinator, pri svakom novom koriscenju).</summary>
    public int Generation { get; private set; }

    public long TotalWritten => Volatile.Read(ref _written);

    /// <summary>Koordinator: upisuje gotov zvuk. Vraca broj upisanih frejmova (ne vise od slobodnog mesta).</summary>
    public int Write(ReadOnlySpan<float> interleaved)
    {
        var frames = (int)Math.Min(interleaved.Length / 2, FreeFrames);
        var written = Volatile.Read(ref _written);
        var start = (int)(written % _capacityFrames);
        var first = Math.Min(frames, _capacityFrames - start);
        interleaved[..(first * 2)].CopyTo(_ring.AsSpan(start * 2));
        if (frames > first)
        {
            interleaved.Slice(first * 2, (frames - first) * 2).CopyTo(_ring);
        }

        Volatile.Write(ref _written, written + frames);
        return frames;
    }

    /// <summary>Koordinator: posle poslednjeg upisa.</summary>
    public void MarkEnded(StopReason reason)
    {
        _endReason = (int)reason;
        _ended = true;
    }

    /// <summary>Nit za mesanje: cita do <paramref name="interleaved"/>.Length/2 frejmova; ne ceka.</summary>
    public int Read(Span<float> interleaved)
    {
        var read = Volatile.Read(ref _read);
        var frames = (int)Math.Min(interleaved.Length / 2, Volatile.Read(ref _written) - read);
        if (frames <= 0)
        {
            return 0;
        }

        var start = (int)(read % _capacityFrames);
        var first = Math.Min(frames, _capacityFrames - start);
        _ring.AsSpan(start * 2, first * 2).CopyTo(interleaved);
        if (frames > first)
        {
            _ring.AsSpan(0, (frames - first) * 2).CopyTo(interleaved[(first * 2)..]);
        }

        Volatile.Write(ref _read, read + frames);
        return frames;
    }

    /// <summary>Nit za mesanje: deck vise ne koristi ovaj bafer.</summary>
    public void Release() => _released = true;

    /// <summary>Koordinator: novo koriscenje (samo posle <see cref="Release"/> ili pre prve predaje).</summary>
    public void Reset()
    {
        Volatile.Write(ref _written, 0);
        Volatile.Write(ref _read, 0);
        _endReason = 0;
        _ended = false;
        Generation++;
        _released = false;
    }
}

/// <summary>Pesma spremna za mesanje: bafer decka i podaci o pesmi. Pravi je koordinator.</summary>
public sealed class PreparedTrack
{
    internal PreparedTrack(QueuedTrack track, DeckFeed feed, long trackNumber)
    {
        Track = track;
        Feed = feed;
        TrackNumber = trackNumber;
        LengthFrames = track.Source.LengthFrames;
        Source = new BufferedSource(feed, track.Source.Format, LengthFrames);
    }

    public QueuedTrack Track { get; }

    public DeckFeed Feed { get; }

    public long TrackNumber { get; }

    public long? LengthFrames { get; }

    /// <summary>Izvor koji deck cita (samo iz bafera decka).</summary>
    internal BufferedSource Source { get; }
}

/// <summary>
/// Izvor za deck koji cita samo iz bafera decka. Kad nema zvuka a pesma nije zavrsena, vraca 0 i
/// <see cref="Ended"/> = false: deck tada ne smatra pesmu zavrsenom (prazan bafer), nego ceka.
/// Zatvaranje samo pusta bafer; dekoder zatvara koordinator.
/// </summary>
public sealed class BufferedSource(DeckFeed feed, AudioFormat format, long? lengthFrames) : ISampleSource
{
    public DeckFeed Feed => feed;

    public AudioFormat Format => format;

    public long? LengthFrames => lengthFrames;

    public bool Ended => feed.Ended && feed.Available == 0;

    public int Read(Span<float> buffer) => feed.Read(buffer);

    public void Dispose() => feed.Release();
}

/// <summary>
/// Mesto za predaju "ovo je sledeca": jedna vrednost, bez brave. Koordinator objavljuje i povlaci
/// (<c>CompareExchange</c>), nit za mesanje preuzima (<c>Exchange</c>): od preuzimanja i povlacenja uspe tacno jedno.
/// </summary>
public sealed class HandoffSlot
{
    private PreparedTrack? _item;

    public PreparedTrack? Peek() => Volatile.Read(ref _item);

    /// <summary>Koordinator: objavljuje ako je mesto prazno.</summary>
    public bool Publish(PreparedTrack item) => Interlocked.CompareExchange(ref _item, item, null) is null;

    /// <summary>Nit za mesanje: uzima i prazni mesto u jednom koraku.</summary>
    public PreparedTrack? Take() => Interlocked.Exchange(ref _item, null);

    /// <summary>Koordinator: povlaci objavljenu stavku; false ako ju je nit za mesanje vec uzela.</summary>
    public bool Retract(PreparedTrack item) => ReferenceEquals(Interlocked.CompareExchange(ref _item, null, item), item);
}
