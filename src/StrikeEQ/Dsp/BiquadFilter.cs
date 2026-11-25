using StrikeEQ.Models;

namespace StrikeEQ.Dsp;

/// <summary>
/// Robert Bristow-Johnson (RBJ) Audio EQ Cookbook stereo biquad filter
/// using Direct Form II Transposed for numerical stability at low frequencies.
/// </summary>
public sealed class BiquadFilter
{
    private double _b0 = 1.0, _b1, _b2, _a1, _a2;
    private double _z1L, _z2L;
    private double _z1R, _z2R;
    private double _sampleRate = 48000.0;
    private bool _bypass = true;

    public EqFilterType FilterType { get; private set; } = EqFilterType.Peaking;
    public double Frequency { get; private set; } = 1000.0;
    public double GainDb { get; private set; } = 0.0;
    public double Q { get; private set; } = 1.41;
    public bool Enabled { get; private set; } = true;

    public void Configure(double sampleRate, EqFilterType type, double frequency, double gainDb, double q, bool enabled)
    {
        _sampleRate = Math.Clamp(sampleRate, 8000.0, 384000.0);
        FilterType = type;
        Frequency = Math.Clamp(frequency, 15.0, Math.Min(21000.0, _sampleRate * 0.48));
        GainDb = Math.Clamp(gainDb, -24.0, 24.0);
        Q = Math.Clamp(q, 0.25, 18.0);
        Enabled = enabled;

        if (!enabled || (Math.Abs(GainDb) < 0.005 && type is EqFilterType.Peaking or EqFilterType.LowShelf or EqFilterType.HighShelf))
        {
            _b0 = 1.0; _b1 = 0.0; _b2 = 0.0; _a1 = 0.0; _a2 = 0.0;
            _bypass = true;
            return;
        }

        _bypass = false;
        double w0 = 2.0 * Math.PI * Frequency / _sampleRate;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * Q);
        double a = Math.Pow(10.0, GainDb / 40.0);

        double b0, b1, b2, a0, a1, a2;

        switch (type)
        {
            case EqFilterType.Peaking:
                b0 = 1.0 + alpha * a;
                b1 = -2.0 * cosW0;
                b2 = 1.0 - alpha * a;
                a0 = 1.0 + alpha / a;
                a1 = -2.0 * cosW0;
                a2 = 1.0 - alpha / a;
                break;

            case EqFilterType.LowShelf:
            {
                double sqrtA = Math.Sqrt(a);
                double shelfAlpha = sinW0 / 2.0 * Math.Sqrt((a + 1.0 / a) * (1.0 / Math.Clamp(Q, 0.3, 2.0) - 1.0) + 2.0);
                if (double.IsNaN(shelfAlpha)) shelfAlpha = alpha;
                double twoSqrtAAlpha = 2.0 * sqrtA * shelfAlpha;
                b0 = a * ((a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha);
                b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cosW0);
                b2 = a * ((a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha);
                a0 = (a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha;
                a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cosW0);
                a2 = (a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha;
                break;
            }

            case EqFilterType.HighShelf:
            {
                double sqrtA = Math.Sqrt(a);
                double shelfAlpha = sinW0 / 2.0 * Math.Sqrt((a + 1.0 / a) * (1.0 / Math.Clamp(Q, 0.3, 2.0) - 1.0) + 2.0);
                if (double.IsNaN(shelfAlpha)) shelfAlpha = alpha;
                double twoSqrtAAlpha = 2.0 * sqrtA * shelfAlpha;
                b0 = a * ((a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha);
                b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW0);
                b2 = a * ((a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha);
                a0 = (a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha;
                a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosW0);
                a2 = (a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha;
                break;
            }

            case EqFilterType.HighPass:
                b0 = (1.0 + cosW0) / 2.0;
                b1 = -(1.0 + cosW0);
                b2 = (1.0 + cosW0) / 2.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosW0;
                a2 = 1.0 - alpha;
                break;

            case EqFilterType.LowPass:
                b0 = (1.0 - cosW0) / 2.0;
                b1 = 1.0 - cosW0;
                b2 = (1.0 - cosW0) / 2.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosW0;
                a2 = 1.0 - alpha;
                break;

            default:
                b0 = 1.0; b1 = 0.0; b2 = 0.0; a0 = 1.0; a1 = 0.0; a2 = 0.0;
                break;
        }

        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public void ResetState()
    {
        _z1L = _z2L = _z1R = _z2R = 0.0;
    }

    public void ProcessStereo(ref float left, ref float right)
    {
        if (_bypass) return;

        double xL = left;
        double yL = _b0 * xL + _z1L;
        _z1L = _b1 * xL - _a1 * yL + _z2L;
        _z2L = _b2 * xL - _a2 * yL;
        // Flush denormals
        if (Math.Abs(_z1L) < 1e-18) _z1L = 0.0;
        if (Math.Abs(_z2L) < 1e-18) _z2L = 0.0;
        left = (float)yL;

        double xR = right;
        double yR = _b0 * xR + _z1R;
        _z1R = _b1 * xR - _a1 * yR + _z2R;
        _z2R = _b2 * xR - _a2 * yR;
        if (Math.Abs(_z1R) < 1e-18) _z1R = 0.0;
        if (Math.Abs(_z2R) < 1e-18) _z2R = 0.0;
        right = (float)yR;
    }

    /// <summary>
    /// Exact magnitude response |H(e^{j*w})| in dB at any frequency for plotting the EQ curve.
    /// </summary>
    public double GetMagnitudeDb(double frequencyHz)
    {
        if (_bypass) return 0.0;

        double w = 2.0 * Math.PI * frequencyHz / _sampleRate;
        double cosW = Math.Cos(w);
        double sinW = Math.Sin(w);
        double cos2W = 2.0 * cosW * cosW - 1.0;
        double sin2W = 2.0 * sinW * cosW;

        double numRe = _b0 + _b1 * cosW + _b2 * cos2W;
        double numIm = -(_b1 * sinW + _b2 * sin2W);
        double denRe = 1.0 + _a1 * cosW + _a2 * cos2W;
        double denIm = -(_a1 * sinW + _a2 * sin2W);

        double magSq = (numRe * numRe + numIm * numIm) / Math.Max(1e-24, denRe * denRe + denIm * denIm);
        return 10.0 * Math.Log10(Math.Max(1e-12, magSq));
    }

    public static double ComputeBandMagnitudeDb(EqBand band, double frequencyHz, double sampleRate = 48000.0)
    {
        var filter = new BiquadFilter();
        filter.Configure(sampleRate, band.FilterType, band.Frequency, band.GainDb, band.Q, band.Enabled);
        return filter.GetMagnitudeDb(frequencyHz);
    }
}
