namespace LustreRadio.Audio;

/// <summary>
/// Izvor zvuka jedne pesme (dekoder ili vestacki signal). Jedan "frejm" = po jedan uzorak za svaki kanal.
/// </summary>
public interface ISampleSource : IDisposable
{
    AudioFormat Format { get; }

    /// <summary>Ukupna duzina u frejmovima, ako je poznata unapred.</summary>
    long? LengthFrames { get; }

    /// <summary>
    /// Puni bafer (isprepletano). Vraca broj upisanih FREJMOVA. 0 znaci kraj.
    /// Sme da vrati manje od trazenog i pre kraja.
    /// </summary>
    int Read(Span<float> buffer);
}

/// <summary>Sve sto proizvodi gotov stereo zvuk blok po blok.</summary>
public interface IFrameProvider
{
    AudioFormat Format { get; }

    /// <summary>Vraca broj upisanih frejmova. Manje od trazenog samo kad vise nema sta da se pusti.</summary>
    int Read(Span<float> buffer);
}

/// <summary>Odrediste zvuka: stvarni uredjaj ili fajl/memorija (za testove).</summary>
public interface IAudioSink : IDisposable
{
    void Write(ReadOnlySpan<float> interleaved);
}
