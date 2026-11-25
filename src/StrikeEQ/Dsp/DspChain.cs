using StrikeEQ.Models;

namespace StrikeEQ.Dsp;

/// <summary>
/// Full stereo real-time DSP processing chain:
/// Input -> Preamp -> 10-Band Parametric Biquad EQ -> Scout/Night Mode Compressor & Limiter -> Output.
/// Supports click-free A/B bypass and live level/GR metering.
/// </summary>
public sealed class DspChain
{
    public const int MaxBands = 10;

    private readonly BiquadFilter[] _filters = new BiquadFilter[MaxBands];
    private readonly DynamicProcessor _dynamics = new();
    private readonly object _configLock = new();

    private double _sampleRate = 48000.0;
    private float _preampLinear = 1.0f;
    private volatile bool _bypass;
    private float _bypassMix = 1.0f; // 1.0 = wet (EQ active), 0.0 = dry (bypassed)

    private volatile float _inputPeakL;
    private volatile float _inputPeakR;
    private volatile float _outputPeakL;
    private volatile float _outputPeakR;

    public FftAnalyzer FftAnalyzer { get; } = new();

    public bool Bypass
    {
        get => _bypass;
        set => _bypass = value;
    }

    public float InputPeakL => _inputPeakL;
    public float InputPeakR => _inputPeakR;
    public float OutputPeakL => _outputPeakL;
    public float OutputPeakR => _outputPeakR;
    public float GainReductionDb => _bypass ? 0f : _dynamics.CurrentGainReductionDb;

    public DspChain()
    {
        for (int i = 0; i < MaxBands; i++)
            _filters[i] = new BiquadFilter();
    }

    public void SetSampleRate(double sampleRate)
    {
        lock (_configLock)
        {
            _sampleRate = Math.Clamp(sampleRate, 8000.0, 384000.0);
            FftAnalyzer.SetSampleRate((int)_sampleRate);
        }
    }

    public void ApplyProfile(EqProfile profile)
    {
        lock (_configLock)
        {
            _preampLinear = (float)Math.Pow(10.0, Math.Clamp(profile.PreampDb, -18.0, 18.0) / 20.0);

            for (int i = 0; i < MaxBands; i++)
            {
                if (i < profile.Bands.Count)
                {
                    var b = profile.Bands[i];
                    _filters[i].Configure(_sampleRate, b.FilterType, b.Frequency, b.GainDb, b.Q, b.Enabled);
                }
                else
                {
                    _filters[i].Configure(_sampleRate, EqFilterType.Peaking, 1000.0, 0.0, 1.41, false);
                }
            }

            _dynamics.Configure(_sampleRate, profile.Compressor);
        }
    }

    public void UpdateBand(EqBand band)
    {
        if (band.Index < 0 || band.Index >= MaxBands) return;
        lock (_configLock)
        {
            _filters[band.Index].Configure(_sampleRate, band.FilterType, band.Frequency, band.GainDb, band.Q, band.Enabled);
        }
    }

    public void UpdatePreamp(double preampDb)
    {
        lock (_configLock)
        {
            _preampLinear = (float)Math.Pow(10.0, Math.Clamp(preampDb, -18.0, 18.0) / 20.0);
        }
    }

    public void UpdateCompressor(CompressorSettings settings)
    {
        lock (_configLock)
        {
            _dynamics.Configure(_sampleRate, settings);
        }
    }

    public void ResetStates()
    {
        lock (_configLock)
        {
            foreach (var f in _filters) f.ResetState();
            _dynamics.Reset();
        }
    }

    public void ProcessInterleavedStereo(Span<float> samples)
    {
        float inMaxL = 0f, inMaxR = 0f;
        float outMaxL = 0f, outMaxR = 0f;

        lock (_configLock)
        {
            float targetMix = _bypass ? 0.0f : 1.0f;
            float mixStep = 1.0f / Math.Max(64.0f, (float)(_sampleRate * 0.008)); // 8ms click-free crossfade

            for (int i = 0; i + 1 < samples.Length; i += 2)
            {
                float dryL = samples[i];
                float dryR = samples[i + 1];

                float absInL = Math.Abs(dryL);
                float absInR = Math.Abs(dryR);
                if (absInL > inMaxL) inMaxL = absInL;
                if (absInR > inMaxR) inMaxR = absInR;

                if (_bypassMix < targetMix) _bypassMix = Math.Min(targetMix, _bypassMix + mixStep);
                else if (_bypassMix > targetMix) _bypassMix = Math.Max(targetMix, _bypassMix - mixStep);

                float wetL = dryL * _preampLinear;
                float wetR = dryR * _preampLinear;

                for (int b = 0; b < MaxBands; b++)
                    _filters[b].ProcessStereo(ref wetL, ref wetR);

                _dynamics.ProcessStereo(ref wetL, ref wetR);

                float outL = dryL + (wetL - dryL) * _bypassMix;
                float outR = dryR + (wetR - dryR) * _bypassMix;

                samples[i] = outL;
                samples[i + 1] = outR;

                float absOutL = Math.Abs(outL);
                float absOutR = Math.Abs(outR);
                if (absOutL > outMaxL) outMaxL = absOutL;
                if (absOutR > outMaxR) outMaxR = absOutR;
            }
        }

        // Smooth meter decay
        _inputPeakL = Math.Max(inMaxL, _inputPeakL * 0.85f);
        _inputPeakR = Math.Max(inMaxR, _inputPeakR * 0.85f);
        _outputPeakL = Math.Max(outMaxL, _outputPeakL * 0.85f);
        _outputPeakR = Math.Max(outMaxR, _outputPeakR * 0.85f);

        FftAnalyzer.AddStereoSamples(samples);
    }

    public double GetCombinedMagnitudeDb(double frequencyHz)
    {
        lock (_configLock)
        {
            double db = 20.0 * Math.Log10(Math.Max(1e-6, _preampLinear));
            for (int i = 0; i < MaxBands; i++)
                db += _filters[i].GetMagnitudeDb(frequencyHz);
            return db;
        }
    }

    public void DecayMeters()
    {
        _inputPeakL *= 0.82f;
        _inputPeakR *= 0.82f;
        _outputPeakL *= 0.82f;
        _outputPeakR *= 0.82f;
        FftAnalyzer.DecayWhenIdle();
    }
}
