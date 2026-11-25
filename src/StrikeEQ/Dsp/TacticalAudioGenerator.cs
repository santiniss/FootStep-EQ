using System.IO;

namespace StrikeEQ.Dsp;

public enum SoundLabScenario
{
    TacticalCombatMix,   // Alternating enemy footsteps (L/R panned, 1.5-4.5kHz) + loud gunshots + low explosion rumble
    FootstepsOnly,       // Clean concrete & gravel footstep pattern panning Left -> Center -> Right
    PinkNoiseReference,  // Smooth 1/f pink noise ideal for hearing EQ curves clearly
    WavFile              // User-loaded .wav file (16-bit PCM or 32-bit float)
}

/// <summary>
/// Built-in Sound Test Lab generator so users can immediately audition EQ curves
/// and the Scout/Night Mode compressor on realistic footsteps + gunshot transients
/// without needing any external virtual cable or game open.
/// </summary>
public sealed class TacticalAudioGenerator
{
    private readonly Random _rng = new(1337);
    private readonly BiquadFilter _stepBandpass = new();
    private readonly BiquadFilter _stepHigh = new();
    private readonly BiquadFilter _gunLow = new();
    private readonly BiquadFilter _rumbleLow = new();

    private double _sampleRate = 48000.0;
    private long _sampleIndex;
    private double _b0, _b1, _b2, _b3, _b4, _b5, _b6; // Paul Kellet pink noise filter

    private float[]? _wavSamples;
    private int _wavPos;
    private readonly object _wavLock = new();

    public SoundLabScenario Scenario { get; set; } = SoundLabScenario.TacticalCombatMix;
    public string? LoadedWavName { get; private set; }

    public TacticalAudioGenerator(double sampleRate = 48000.0)
    {
        Configure(sampleRate);
    }

    public void Configure(double sampleRate)
    {
        _sampleRate = Math.Clamp(sampleRate, 8000.0, 384000.0);
        _stepBandpass.Configure(_sampleRate, Models.EqFilterType.Peaking, 2600.0, 12.0, 1.8, true);
        _stepHigh.Configure(_sampleRate, Models.EqFilterType.HighPass, 1100.0, 0.0, 0.9, true);
        _gunLow.Configure(_sampleRate, Models.EqFilterType.LowPass, 1400.0, 0.0, 0.8, true);
        _rumbleLow.Configure(_sampleRate, Models.EqFilterType.LowPass, 140.0, 0.0, 0.7, true);
    }

