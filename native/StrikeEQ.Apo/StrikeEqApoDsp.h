#pragma once

#include "StrikeEqApoInterfaces.h"
#include <algorithm>
#include <cmath>
#include <cstring>

namespace strikeeq {

static constexpr double kPi = 3.14159265358979323846;
static constexpr int kMaxChannels = 8;

struct MultiChannelBiquad {
    double b0 = 1.0, b1 = 0.0, b2 = 0.0, a1 = 0.0, a2 = 0.0;
    double z1[kMaxChannels]{};
    double z2[kMaxChannels]{};
    bool bypass = true;

    void ResetState() noexcept {
        std::memset(z1, 0, sizeof(z1));
        std::memset(z2, 0, sizeof(z2));
    }

    void Configure(double sampleRate, const StrikeEqBandParam& p) noexcept {
        double sr = std::clamp(sampleRate, 8000.0, 384000.0);
        double freq = std::clamp(static_cast<double>(p.frequency), 15.0, std::min(21000.0, sr * 0.48));
        double gainDb = std::clamp(static_cast<double>(p.gainDb), -24.0, 24.0);
        double q = std::clamp(static_cast<double>(p.q), 0.25, 18.0);

        if (!p.enabled || (std::fabs(gainDb) < 0.005 && p.filterType <= 2)) {
            b0 = 1.0; b1 = 0.0; b2 = 0.0; a1 = 0.0; a2 = 0.0;
            bypass = true;
            return;
        }

        bypass = false;
        double w0 = 2.0 * kPi * freq / sr;
        double cosW0 = std::cos(w0);
        double sinW0 = std::sin(w0);
        double alpha = sinW0 / (2.0 * q);
        double A = std::pow(10.0, gainDb / 40.0);

        double cb0 = 1.0, cb1 = 0.0, cb2 = 0.0, ca0 = 1.0, ca1 = 0.0, ca2 = 0.0;

        switch (p.filterType) {
            case 0: // Peaking
                cb0 = 1.0 + alpha * A;
                cb1 = -2.0 * cosW0;
                cb2 = 1.0 - alpha * A;
                ca0 = 1.0 + alpha / A;
                ca1 = -2.0 * cosW0;
                ca2 = 1.0 - alpha / A;
                break;

            case 1: { // LowShelf
                double sqrtA = std::sqrt(A);
                double qClamped = std::clamp(q, 0.3, 2.0);
                double inner = (A + 1.0 / A) * (1.0 / qClamped - 1.0) + 2.0;
                double shelfAlpha = inner > 0.0 ? (sinW0 * 0.5 * std::sqrt(inner)) : alpha;
                double twoSqrt = 2.0 * sqrtA * shelfAlpha;
                cb0 = A * ((A + 1.0) - (A - 1.0) * cosW0 + twoSqrt);
                cb1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cosW0);
                cb2 = A * ((A + 1.0) - (A - 1.0) * cosW0 - twoSqrt);
                ca0 = (A + 1.0) + (A - 1.0) * cosW0 + twoSqrt;
                ca1 = -2.0 * ((A - 1.0) + (A + 1.0) * cosW0);
                ca2 = (A + 1.0) + (A - 1.0) * cosW0 - twoSqrt;
                break;
            }

            case 2: { // HighShelf
                double sqrtA = std::sqrt(A);
                double qClamped = std::clamp(q, 0.3, 2.0);
                double inner = (A + 1.0 / A) * (1.0 / qClamped - 1.0) + 2.0;
                double shelfAlpha = inner > 0.0 ? (sinW0 * 0.5 * std::sqrt(inner)) : alpha;
                double twoSqrt = 2.0 * sqrtA * shelfAlpha;
                cb0 = A * ((A + 1.0) + (A - 1.0) * cosW0 + twoSqrt);
                cb1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cosW0);
                cb2 = A * ((A + 1.0) + (A - 1.0) * cosW0 - twoSqrt);
                ca0 = (A + 1.0) - (A - 1.0) * cosW0 + twoSqrt;
                ca1 = 2.0 * ((A - 1.0) - (A + 1.0) * cosW0);
                ca2 = (A + 1.0) - (A - 1.0) * cosW0 - twoSqrt;
                break;
            }

            case 3: // HighPass
                cb0 = (1.0 + cosW0) * 0.5;
                cb1 = -(1.0 + cosW0);
                cb2 = (1.0 + cosW0) * 0.5;
                ca0 = 1.0 + alpha;
                ca1 = -2.0 * cosW0;
                ca2 = 1.0 - alpha;
                break;

