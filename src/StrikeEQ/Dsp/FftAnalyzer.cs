using System.Numerics;

namespace StrikeEQ.Dsp;

/// <summary>
/// 2048-point Radix-2 Cooley-Tukey FFT spectrum analyzer with Hann windowing,
/// logarithmic frequency bin mapping (20 Hz - 20 kHz), exponential smoothing, and peak hold.
/// </summary>
public sealed class FftAnalyzer
{
    public const int FftSize = 2048;
    public const int BarCount = 64;

    private readonly float[] _ringBuffer = new float[FftSize];
    private readonly double[] _hannWindow = new double[FftSize];
    private readonly Complex[] _fftWork = new Complex[FftSize];
    private readonly float[] _smoothedBars = new float[BarCount];
    private readonly float[] _peakHoldBars = new float[BarCount];
    private readonly int[] _barStartBin = new int[BarCount];
    private readonly int[] _barEndBin = new int[BarCount];
    private readonly object _lock = new();

    private int _writePos;
    private int _sampleRate = 48000;
    private long _lastSignalTicks;

    public FftAnalyzer(int sampleRate = 48000)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _hannWindow[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
        }
        SetSampleRate(sampleRate);
    }

    public void SetSampleRate(int sampleRate)
    {
        lock (_lock)
        {
            _sampleRate = Math.Clamp(sampleRate, 8000, 384000);
            double minFreq = 20.0;
            double maxFreq = Math.Min(20000.0, _sampleRate * 0.49);
            double binHz = (double)_sampleRate / FftSize;

            for (int i = 0; i < BarCount; i++)
            {
                double t0 = (double)i / BarCount;
                double t1 = (double)(i + 1) / BarCount;
                double f0 = minFreq * Math.Pow(maxFreq / minFreq, t0);
                double f1 = minFreq * Math.Pow(maxFreq / minFreq, t1);

                int b0 = Math.Clamp((int)Math.Floor(f0 / binHz), 1, FftSize / 2 - 1);
                int b1 = Math.Clamp((int)Math.Ceiling(f1 / binHz), b0 + 1, FftSize / 2);
                _barStartBin[i] = b0;
                _barEndBin[i] = b1;
            }
        }
    }

    public void AddMonoSample(float sample)
    {
        lock (_lock)
        {
            _ringBuffer[_writePos] = sample;
            _writePos = (_writePos + 1) & (FftSize - 1);
            if (Math.Abs(sample) > 1e-5f)
                _lastSignalTicks = Environment.TickCount64;
        }
    }

    public void AddStereoSamples(ReadOnlySpan<float> interleaved)
    {
        lock (_lock)
        {
            bool hasSignal = false;
            for (int i = 0; i + 1 < interleaved.Length; i += 2)
            {
                float mono = 0.5f * (interleaved[i] + interleaved[i + 1]);
                _ringBuffer[_writePos] = mono;
                _writePos = (_writePos + 1) & (FftSize - 1);
                if (Math.Abs(mono) > 1e-5f) hasSignal = true;
            }
            if (hasSignal)
                _lastSignalTicks = Environment.TickCount64;
        }
    }

    public void DecayWhenIdle()
    {
        lock (_lock)
        {
            for (int i = 0; i < BarCount; i++)
            {
                _smoothedBars[i] *= 0.86f;
                if (_smoothedBars[i] < 0.002f) _smoothedBars[i] = 0f;
                _peakHoldBars[i] = Math.Max(0f, _peakHoldBars[i] - 0.012f);
            }
        }
    }

    public void GetSpectrumSnapshot(float[] barsOut, float[] peaksOut)
    {
        lock (_lock)
        {
            bool isIdle = Environment.TickCount64 - _lastSignalTicks > 180;
            if (isIdle)
            {
                for (int i = 0; i < BarCount; i++)
                {
                    _smoothedBars[i] *= 0.84f;
                    if (_smoothedBars[i] < 0.002f) _smoothedBars[i] = 0f;
                    _peakHoldBars[i] = Math.Max(_smoothedBars[i], _peakHoldBars[i] - 0.012f);
                    barsOut[i] = _smoothedBars[i];
                    peaksOut[i] = _peakHoldBars[i];
                }
                return;
            }

            int pos = _writePos;
            for (int i = 0; i < FftSize; i++)
            {
                float s = _ringBuffer[(pos + i) & (FftSize - 1)];
                _fftWork[i] = new Complex(s * _hannWindow[i], 0.0);
            }

            ComputeFftInPlace(_fftWork);

            double scale = 2.0 / (FftSize * 0.5); // Compensate for Hann window coherent gain
            for (int b = 0; b < BarCount; b++)
            {
                int start = _barStartBin[b];
                int end = _barEndBin[b];
                double maxMag = 1e-9;
                for (int k = start; k < end; k++)
                {
                    double mag = _fftWork[k].Magnitude * scale;
                    if (mag > maxMag) maxMag = mag;
                }

                double db = 20.0 * Math.Log10(maxMag);
                // Map -78 dB .. 0 dB -> 0 .. 1 with slight high-frequency visual tilt (+1.5 dB/oct)
                double tilt = (double)b / BarCount * 6.0;
                float norm = (float)Math.Clamp((db + tilt + 76.0) / 76.0, 0.0, 1.0);

                // Fast attack, smooth release
                float prev = _smoothedBars[b];
                float smoothed = norm > prev ? prev + (norm - prev) * 0.58f : prev * 0.85f + norm * 0.15f;
                _smoothedBars[b] = smoothed;

                if (smoothed >= _peakHoldBars[b])
                    _peakHoldBars[b] = smoothed;
                else
                    _peakHoldBars[b] = Math.Max(smoothed, _peakHoldBars[b] - 0.0075f);

                barsOut[b] = _smoothedBars[b];
                peaksOut[b] = _peakHoldBars[b];
            }
        }
    }

    public static void ComputeFftInPlace(Complex[] buffer)
    {
        int n = buffer.Length;
        int bits = (int)Math.Log2(n);

        for (int i = 0; i < n; i++)
        {
            int j = BitReverse(i, bits);
            if (j > i)
            {
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            var wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            int half = len >> 1;

            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int j = 0; j < half; j++)
                {
                    var u = buffer[i + j];
                    var v = buffer[i + j + half] * w;
                    buffer[i + j] = u + v;
                    buffer[i + j + half] = u - v;
                    w *= wLen;
                }
            }
        }
    }

    private static int BitReverse(int x, int bits)
    {
        int r = 0;
        for (int i = 0; i < bits; i++)
        {
            r = (r << 1) | (x & 1);
            x >>= 1;
        }
        return r;
    }

    public static double FindDominantFrequency(float[] monoSamples, int sampleRate)
    {
        var buf = new Complex[FftSize];
        int count = Math.Min(monoSamples.Length, FftSize);
        for (int i = 0; i < count; i++)
        {
            double win = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
            buf[i] = new Complex(monoSamples[i] * win, 0.0);
        }
        ComputeFftInPlace(buf);

        int peakBin = 1;
        double peakMag = 0.0;
        for (int k = 1; k < FftSize / 2; k++)
        {
            double m = buf[k].Magnitude;
            if (m > peakMag)
            {
                peakMag = m;
                peakBin = k;
            }
        }
        return peakBin * (double)sampleRate / FftSize;
    }
}
