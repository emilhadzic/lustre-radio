namespace LustreRadio.Analysis;

/// <summary>
/// Glasnoca programa po ITU-R BS.1770-4 / EBU R 128 (integrisana, sa dva praga).
/// Radi za bilo koji sample rate: koeficijenti K-filtera se racunaju iz analognog uzora,
/// a na 48 kHz daju tacno vrednosti iz standarda. Ulaz: stereo, isprepleteno (L, D, L, D…).
/// Podaci se dodaju u delovima bilo koje velicine; memorija raste 80 bajtova po sekundi zvuka.
/// </summary>
public sealed class LoudnessMeter
{
    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateLu = -10.0;
    private const int StepsPerBlock = 4; // blok 400 ms, korak 100 ms (preklapanje 75%)

    private readonly Biquad _shelfLeft;
    private readonly Biquad _shelfRight;
    private readonly Biquad _highLeft;
    private readonly Biquad _highRight;
    private readonly int _stepFrames;
    private readonly List<double> _stepEnergy = [];
    private double _currentEnergy;
    private int _currentFrames;

    public LoudnessMeter(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        SampleRate = sampleRate;
        _stepFrames = (int)Math.Round(sampleRate / 10.0);
        var shelf = KWeighting.Shelf(sampleRate);
        var high = KWeighting.HighPass(sampleRate);
        _shelfLeft = new Biquad(shelf);
        _shelfRight = new Biquad(shelf);
        _highLeft = new Biquad(high);
        _highRight = new Biquad(high);
    }

    public int SampleRate { get; }

    public void Add(ReadOnlySpan<float> interleavedStereo)
    {
        for (var i = 0; i + 1 < interleavedStereo.Length; i += 2)
        {
            var left = _highLeft.Process(_shelfLeft.Process(interleavedStereo[i]));
            var right = _highRight.Process(_shelfRight.Process(interleavedStereo[i + 1]));
            _currentEnergy += (left * left) + (right * right);
            if (++_currentFrames == _stepFrames)
            {
                _stepEnergy.Add(_currentEnergy / _stepFrames);
                _currentEnergy = 0;
                _currentFrames = 0;
            }
        }
    }

    /// <summary>
    /// Integrisana glasnoca u LUFS. Null ako je zvuk kraci od 400 ms ili je sav ispod −70 LUFS (tisina):
    /// tada glasnoca nije definisana i ne sme se izmisliti broj.
    /// </summary>
    public double? IntegratedLufs()
    {
        var blocks = _stepEnergy.Count - StepsPerBlock + 1;
        if (blocks <= 0)
        {
            return null;
        }

        var power = new double[blocks];
        var window = 0.0;
        for (var i = 0; i < _stepEnergy.Count; i++)
        {
            window += _stepEnergy[i];
            if (i >= StepsPerBlock)
            {
                window -= _stepEnergy[i - StepsPerBlock];
            }

            if (i >= StepsPerBlock - 1)
            {
                power[i - StepsPerBlock + 1] = Math.Max(0, window) / StepsPerBlock;
            }
        }

        var absolute = PowerOf(AbsoluteGateLufs);
        double sum = 0;
        var count = 0;
        foreach (var p in power)
        {
            if (p > absolute)
            {
                sum += p;
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        var relative = PowerOf(ToLufs(sum / count) + RelativeGateLu);
        sum = 0;
        count = 0;
        foreach (var p in power)
        {
            if (p > absolute && p > relative)
            {
                sum += p;
                count++;
            }
        }

        return count == 0 ? null : ToLufs(sum / count);
    }

    private static double ToLufs(double power) => -0.691 + (10 * Math.Log10(power));

    private static double PowerOf(double lufs) => Math.Pow(10, (lufs + 0.691) / 10);

    private sealed class Biquad(BiquadCoefficients c)
    {
        private double _x1;
        private double _x2;
        private double _y1;
        private double _y2;

        public double Process(double x)
        {
            var y = (c.B0 * x) + (c.B1 * _x1) + (c.B2 * _x2) - (c.A1 * _y1) - (c.A2 * _y2);
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}

public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2);

/// <summary>Dva filtera K-ponderisanja iz BS.1770: visoka polica (uticaj glave) i visokopropusni filter.</summary>
public static class KWeighting
{
    public static BiquadCoefficients Shelf(int sampleRate)
    {
        const double frequency = 1681.974450955533;
        const double gainDb = 3.999843853973347;
        const double q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * frequency / sampleRate);
        var vh = Math.Pow(10, gainDb / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + (k / q) + (k * k);
        return new BiquadCoefficients(
            (vh + (vb * k / q) + (k * k)) / a0,
            2 * ((k * k) - vh) / a0,
            (vh - (vb * k / q) + (k * k)) / a0,
            2 * ((k * k) - 1) / a0,
            (1 - (k / q) + (k * k)) / a0);
    }

    public static BiquadCoefficients HighPass(int sampleRate)
    {
        const double frequency = 38.13547087602444;
        const double q = 0.5003270373238773;
        var k = Math.Tan(Math.PI * frequency / sampleRate);
        var a0 = 1 + (k / q) + (k * k);
        return new BiquadCoefficients(1, -2, 1, 2 * ((k * k) - 1) / a0, (1 - (k / q) + (k * k)) / a0);
    }
}
