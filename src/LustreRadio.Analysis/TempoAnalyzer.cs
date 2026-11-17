namespace LustreRadio.Analysis;

/// <summary>Tempo i mreza udaraca (DJ Mix, spec. 5.3).</summary>
/// <param name="Bpm">Udari u minuti.</param>
/// <param name="BeatPeriodFrames">Razmak udaraca u frejmovima fajla (precizno, iz cele pesme).</param>
/// <param name="FirstBeatFrame">Prvi udarac mreze, u frejmovima fajla.</param>
/// <param name="Stable">Tempo stalan kroz celu pesmu i udarci jasni; samo tada sme DJ Mix.</param>
public sealed record TempoResult(double Bpm, double BeatPeriodFrames, long FirstBeatFrame, bool Stable);

/// <summary>
/// Procena tempa u istom prolazu kao glasnoca. Pamti samo krivu pocetaka zvuka (jedan broj na 256 frejmova).
/// Postupak: kriva pocetaka -> autokorelacija (60-180 BPM) -> faza -> pracenje udarac po udarac uz linearnu regresiju
/// -> stabilnost (odstupanje udaraca, razlika tempa u prvoj i drugoj polovini, udeo jasnih udaraca).
/// Svaka nesigurnost daje Stable = false, pa radio koristi Smart Crossfade.
/// </summary>
public sealed class TempoAnalyzer
{
    private const int Hop = 256;

    /// <summary>Najvece dozvoljeno odstupanje udarca od mreze (ms) za stabilan tempo.</summary>
    public const double MaxResidualMs = 20;

    /// <summary>Najveca razlika tempa izmedju prve i druge polovine pesme za stabilan tempo.</summary>
    public const double MaxDrift = 0.002;

    private readonly int _sampleRate;
    private readonly List<float> _onset = [];
    private double _low;
    private double _lowEnergy;
    private double _fullEnergy;
    private int _fill;
    private double _prevLow = -100;
    private double _prevFull = -100;
    private readonly double _alpha;

    public TempoAnalyzer(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _sampleRate = sampleRate;
        _alpha = 1 - Math.Exp(-2 * Math.PI * 200 / sampleRate); // niskopropusni ~200 Hz (bubanj, bas)
    }

    public void Add(ReadOnlySpan<float> interleavedStereo)
    {
        for (var i = 0; i + 1 < interleavedStereo.Length; i += 2)
        {
            var mono = 0.5 * (interleavedStereo[i] + interleavedStereo[i + 1]);
            _low += _alpha * (mono - _low);
            _lowEnergy += _low * _low;
            _fullEnergy += mono * mono;
            if (++_fill == Hop)
            {
                var low = 10 * Math.Log10((_lowEnergy / Hop) + 1e-10);
                var full = 10 * Math.Log10((_fullEnergy / Hop) + 1e-10);
                _onset.Add((float)(Math.Max(0, low - _prevLow) + (0.5 * Math.Max(0, full - _prevFull))));
                _prevLow = low;
                _prevFull = full;
                _lowEnergy = 0;
                _fullEnergy = 0;
                _fill = 0;
            }
        }
    }

