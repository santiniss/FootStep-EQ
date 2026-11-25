using StrikeEQ.Models;

namespace StrikeEQ.Dsp;

/// <summary>
/// Competitive "Scout / Night Mode" dynamic range compressor + brickwall limiter.
/// Attenuates sudden loud transients (gunshots, explosions, flashbangs) while
/// boosting quiet details (enemy footsteps, reloads, defuse sounds) via makeup gain.
/// </summary>
public sealed class DynamicProcessor
{
    private double _sampleRate = 48000.0;
    private double _envelopeDb = -96.0;
    private double _limiterEnv = 1.0;
    private double _attackCoeff;
    private double _releaseCoeff;
    private double _limiterReleaseCoeff;

    private CompressorSettings _settings = new();
    private volatile float _currentGainReductionDb;

    public float CurrentGainReductionDb => _currentGainReductionDb;

    public void Configure(double sampleRate, CompressorSettings settings)
    {
        _sampleRate = Math.Clamp(sampleRate, 8000.0, 384000.0);
        _settings = settings.Clone();

        double attackSec = Math.Clamp(_settings.AttackMs, 0.2, 100.0) / 1000.0;
        double releaseSec = Math.Clamp(_settings.ReleaseMs, 10.0, 1000.0) / 1000.0;
        _attackCoeff = Math.Exp(-1.0 / (attackSec * _sampleRate));
        _releaseCoeff = Math.Exp(-1.0 / (releaseSec * _sampleRate));
        _limiterReleaseCoeff = Math.Exp(-1.0 / (0.045 * _sampleRate));
    }

    public void Reset()
    {
        _envelopeDb = -96.0;
        _limiterEnv = 1.0;
        _currentGainReductionDb = 0.0f;
    }

    public void ProcessStereo(ref float left, ref float right)
    {
        double totalGrDb = 0.0;

        if (_settings.Enabled)
        {
            double peak = Math.Max(Math.Abs(left), Math.Abs(right));
            double inputDb = peak > 1e-6 ? 20.0 * Math.Log10(peak) : -120.0;

            double coeff = inputDb > _envelopeDb ? _attackCoeff : _releaseCoeff;
            _envelopeDb = coeff * _envelopeDb + (1.0 - coeff) * inputDb;

            double grDb = ComputeGainReductionDb(_envelopeDb, _settings.ThresholdDb, _settings.Ratio, _settings.SoftKneeDb);
            totalGrDb = grDb;

            double totalGainDb = -grDb + _settings.MakeupGainDb;
            float linearGain = (float)Math.Pow(10.0, totalGainDb / 20.0);
            left *= linearGain;
            right *= linearGain;
        }

        if (_settings.LimiterEnabled)
        {
            double ceiling = Math.Pow(10.0, Math.Clamp(_settings.LimiterCeilingDb, -6.0, -0.05) / 20.0);
            double peak = Math.Max(Math.Abs(left), Math.Abs(right));
            double desiredGain = peak > ceiling ? ceiling / peak : 1.0;

            if (desiredGain < _limiterEnv)
                _limiterEnv = desiredGain; // Instant attack brickwall protection
            else
                _limiterEnv = _limiterReleaseCoeff * _limiterEnv + (1.0 - _limiterReleaseCoeff) * desiredGain;

            if (_limiterEnv < 0.9999)
            {
                left *= (float)_limiterEnv;
                right *= (float)_limiterEnv;
                double limGr = -20.0 * Math.Log10(Math.Max(1e-6, _limiterEnv));
                totalGrDb += limGr;
            }

            // Hard safety clamp at ceiling
            float c = (float)ceiling;
            left = Math.Clamp(left, -c, c);
            right = Math.Clamp(right, -c, c);
        }

        _currentGainReductionDb = (float)totalGrDb;
    }

    public static double ComputeGainReductionDb(double inputDb, double thresholdDb, double ratio, double kneeDb)
    {
        if (ratio <= 1.0) return 0.0;
        double slope = 1.0 - 1.0 / ratio;
        double halfKnee = Math.Max(0.0, kneeDb) * 0.5;
        double over = inputDb - thresholdDb;

        if (halfKnee > 0.001)
        {
            if (over <= -halfKnee) return 0.0;
            if (over >= halfKnee) return over * slope;
            double x = over + halfKnee;
            return slope * (x * x) / (2.0 * kneeDb);
        }

        return over > 0.0 ? over * slope : 0.0;
    }
}
