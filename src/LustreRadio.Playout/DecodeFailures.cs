using System.Runtime.InteropServices;
using LustreRadio.Audio;
using LustreRadio.Core.History;

namespace LustreRadio.Playout;

/// <summary>Vrsta prestanka dekodiranja (plan faze B, 2.7, slucaj 1).</summary>
public enum DecodeFailureKind
{
    /// <summary>Dekoder javio da su podaci neispravni (spisak poznatih gresaka). Broji se samo ako je potvrdjena.</summary>
    ExplicitDecoderError,

    /// <summary>Greska koja nije na spisku: nikad se ne broji.</summary>
    UndeterminedDecoderError,

    /// <summary>Greska citanja ili pristupa; uredjaj nedostupan. Ne broji se.</summary>
    ReadError,

    /// <summary>Kraj ranije od duzine izmerene pri skeniranju. Ne broji se; pokrece ponovno merenje.</summary>
    UnexpectedEnd,
}

/// <summary>Ishod provere greske.</summary>
public enum FaultCheck
{
    Confirmed,
    NotRepeated,
    FileChanged,
    CheckFailed,
}

/// <summary>
/// Razvrstavanje i provera gresaka (slucaj 1). U pravilo "3 greske -> van rotacije" ulazi SAMO potvrdjena eksplicitna
/// greska: fajl nije menjan, ponovo se otvara istim dekoderom i ista vrsta greske se ponovi na istom mestu (u okviru 1 s).
/// Provera radi u pozadini, snizenim prioritetom; nikad na audio-niti ni na koordinatoru.
/// </summary>
public static class DecodeFailures
{
    /// <summary>HRESULT-ovi Media Foundation koji znace "neispravni podaci" (spisak se proverava testom B-129).</summary>
    public static readonly IReadOnlySet<int> MediaFoundationDataErrors = new HashSet<int>
    {
        unchecked((int)0xC00D36CB), // MF_E_INVALID_STREAM_DATA
        unchecked((int)0xC00D36C4), // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
        unchecked((int)0xC00D36B4), // MF_E_INVALIDMEDIATYPE
        unchecked((int)0xC00D5212), // MF_E_TOPO_CODEC_NOT_FOUND
        unchecked((int)0xC00D3E85), // MF_E_INVALID_FILE_FORMAT
    };

    public static DecodeFailureKind Classify(StopReason reason, Exception? error) => (reason, error) switch
    {
        (StopReason.UnexpectedEnd, _) => DecodeFailureKind.UnexpectedEnd,
        (_, IOException and not EndOfStreamException) or (_, UnauthorizedAccessException) => DecodeFailureKind.ReadError,
        (_, InvalidDataException) or (_, FormatException) or (_, EndOfStreamException) => DecodeFailureKind.ExplicitDecoderError,
        (_, COMException com) when MediaFoundationDataErrors.Contains(com.HResult) => DecodeFailureKind.ExplicitDecoderError,
        (_, AudioDecodeException) => DecodeFailureKind.ExplicitDecoderError,
        _ => DecodeFailureKind.UndeterminedDecoderError,
    };

    /// <summary>
    /// Ponovna provera: <paramref name="reopen"/> otvara fajl istim dekoderom; ista vrsta greske mora da se ponovi
    /// najkasnije 1 s posle mesta prve greske. <paramref name="unchanged"/>: fajl nije menjan od skeniranja.
    /// </summary>
    public static FaultCheck Confirm(Func<ISampleSource> reopen, Func<bool> unchanged, Exception original, long positionFrames, AudioFormat format, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(reopen);
        ArgumentNullException.ThrowIfNull(unchanged);
        ArgumentNullException.ThrowIfNull(original);
        if (!unchanged())
        {
            return FaultCheck.FileChanged;
        }

        var limit = positionFrames + format.SampleRate;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var source = reopen();
            var buffer = new float[4096 * 2];
            long position = 0;
            while (position <= limit)
            {
                if (watch.Elapsed > timeout)
                {
                    return FaultCheck.CheckFailed;
                }

                int read;
                try
                {
                    read = source.Read(buffer);
                }
                catch (Exception again) when (again.GetType() == original.GetType())
                {
                    return position >= positionFrames - format.SampleRate ? FaultCheck.Confirmed : FaultCheck.NotRepeated;
                }

                if (read <= 0)
                {
                    return FaultCheck.NotRepeated;
                }

                position += read;
            }

            return FaultCheck.NotRepeated;
        }
        catch (Exception)
        {
            return FaultCheck.CheckFailed;
        }
    }

    /// <summary>Ponovno merenje duzine (posle neocekivanog kraja): broji sve frejmove fajla. -1 ako ne uspe.</summary>
    public static long Measure(Func<ISampleSource> reopen, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(reopen);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var source = reopen();
            var buffer = new float[8192 * 2];
            long frames = 0;
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                frames += read;
                if (watch.Elapsed > timeout)
                {
                    return -1;
                }
            }

            return frames;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
