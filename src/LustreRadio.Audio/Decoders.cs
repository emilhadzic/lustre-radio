using NAudio.Wave;

namespace LustreRadio.Audio;

/// <summary>Fajl ne moze da se procita nijednim dekoderom.</summary>
public sealed class AudioDecodeException(string path, string details)
    : Exception($"Fajl ne moze da se dekodira: {path}. {details}")
{
    public string FilePath { get; } = path;
}

/// <summary>
/// Izbor dekodera (spec. deo 4). Dekoder se bira jednom i njegov ID se cuva uz pesmu,
/// da analiza i reprodukcija uvek koriste isti.
/// </summary>
public static class Decoders
{
    public const string Wav = "naudio-wav";
    public const string MediaFoundation = "mf";
    public const string Vorbis = "nvorbis";
    public const string NLayer = "nlayer";

    /// <summary>Dekoderi koji se probaju za dati format, redom.</summary>
    public static IReadOnlyList<string> CandidatesFor(AudioKind kind) => kind switch
    {
        AudioKind.Wav => [Wav, MediaFoundation],
        AudioKind.Mp3 => [MediaFoundation, NLayer],
        AudioKind.Flac => [MediaFoundation],
        AudioKind.Mp4 => [MediaFoundation],
        AudioKind.AacAdts => [MediaFoundation],
        AudioKind.OggVorbis => [Vorbis],
        AudioKind.OggOpus => [],
        _ => [MediaFoundation],
    };

    /// <summary>
    /// Otvara fajl. Ako je zadat decoderId, koristi se samo taj dekoder (bez rezervnog).
    /// </summary>
    public static AudioFileSource Open(string path, AudioFormat target, string? decoderId = null)
    {
        var kind = AudioFileSniffer.Detect(path);
        var candidates = decoderId is null ? CandidatesFor(kind) : [decoderId];
        if (candidates.Count == 0)
        {
            throw new AudioDecodeException(path, $"Format jos nije podrzan: {kind}.");
        }

        var errors = new List<string>();
        foreach (var id in candidates)
        {
            WaveStream? reader = null;
            try
            {
                reader = Create(id, path);
                return new AudioFileSource(reader, target, id, path);
            }
            catch (Exception ex)
            {
                reader?.Dispose();
                errors.Add($"{id}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        throw new AudioDecodeException(path, string.Join(" | ", errors));
    }

    private static WaveStream Create(string decoderId, string path) => decoderId switch
    {
        Wav => new WaveFileReader(path),
        MediaFoundation => new MediaFoundationReader(
            path,
            new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true }),
        Vorbis => new NAudio.Vorbis.VorbisWaveReader(path),
        NLayer => new Mp3FileReaderBase(path, format => new global::NLayer.NAudioSupport.Mp3FrameDecompressor(format)),
        _ => throw new ArgumentException($"Nepoznat dekoder: {decoderId}", nameof(decoderId)),
    };
}
