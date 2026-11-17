namespace LustreRadio.Core.Diagnostics;

/// <summary>Posao koji audio-nit ne sme da radi (specifikacija 2.3).</summary>
public enum AudioViolation
{
    /// <summary>Otvaranje fajla ili citanje podataka o fajlu.</summary>
    FileAccess = 0,

    /// <summary>Dekodiranje zvuka.</summary>
    Decode = 1,

    /// <summary>Rad sa bazom.</summary>
    Database = 2,

    /// <summary>Pisanje u dnevnik programa.</summary>
    Log = 3,

    /// <summary>Cekanje na bravu.</summary>
    Lock = 4,
}

/// <summary>
/// Cuvar audio-niti. Nit za pripremu zvuka i nit zvucnog uredjaja se obelezavaju; mesta u kodu
/// koja audio-nit ne bi smela da dotakne prijavljuju se ovde.
/// FAZA B0: cuvar SAMO BROJI. Ne baca gresku i ne menja ponasanje programa (plan faze B, v8, korak B0).
/// </summary>
public static class AudioThreadGuard
{
    private static readonly long[] Counts = new long[5];

    [ThreadStatic]
    private static bool t_audio;

    [ThreadStatic]
    private static long t_count;

    public static bool IsAudioThread => t_audio;

    /// <summary>Obelezava trenutnu nit kao audio-nit.</summary>
    public static void MarkCurrentThread() => t_audio = true;

    public static void UnmarkCurrentThread() => t_audio = false;

    /// <summary>Prijava: ako je trenutna nit audio-nit, krsenje se broji.</summary>
    public static void Touch(AudioViolation kind)
    {
        if (t_audio)
        {
            Interlocked.Increment(ref Counts[(int)kind]);
            t_count++;
        }
    }

    /// <summary>Broj prijava na trenutnoj niti (dok je bila obelezena kao audio-nit).</summary>
    public static long CurrentThreadCount => t_count;

    public static long Count(AudioViolation kind) => Interlocked.Read(ref Counts[(int)kind]);

    public static long Total
    {
        get
        {
            long sum = 0;
            for (var i = 0; i < Counts.Length; i++)
            {
                sum += Interlocked.Read(ref Counts[i]);
            }

            return sum;
        }
    }

    /// <summary>Kratak opis za dnevnik: "fajl=0 dekoder=0 baza=0 dnevnik=0 brava=0".</summary>
    public static string Describe() =>
        $"fajl={Count(AudioViolation.FileAccess)} dekoder={Count(AudioViolation.Decode)} baza={Count(AudioViolation.Database)} dnevnik={Count(AudioViolation.Log)} brava={Count(AudioViolation.Lock)}";
}