            case 4: // LowPass
                cb0 = (1.0 - cosW0) * 0.5;
                cb1 = 1.0 - cosW0;
                cb2 = (1.0 - cosW0) * 0.5;
                ca0 = 1.0 + alpha;
                ca1 = -2.0 * cosW0;
                ca2 = 1.0 - alpha;
                break;
        }

        b0 = cb0 / ca0;
        b1 = cb1 / ca0;
        b2 = cb2 / ca0;
        a1 = ca1 / ca0;
        a2 = ca2 / ca0;
    }

    inline float ProcessSample(int ch, float sample) noexcept {
        if (bypass) return sample;
        double x = sample;
        double y = b0 * x + z1[ch];
        double nz1 = b1 * x - a1 * y + z2[ch];
        double nz2 = b2 * x - a2 * y;
        z1[ch] = std::fabs(nz1) < 1e-18 ? 0.0 : nz1;
        z2[ch] = std::fabs(nz2) < 1e-18 ? 0.0 : nz2;
        return static_cast<float>(y);
    }
};

class ApoDspEngine {
public:
    void SetFormat(uint32_t sampleRate, uint32_t channels) noexcept {
        _sampleRate = std::clamp<double>(sampleRate, 8000.0, 384000.0);
        _channels = std::clamp<uint32_t>(channels, 1, kMaxChannels);
        Reset();
    }

    void Reset() noexcept {
        for (auto& b : _bands) b.ResetState();
        _envelopeDb = -96.0;
        _limiterEnv = 1.0;
        _bypassMix = _bypass ? 0.0f : 1.0f;
        _lastGrDb = 0.0f;
        _lastPeakL = 0.0f;
        _lastPeakR = 0.0f;
    }

    void ApplyState(const StrikeEqSharedState& st) noexcept {
        _bypass = (st.bypass != 0);
        double preampDb = std::clamp(static_cast<double>(st.preampDb), -18.0, 18.0);
        _preampLinear = static_cast<float>(std::pow(10.0, preampDb / 20.0));

        for (int i = 0; i < STRIKEEQ_MAX_BANDS; ++i) {
            _bands[i].Configure(_sampleRate, st.bands[i]);
        }

        _compEnabled = (st.compEnabled != 0);
        _compThresholdDb = std::clamp(static_cast<double>(st.compThresholdDb), -50.0, 0.0);
        _compRatio = std::clamp(static_cast<double>(st.compRatio), 1.0, 20.0);
        _compMakeupDb = std::clamp(static_cast<double>(st.compMakeupDb), -12.0, 24.0);
        _compSoftKneeDb = std::clamp(static_cast<double>(st.compSoftKneeDb), 0.0, 24.0);
        _limiterEnabled = (st.limiterEnabled != 0);
        _limiterCeiling = std::pow(10.0, std::clamp(static_cast<double>(st.limiterCeilingDb), -6.0, -0.05) / 20.0);

        double atkSec = std::clamp(static_cast<double>(st.compAttackMs), 0.2, 100.0) / 1000.0;
        double relSec = std::clamp(static_cast<double>(st.compReleaseMs), 10.0, 1000.0) / 1000.0;
        _attackCoeff = std::exp(-1.0 / (atkSec * _sampleRate));
        _releaseCoeff = std::exp(-1.0 / (relSec * _sampleRate));
        _limiterReleaseCoeff = std::exp(-1.0 / (0.045 * _sampleRate));
    }