    public bool LoadWavFile(string path, out string error)
    {
        error = string.Empty;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            string riff = new(br.ReadChars(4));
            if (riff != "RIFF") { error = "Not a valid RIFF WAV file."; return false; }
            br.ReadInt32(); // file size
            string wave = new(br.ReadChars(4));
            if (wave != "WAVE") { error = "Not a valid WAVE file."; return false; }

            ushort audioFormat = 1;
            ushort channels = 2;
            int sampleRate = 48000;
            ushort bitsPerSample = 16;
            byte[]? rawData = null;

            while (fs.Position + 8 <= fs.Length)
            {
                string chunkId = new(br.ReadChars(4));
                int chunkSize = br.ReadInt32();
                if (chunkSize < 0 || fs.Position + chunkSize > fs.Length) break;

                if (chunkId == "fmt ")
                {
                    long end = fs.Position + chunkSize;
                    audioFormat = br.ReadUInt16();
                    channels = br.ReadUInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32(); // byte rate
                    br.ReadUInt16(); // block align
                    bitsPerSample = br.ReadUInt16();
                    fs.Position = end;
                }
                else if (chunkId == "data")
                {
                    rawData = br.ReadBytes(chunkSize);
                }
                else
                {
                    fs.Position += chunkSize;
                }
                if ((chunkSize & 1) == 1 && fs.Position < fs.Length) fs.Position++;
            }

            if (rawData is null || rawData.Length == 0) { error = "No audio data chunk found."; return false; }
            if (channels < 1 || channels > 2) { error = "Only mono or stereo WAV files are supported."; return false; }

            int bytesPerSample = bitsPerSample / 8;
            int frameCount = rawData.Length / (bytesPerSample * channels);
            var stereo = new float[frameCount * 2];

            for (int f = 0; f < frameCount; f++)
            {
                float l = ReadSample(rawData, f * channels, audioFormat, bitsPerSample);
                float r = channels == 2 ? ReadSample(rawData, f * channels + 1, audioFormat, bitsPerSample) : l;
                stereo[f * 2] = l;
                stereo[f * 2 + 1] = r;
            }

            // Resample linearly to target _sampleRate if needed
            if (sampleRate != (int)_sampleRate && sampleRate > 0)
            {
                double ratio = sampleRate / _sampleRate;
                int outFrames = (int)(frameCount / ratio);
                var resampled = new float[outFrames * 2];
                for (int i = 0; i < outFrames; i++)
                {
                    double src = i * ratio;
                    int idx = (int)src;
                    float frac = (float)(src - idx);
                    int idxNext = Math.Min(idx + 1, frameCount - 1);
                    resampled[i * 2] = stereo[idx * 2] * (1f - frac) + stereo[idxNext * 2] * frac;
                    resampled[i * 2 + 1] = stereo[idx * 2 + 1] * (1f - frac) + stereo[idxNext * 2 + 1] * frac;
                }
                stereo = resampled;
            }

            lock (_wavLock)
            {
                _wavSamples = stereo;
                _wavPos = 0;
                LoadedWavName = Path.GetFileName(path);
                Scenario = SoundLabScenario.WavFile;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static float ReadSample(byte[] data, int sampleIdx, ushort format, ushort bits)
    {
        int offset = sampleIdx * (bits / 8);
        if (format == 3 && bits == 32)
            return BitConverter.ToSingle(data, offset);
        if (bits == 16)
            return BitConverter.ToInt16(data, offset) / 32768f;
        if (bits == 24)
        {
            int val = data[offset] | (data[offset + 1] << 8) | ((sbyte)data[offset + 2] << 16);
            return val / 8388608f;
        }
        return 0f;
    }

    public void GenerateInterleavedStereo(Span<float> output)
    {
        if (Scenario == SoundLabScenario.WavFile)
        {
            lock (_wavLock)
            {
                if (_wavSamples is { Length: > 0 })
                {
                    for (int i = 0; i + 1 < output.Length; i += 2)
                    {
                        output[i] = _wavSamples[_wavPos];
                        output[i + 1] = _wavSamples[_wavPos + 1];
                        _wavPos += 2;
                        if (_wavPos + 1 >= _wavSamples.Length) _wavPos = 0;
                    }
                    return;
                }
            }
        }

        for (int i = 0; i + 1 < output.Length; i += 2)
        {
            double t = _sampleIndex / _sampleRate;
            float white = (float)(_rng.NextDouble() * 2.0 - 1.0);
            float pink = NextPink(white);

            float left = 0f, right = 0f;

            switch (Scenario)
            {
                case SoundLabScenario.PinkNoiseReference:
                    left = pink * 0.22f;
                    right = pink * 0.22f;
                    break;

                case SoundLabScenario.FootstepsOnly:
                    GenerateFootstep(t, white, out left, out right, quietLevel: 0.28f);
                    break;

                case SoundLabScenario.TacticalCombatMix:
                default:
                {
                    // 1. Subtle low-frequency map ambiance / distant rumble (60-140 Hz)
                    float ambL = pink * 0.14f;
                    float ambR = pink * 0.14f;
                    _rumbleLow.ProcessStereo(ref ambL, ref ambR);

                    // 2. Quiet enemy footsteps every 0.48s panning slowly left <-> right (2-4.5 kHz clarity range)
                    GenerateFootstep(t, white, out float stepL, out float stepR, quietLevel: 0.14f);

                    // 3. Loud rifle burst every 3.2s (3 shots spaced 0.11s apart) — triggers Scout/Night Mode compressor!
                    double cycle = t % 3.2;
                    float gun = 0f;
                    for (int shot = 0; shot < 3; shot++)
                    {
                        double dt = cycle - (1.8 + shot * 0.115);
                        if (dt >= 0.0 && dt < 0.18)
                        {
                            double crack = Math.Exp(-dt * 48.0) * white * 0.95;
                            double punch = Math.Exp(-dt * 26.0) * Math.Sin(2.0 * Math.PI * (155.0 - dt * 420.0) * dt) * 0.85;
                            gun += (float)(crack + punch);
                        }
                    }
                    float gunL = gun * 0.72f;
                    float gunR = gun * 0.68f;
                    _gunLow.ProcessStereo(ref gunL, ref gunR);

                    left = ambL + stepL + gunL;
                    right = ambR + stepR + gunR;
                    break;
                }
            }

            output[i] = Math.Clamp(left, -0.98f, 0.98f);
            output[i + 1] = Math.Clamp(right, -0.98f, 0.98f);
            _sampleIndex++;
        }
    }

    private void GenerateFootstep(double t, float white, out float left, out float right, float quietLevel)
    {
        const double stepPeriod = 0.46;
        double phase = t % stepPeriod;
        // Heel strike + toe scuff double envelope
        double heel = phase < 0.055 ? Math.Exp(-phase * 65.0) * Math.Sin(phase / 0.055 * Math.PI) : 0.0;
        double grit = phase is >= 0.025 and < 0.12 ? Math.Exp(-(phase - 0.025) * 32.0) * 0.7 : 0.0;
        float raw = (float)((heel * 0.8 + grit) * white * quietLevel);

        float sL = raw, sR = raw;
        _stepHigh.ProcessStereo(ref sL, ref sR);
        _stepBandpass.ProcessStereo(ref sL, ref sR);

        // Smooth stereo pan (enemy circling around)
        double pan = 0.5 + 0.42 * Math.Sin(2.0 * Math.PI * t / 6.0);
        left = sL * (float)(1.0 - pan * 0.75);
        right = sR * (float)(0.25 + pan * 0.75);
    }

    private float NextPink(float white)
    {
        _b0 = 0.99886 * _b0 + white * 0.0555179;
        _b1 = 0.99332 * _b1 + white * 0.0750759;
        _b2 = 0.96900 * _b2 + white * 0.1538520;
        _b3 = 0.86650 * _b3 + white * 0.3104856;
        _b4 = 0.55000 * _b4 + white * 0.5329522;
        _b5 = -0.7616 * _b5 - white * 0.0168980;
        double pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362;
        _b6 = white * 0.115926;
        return (float)(pink * 0.11);
    }
}