    /// <summary>Rezultat; null ako pesma nema prepoznatljiv ritam ili je prekratka (ispod 30 s).</summary>
    public TempoResult? Result()
    {
        var hopsPerSecond = (double)_sampleRate / Hop;
        var n = _onset.Count;
        if (n < hopsPerSecond * 30)
        {
            return null;
        }

        var env = new double[n];
        double mean = 0;
        for (var i = 0; i < n; i++)
        {
            mean += _onset[i];
        }

        mean /= n;
        double variance = 0;
        for (var i = 0; i < n; i++)
        {
            env[i] = _onset[i] - mean;
            variance += env[i] * env[i];
        }

        var std = Math.Sqrt(variance / n);
        if (std < 1e-6)
        {
            return null;
        }

        // 1. autokorelacija, uz blagu prednost tempima oko 120 BPM (protiv "pola" i "duplo")
        var minLag = (int)Math.Floor(hopsPerSecond * 60 / 180);
        var maxLag = (int)Math.Ceiling(hopsPerSecond * 60 / 60);
        var ac = new double[maxLag + 2];
        for (var lag = minLag - 1; lag <= maxLag + 1 && lag < n; lag++)
        {
            double sum = 0;
            for (var i = lag; i < n; i++)
            {
                sum += env[i] * env[i - lag];
            }

            ac[lag] = sum / (n - lag);
        }

        var bestLag = -1;
        double bestScore = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var bpm = 60 * hopsPerSecond / lag;
            var weight = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120) / 0.9, 2));
            var score = ac[lag] * weight;
            if (ac[lag] > ac[lag - 1] && ac[lag] >= ac[lag + 1] && score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (bestLag < 0 || bestScore <= 0)
        {
            return null;
        }

        var denom = ac[bestLag - 1] - (2 * ac[bestLag]) + ac[bestLag + 1];
        var period = bestLag + (Math.Abs(denom) > 1e-12 ? 0.5 * (ac[bestLag - 1] - ac[bestLag + 1]) / denom : 0);

        // 2. faza: pocetak mreze sa najvise jakih pocetaka
        var bestPhase = 0;
        double bestPhaseScore = double.MinValue;
        for (var phase = 0; phase < (int)Math.Ceiling(period); phase++)
        {
            double sum = 0;
            for (var t = (double)phase; t < n; t += period)
            {
                sum += env[(int)t];
            }

            if (sum > bestPhaseScore)
            {
                bestPhaseScore = sum;
                bestPhase = phase;
            }
        }

        // 3. pracenje udarac po udarac: predvidjanje iz dosadasnje mreze, trazenje vrha u +-15% razmaka
        var threshold = std;
        var a = (double)bestPhase;
        var b = period;
        var beats = new List<(double K, double T)>();
        var total = 0;
        for (var k = 0; ; k++)
        {
            var predicted = a + (b * k);
            if (predicted >= n - 2)
            {
                break;
            }

            total++;
            var from = Math.Max(1, (int)Math.Floor(predicted - (0.15 * b)));
            var to = Math.Min(n - 2, (int)Math.Ceiling(predicted + (0.15 * b)));
            var peak = -1;
            for (var i = from; i <= to; i++)
            {
                if (peak < 0 || env[i] > env[peak])
                {
                    peak = i;
                }
            }

            if (peak < 0 || env[peak] < threshold)
            {
                continue;
            }

            var d = env[peak - 1] - (2 * env[peak]) + env[peak + 1];
            var t = peak + (Math.Abs(d) > 1e-12 ? 0.5 * (env[peak - 1] - env[peak + 1]) / d : 0);
            beats.Add((k, t));
            if (beats.Count >= 8 && beats.Count % 4 == 0)
            {
                (a, b) = Fit(beats, 0, beats.Count);
            }
        }

        if (beats.Count < 32)
        {
            return null;
        }

        (a, b) = Fit(beats, 0, beats.Count);
        double residual = 0;
        foreach (var (k, t) in beats)
        {
            var e = t - (a + (b * k));
            residual += e * e;
        }

        var residualMs = Math.Sqrt(residual / beats.Count) / hopsPerSecond * 1000;
        var half = beats.Count / 2;
        var b1 = Fit(beats, 0, half).B;
        var b2 = Fit(beats, half, beats.Count).B;
        var drift = Math.Abs(b1 - b2) / b;
        var coverage = (double)beats.Count / Math.Max(1, total);
        var stable = residualMs <= MaxResidualMs && drift <= MaxDrift && coverage >= 0.5;

        // vreme pocetka udarca: sredina prozora od 256 frejmova u kom je porast energije
        var periodFrames = b * Hop;
        var first = (a * Hop) + (Hop / 2.0);
        while (first - periodFrames >= 0)
        {
            first -= periodFrames;
        }

        while (first < 0)
        {
            first += periodFrames;
        }

        return new TempoResult(60.0 * _sampleRate / periodFrames, periodFrames, (long)Math.Round(first), stable);
    }

    private static (double A, double B) Fit(List<(double K, double T)> beats, int from, int to)
    {
        double sk = 0, st = 0, skk = 0, skt = 0;
        var m = to - from;
        for (var i = from; i < to; i++)
        {
            var (k, t) = beats[i];
            sk += k;
            st += t;
            skk += k * k;
            skt += k * t;
        }

        var den = (m * skk) - (sk * sk);
        if (m < 2 || Math.Abs(den) < 1e-9)
        {
            return (beats[from].T, 1);
        }

        var b = ((m * skt) - (sk * st)) / den;
        return ((st - (b * sk)) / m, b);
    }
}