    void ProcessBlock(const float* inBuf, float* outBuf, uint32_t frameCount, uint32_t channels) noexcept {
        uint32_t procCh = std::min<uint32_t>(channels, kMaxChannels);
        float targetMix = _bypass ? 0.0f : 1.0f;
        float mixStep = 1.0f / std::max(64.0f, static_cast<float>(_sampleRate * 0.008));

        float maxL = 0.0f;
        float maxR = 0.0f;
        double maxGrDb = 0.0;

        for (uint32_t f = 0; f < frameCount; ++f) {
            if (_bypassMix < targetMix) _bypassMix = std::min(targetMix, _bypassMix + mixStep);
            else if (_bypassMix > targetMix) _bypassMix = std::max(targetMix, _bypassMix - mixStep);

            const float* inFrame = inBuf + (f * channels);
            float* outFrame = outBuf + (f * channels);

            float wet[kMaxChannels]{};
            double linkedPeak = 0.0;

            // 1. Preamp + 10-band Parametric Biquad EQ per channel
            for (uint32_t c = 0; c < procCh; ++c) {
                float s = inFrame[c] * _preampLinear;
                for (int b = 0; b < STRIKEEQ_MAX_BANDS; ++b) {
                    s = _bands[b].ProcessSample(static_cast<int>(c), s);
                }
                wet[c] = s;
                double a = std::fabs(s);
                if (a > linkedPeak) linkedPeak = a;
            }

            // 2. Linked Multi-Channel Scout / Night Mode Compressor + Brickwall Limiter
            double frameGrDb = 0.0;
            if (_compEnabled) {
                double inputDb = linkedPeak > 1e-6 ? 20.0 * std::log10(linkedPeak) : -120.0;
                double coeff = inputDb > _envelopeDb ? _attackCoeff : _releaseCoeff;
                _envelopeDb = coeff * _envelopeDb + (1.0 - coeff) * inputDb;

                double grDb = ComputeGainReductionDb(_envelopeDb, _compThresholdDb, _compRatio, _compSoftKneeDb);
                frameGrDb = grDb;

                float linGain = static_cast<float>(std::pow(10.0, (-grDb + _compMakeupDb) / 20.0));
                linkedPeak = 0.0;
                for (uint32_t c = 0; c < procCh; ++c) {
                    wet[c] *= linGain;
                    double a = std::fabs(wet[c]);
                    if (a > linkedPeak) linkedPeak = a;
                }
            }

            if (_limiterEnabled) {
                double desiredGain = linkedPeak > _limiterCeiling ? (_limiterCeiling / linkedPeak) : 1.0;
                if (desiredGain < _limiterEnv)
                    _limiterEnv = desiredGain;
                else
                    _limiterEnv = _limiterReleaseCoeff * _limiterEnv + (1.0 - _limiterReleaseCoeff) * desiredGain;

                float limG = static_cast<float>(_limiterEnv);
                float ceilF = static_cast<float>(_limiterCeiling);
                if (_limiterEnv < 0.9999) {
                    frameGrDb += -20.0 * std::log10(std::max(1e-6, _limiterEnv));
                }
                for (uint32_t c = 0; c < procCh; ++c) {
                    wet[c] = std::clamp(wet[c] * limG, -ceilF, ceilF);
                }
            }

            if (frameGrDb > maxGrDb) maxGrDb = frameGrDb;

            // 3. Dry/Wet crossfade mix + extra channel passthrough
            for (uint32_t c = 0; c < procCh; ++c) {
                float dry = inFrame[c];
                float res = dry + (wet[c] - dry) * _bypassMix;
                outFrame[c] = res;
            }
            for (uint32_t c = procCh; c < channels; ++c) {
                outFrame[c] = inFrame[c];
            }

            if (procCh >= 1) maxL = std::max(maxL, std::fabs(outFrame[0]));
            if (procCh >= 2) maxR = std::max(maxR, std::fabs(outFrame[1]));
            else maxR = maxL;
        }

        _lastGrDb = _bypass ? 0.0f : static_cast<float>(maxGrDb);
        _lastPeakL = std::max(maxL, _lastPeakL * 0.80f);
        _lastPeakR = std::max(maxR, _lastPeakR * 0.80f);
    }

    float LastGainReductionDb() const noexcept { return _lastGrDb; }
    float LastPeakL() const noexcept { return _lastPeakL; }
    float LastPeakR() const noexcept { return _lastPeakR; }

private:
    static double ComputeGainReductionDb(double inputDb, double thresholdDb, double ratio, double kneeDb) noexcept {
        if (ratio <= 1.0) return 0.0;
        double slope = 1.0 - 1.0 / ratio;
        double halfKnee = std::max(0.0, kneeDb) * 0.5;
        double over = inputDb - thresholdDb;

        if (halfKnee > 0.001) {
            if (over <= -halfKnee) return 0.0;
            if (over >= halfKnee) return over * slope;
            double x = over + halfKnee;
            return slope * (x * x) / (2.0 * kneeDb);
        }
        return over > 0.0 ? over * slope : 0.0;
    }

    double _sampleRate = 48000.0;
    uint32_t _channels = 2;
    bool _bypass = false;
    float _bypassMix = 1.0f;
    float _preampLinear = 1.0f;

    MultiChannelBiquad _bands[STRIKEEQ_MAX_BANDS];

    bool _compEnabled = false;
    double _compThresholdDb = -22.0;
    double _compRatio = 4.0;
    double _compMakeupDb = 4.0;
    double _compSoftKneeDb = 6.0;
    bool _limiterEnabled = true;
    double _limiterCeiling = 0.944;

    double _envelopeDb = -96.0;
    double _limiterEnv = 1.0;
    double _attackCoeff = 0.99;
    double _releaseCoeff = 0.999;
    double _limiterReleaseCoeff = 0.999;

    float _lastGrDb = 0.0f;
    float _lastPeakL = 0.0f;
    float _lastPeakR = 0.0f;
};

} // namespace strikeeq
