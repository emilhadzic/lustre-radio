namespace LustreRadio.Core.Loudness;

/// <summary>Podesavanja glasnoce (spec. 2.4). Sve vrednosti su parametri koje admin moze da menja.</summary>
public sealed record LoudnessSettings
{
    /// <summary>Ciljna glasnoca u LUFS. Pocetna vrednost -18 (testira se -18 do -20).</summary>
    public double TargetLufs { get; init; } = -18.0;

    /// <summary>Najvece dozvoljeno pojacanje tihe pesme, u dB.</summary>
    public double MaxBoostDb { get; init; } = 6.0;

    /// <summary>Vrh pesme posle pojacanja ne sme preci ovu vrednost (dBFS).</summary>
    public double PeakCeilingDb { get; init; } = -1.0;

    /// <summary>Pojacanje za pesmu koja jos nije izmerena (vecina muzike je glasnija od cilja).</summary>
    public double UnmeasuredGainDb { get; init; } = -6.0;
}

/// <summary>
/// Racuna pojacanje jedne pesme. Tiha pesma koja ne moze do cilja ostaje malo tisa,
/// umesto da je limiter gnjeci; limiter je samo osigurac.
/// </summary>
public static class TrackGain
{
    /// <param name="integratedLufs">Izmerena glasnoca pesme, ili null ako nije merena.</param>
    /// <param name="peakDbfs">Najvisi vrh pesme u dBFS, ili null ako nije meren.</param>
    public static double ComputeDb(double? integratedLufs, double? peakDbfs, LoudnessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        double gain;
        if (integratedLufs is double lufs && double.IsFinite(lufs))
        {
            gain = Math.Min(settings.TargetLufs - lufs, settings.MaxBoostDb);
        }
        else
        {
            gain = settings.UnmeasuredGainDb;
        }

        if (peakDbfs is double peak && double.IsFinite(peak))
        {
            gain = Math.Min(gain, settings.PeakCeilingDb - peak);
        }

        return gain;
    }

    public static float ToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);
}
