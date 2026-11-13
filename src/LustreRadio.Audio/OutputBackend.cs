namespace LustreRadio.Audio;

/// <summary>
/// Vadi pripremljen zvuk iz kruznog bafera za uredjaj. Vraca samo ono cega ima;
/// uredjaj sam dopunjava tisinom ako zvuk kasni.
/// </summary>
internal sealed class OutputPuller(FloatRingBuffer ring, RenderPump pump)
{
    /// <summary>Sve je pripremljeno i predato uredjaju.</summary>
    public bool Finished => pump.Finished && ring.AvailableToRead == 0;

    public int Read(Span<float> destination)
    {
        var read = ring.Read(destination);
        pump.Signal();
        return read;
    }
}

/// <summary>Jedna otvorena veza sa zvucnim uredjajem.</summary>
internal interface IOutputSession : IDisposable
{
    string DeviceId { get; }

    string DeviceName { get; }

    /// <summary>Uredjaj je stao: null = uredno (kraj zvuka), inace greska uredjaja.</summary>
    event Action<Exception?>? Stopped;

    void Play();
}

/// <summary>Pristup zvucnim uredjajima (pravi WASAPI ili lazni u testovima).</summary>
internal interface IOutputBackend : IDisposable
{
    /// <summary>Nesto se promenilo medju uredjajima (dodat, uklonjen, promenjen podrazumevani).</summary>
    event Action? DevicesChanged;

    /// <summary>ID podrazumevanog uredjaja ili null ako ga nema.</summary>
    string? CurrentDefaultDeviceId();

    /// <summary>Otvara podrazumevani uredjaj. Baca gresku ako ga nema.</summary>
    IOutputSession Open(OutputPuller puller, AudioFormat format, int latencyMs, Action<int> reportUnderrunFrames);
}
