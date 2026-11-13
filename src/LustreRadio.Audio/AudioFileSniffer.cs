using System.Text;

namespace LustreRadio.Audio;

public enum AudioKind
{
    Unknown,
    Wav,
    Mp3,
    Flac,
    Mp4,
    AacAdts,
    OggVorbis,
    OggOpus,
}

/// <summary>
/// Prepoznaje format po sadrzaju fajla, ne po ekstenziji (spec. deo 4: "ekstenziji se ne veruje").
/// </summary>
public static class AudioFileSniffer
{
    public static AudioKind Detect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> head = stackalloc byte[64];

        // ID3v2 oznake (naslov, slika albuma) stoje ispred zvuka; preskoci ih
        for (var guard = 0; guard < 4; guard++)
        {
            var start = stream.Position;
            var read = stream.ReadAtLeast(head[..10], 10, throwOnEndOfStream: false);
            if (read < 10 || head[0] != 'I' || head[1] != 'D' || head[2] != '3')
            {
                stream.Position = start;
                break;
            }

            var size = ((head[6] & 0x7F) << 21) | ((head[7] & 0x7F) << 14) | ((head[8] & 0x7F) << 7) | (head[9] & 0x7F);
            var footer = (head[5] & 0x10) != 0 ? 10 : 0;
            stream.Position = start + 10 + size + footer;
        }

        var count = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var bytes = head[..count];
        if (count < 4)
        {
            return AudioKind.Unknown;
        }

        if (StartsWith(bytes, "RIFF") && count >= 12 && StartsWith(bytes[8..], "WAVE"))
        {
            return AudioKind.Wav;
        }

        if (StartsWith(bytes, "fLaC"))
        {
            return AudioKind.Flac;
        }

        if (StartsWith(bytes, "OggS"))
        {
            return bytes.IndexOf("OpusHead"u8) >= 0 ? AudioKind.OggOpus
                : bytes.IndexOf("vorbis"u8) >= 0 ? AudioKind.OggVorbis
                : AudioKind.Unknown;
        }

        if (count >= 8 && StartsWith(bytes[4..], "ftyp"))
        {
            return AudioKind.Mp4;
        }

        if (bytes[0] == 0xFF && (bytes[1] & 0xF6) == 0xF0)
        {
            return AudioKind.AacAdts;
        }

        if (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0 && (bytes[1] & 0x06) != 0)
        {
            return AudioKind.Mp3;
        }

        return AudioKind.Unknown;
    }

    private static bool StartsWith(ReadOnlySpan<byte> bytes, string ascii) =>
        bytes.Length >= ascii.Length && Encoding.ASCII.GetString(bytes[..ascii.Length]) == ascii;
}
