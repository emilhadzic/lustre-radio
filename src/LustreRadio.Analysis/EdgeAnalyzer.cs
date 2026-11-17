namespace LustreRadio.Analysis;

/// <summary>Rezultat analize krajeva pesme (Smart Crossfade, spec. 5.2, signali 1 i 2).</summary>
/// <param name="AudioStartFrame">Prvi frejm koji se cuje (uz malu rezervu), u sample rate-u fajla.</param>
/// <param name="AudioEndFrame">Frejm posle poslednjeg koji se cuje (uz malu rezervu), u sample rate-u fajla.</param>
/// <param name="OutroSeconds">Duzina stisavanja na kraju pesme; null ako se pesma zavrsava naglo (tada: obican prelaz).</param>
public sealed record EdgeResult(long AudioStartFrame, long AudioEndFrame, double? OutroSeconds);

/// <summary>
/// Analiza krajeva pesme u istom prolazu kao merenje glasnoce: tisina na pocetku i kraju i stisavanje na kraju.
/// Prag tisine je -60 dBFS (vrh uzorka). Ne zauzima memoriju po uzorku: pamti samo energiju po 100 ms.
/// </summary>
public sealed class EdgeAnalyzer
{
    /// <summary>Verzija analize (krajevi + tempo od verzije 2); pesme sa starijom verzijom se analiziraju ponovo.</summary>
    public const int Version = 2;

    /// <summary>Prag tisine: -60 dBFS.</summary>
    public const float SilenceThreshold = 0.001f;

    public const double MinOutroSeconds = 3;
    public const double MaxOutroSeconds = 10;

    private readonly int _sampleRate;
    private readonly int _blockFrames;
    private readonly List<float> _blockDb = [];
    private double _blockSum;
    private int _blockFill;
    private long _frames;
    private long _firstAudible = -1;
    private long _lastAudible = -1;

    public EdgeAnalyzer(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _sampleRate = sampleRate;
        _blockFrames = Math.Max(1, sampleRate / 10);
    }

    /// <summary>Dodaje stereo zvuk (L, D naizmenicno).</summary>
    public void Add(ReadOnlySpan<float> interleavedStereo)
    {
        for (var i = 0; i + 1 < interleavedStereo.Length; i += 2)
        {
            var l = interleavedStereo[i];
            var r = interleavedStereo[i + 1];
            if (Math.Abs(l) > SilenceThreshold || Math.Abs(r) > SilenceThreshold)
            {
                if (_firstAudible < 0)
                {
                    _firstAudible = _frames;
                }

                _lastAudible = _frames;
            }

            _blockSum += ((l * l) + (r * r)) * 0.5;
            if (++_blockFill == _blockFrames)
            {
                CloseBlock();
            }

            _frames++;
        }
    }

    /// <summary>Rezultat; null ako je ceo fajl tisina.</summary>
    public EdgeResult? Result()
    {
        if (_firstAudible < 0)
        {
            return null;
        }

        var margin = _sampleRate / 100; // 10 ms rezerve na oba kraja
        var start = Math.Max(0, _firstAudible - margin);
        var end = Math.Min(_frames, _lastAudible + 1 + margin);
        return new EdgeResult(start, end, Outro());
    }

    private void CloseBlock()
    {
        var meanSquare = _blockSum / Math.Max(1, _blockFill);
        _blockDb.Add(meanSquare > 1e-12 ? (float)(10 * Math.Log10(meanSquare)) : -120f);
        _blockSum = 0;
        _blockFill = 0;
    }

    /// <summary>
    /// Stisavanje na kraju: poslednje mesto gde je glasnoca (prosek 1 s) jos blizu tela pesme (do 3 dB ispod),
    /// a kraj je bar 12 dB tisi. Krace od 3 s znaci nagao kraj (null); duze se ogranicava na 10 s.
    /// </summary>
    private double? Outro()
    {
        var endBlock = (int)Math.Min(_blockDb.Count - 1, _lastAudible / _blockFrames);
        if (endBlock < 20)
        {
            return null; // prekratko za procenu
        }

        var audible = new List<float>();
        for (var i = 0; i <= endBlock; i++)
        {
            if (_blockDb[i] > -60)
            {
                audible.Add(_blockDb[i]);
            }
        }

        if (audible.Count < 20)
        {
            return null;
        }

        audible.Sort();
        var body = audible[(int)(audible.Count * 0.7)];
        var tail = Smoothed(Math.Max(0, endBlock - 5), endBlock);
        if (tail > body - 12)
        {
            return null; // kraj nije tisi od tela: nagao kraj
        }

        var limit = Math.Max(0, endBlock - 300); // trazi se u poslednjih 30 s
        var t = -1;
        for (var i = endBlock; i >= limit; i--)
        {
            if (Smoothed(i - 5, i + 4) >= body - 3)
            {
                t = i;
                break;
            }
        }

        var seconds = t < 0 ? MaxOutroSeconds : (endBlock - t) * 0.1;
        return seconds < MinOutroSeconds ? null : Math.Min(seconds, MaxOutroSeconds);
    }

    private double Smoothed(int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(_blockDb.Count - 1, to);
        double sum = 0;
        var n = 0;
        for (var i = from; i <= to; i++)
        {
            sum += _blockDb[i];
            n++;
        }

        return n == 0 ? -120 : sum / n;
    }
}
