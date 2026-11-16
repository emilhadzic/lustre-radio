using LustreRadio.Core.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LustreRadio.Audio;

/// <summary>
/// Pesma iz fajla, pretvorena u format engine-a (stereo, zadati sample rate).
/// Cita se u malim delovima; cela pesma se nikad ne ucitava u memoriju.
/// </summary>
public sealed class AudioFileSource : ISampleSource
{
    private readonly WaveStream _reader;
    private readonly float[] _buffer = new float[8192];
    private readonly long? _lengthFrames;
    private ISampleProvider _chain;

    internal AudioFileSource(WaveStream reader, AudioFormat target, string decoderId, string path, AudioKind kind)
    {
        if (target.Channels != 2)
        {
            throw new ArgumentException("Engine radi samo u stereo formatu.", nameof(target));
        }

        _reader = reader;
        Format = target;
        DecoderId = decoderId;
        FilePath = path;
        Kind = kind;
        SourceSampleRate = reader.WaveFormat.SampleRate;
        SourceChannels = reader.WaveFormat.Channels;
        if (SourceSampleRate < 8000 || SourceChannels < 1)
        {
            throw new InvalidDataException($"Neispravan format zvuka: {SourceSampleRate} Hz, {SourceChannels} kanala.");
        }

        var block = reader.WaveFormat.BlockAlign;
        if (block > 0 && reader.Length > 0)
        {
            var sourceFrames = reader.Length / block;
            _lengthFrames = SourceSampleRate == target.SampleRate
                ? sourceFrames
                : (long)Math.Round(sourceFrames * (double)target.SampleRate / SourceSampleRate);
        }

        _chain = BuildChain();
    }

    public AudioFormat Format { get; }

    /// <summary>Koji dekoder cita ovaj fajl (isti mora da se koristi i za analizu).</summary>
    public string DecoderId { get; }

    public string FilePath { get; }

    public AudioKind Kind { get; }

    /// <summary>
    /// Da li je brzo premotavanje upotrebljivo za ovaj fajl. Izmereno (GitHub Actions, 13.11.2026.):
    /// FLAC preko Media Foundation promasi za vise od 1 s; ostali formati do ~0,15 s.
    /// </summary>
    public bool FastSeekIsReliable => !(DecoderId == Decoders.MediaFoundation && Kind == AudioKind.Flac);

    public int SourceSampleRate { get; }

    public int SourceChannels { get; }

    public bool IsResampled => SourceSampleRate != Format.SampleRate;

    /// <summary>
    /// Duzina koju prijavljuje dekoder. Kod nekih formata (npr. MP3) to je procena;
    /// tacna duzina se dobija probnim dekodiranjem.
    /// </summary>
    public long? LengthFrames => _lengthFrames;

    public int Read(Span<float> buffer)
    {
        AudioThreadGuard.Touch(AudioViolation.Decode);
        var want = Math.Min(buffer.Length, _buffer.Length) & ~1;
        if (want == 0)
        {
            return 0;
        }

        var read = _chain.Read(_buffer, 0, want);
        if (read <= 0)
        {
            return 0;
        }

        _buffer.AsSpan(0, read).CopyTo(buffer);
        return read / 2;
    }

    /// <summary>
    /// Brzo premotavanje na priblizno mesto (u frejmovima engine-a). NIJE tacno u uzorak:
    /// izmereno odstupanje zavisi od dekodera i formata (vidi docs/STATUS.md).
    /// Koristi se samo za nastavak pesme posle restarta, nikad za racunanje prelaza.
    /// </summary>
    public void SeekApproximate(long frame)
    {
        var block = _reader.WaveFormat.BlockAlign;
        var sourceFrame = IsResampled
            ? (long)Math.Round(frame * (double)SourceSampleRate / Format.SampleRate)
            : frame;
        var position = Math.Clamp(sourceFrame * block, 0, Math.Max(0, _reader.Length));
        _reader.Position = position - (position % block);
        _chain = BuildChain();
    }

    /// <summary>
    /// Nastavak pesme od zadatog mesta (posle restarta). Poziva se na tek otvorenom fajlu.
    /// Koristi brzo premotavanje gde je pouzdano, inace tacno preskakanje.
    /// </summary>
    public void ResumeAt(long frame)
    {
        if (FastSeekIsReliable)
        {
            SeekApproximate(frame);
        }
        else
        {
            SkipFrames(frame);
        }
    }

    /// <summary>
    /// Tacno preskakanje: dekodira i odbacuje zadati broj frejmova od trenutnog mesta.
    /// Sporije od premotavanja, ali daje iste uzorke kao dekodiranje od pocetka.
    /// Vraca broj stvarno preskocenih frejmova (manje ako se fajl zavrsi).
    /// </summary>
    public long SkipFrames(long frames)
    {
        long skipped = 0;
        while (skipped < frames)
        {
            var want = (int)Math.Min(_buffer.Length, (frames - skipped) * 2) & ~1;
            var read = _chain.Read(_buffer, 0, want);
            if (read <= 0)
            {
                break;
            }

            skipped += read / 2;
        }

        return skipped;
    }

    public void Dispose() => _reader.Dispose();

    private ISampleProvider BuildChain()
    {
        var provider = _reader as ISampleProvider ?? _reader.ToSampleProvider();

        if (SourceChannels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (SourceChannels > 2)
        {
            provider = new MultiplexingSampleProvider([provider], 2); // prva dva kanala
        }

        if (IsResampled)
        {
            provider = new WdlResamplingSampleProvider(provider, Format.SampleRate);
        }

        return provider;
    }
}
