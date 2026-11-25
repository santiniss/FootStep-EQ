using System.IO;
using System.Runtime.InteropServices;
using StrikeEQ.Audio;
using StrikeEQ.Dsp;
using StrikeEQ.Models;
using StrikeEQ.Services;

int passed = 0;
int failed = 0;

void RunTest(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"[PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        failed++;
    }
}

void AssertTrue(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// 1. Biquad Peaking EQ exact gain verification (analytical & time-domain sine wave)
RunTest("Biquad Peaking EQ (+6 dB @ 2000 Hz) analytical & time-domain accuracy", () =>
{
    const double sr = 48000.0;
    const double fc = 2000.0;
    var bq = new BiquadFilter();
    bq.Configure(sr, EqFilterType.Peaking, fc, 6.0, 1.5, true);

    double analyticalDb = bq.GetMagnitudeDb(fc);
    AssertTrue(Math.Abs(analyticalDb - 6.0) < 0.05, $"Expected ~6.0 dB, got {analyticalDb:0.000}");

    // Process 2000 Hz sine wave and measure steady-state RMS gain
    double inSq = 0, outSq = 0;
    for (int i = 0; i < 4800; i++)
    {
        float x = (float)(0.25 * Math.Sin(2.0 * Math.PI * fc * i / sr));
        float l = x, r = x;
        bq.ProcessStereo(ref l, ref r);
        if (i >= 1200)
        {
            inSq += x * x;
            outSq += l * l;
        }
    }
    double measuredDb = 10.0 * Math.Log10(outSq / inSq);
    AssertTrue(Math.Abs(measuredDb - 6.0) < 0.12, $"Time-domain gain expected ~6.0 dB, got {measuredDb:0.000}");
});

// 2. DspChain Unity Gain & Bypass Verification
RunTest("DspChain Bypass preserves dry signal transparently", () =>
{
    var chain = new DspChain();
    chain.SetSampleRate(48000);
    var cs2 = PresetLibrary.CreateBuiltInProfiles().First(p => p.Id == "cs2-precision");
    chain.ApplyProfile(cs2);
    chain.Bypass = true;

    // Warm up crossfade (> 8ms)
    var warmup = new float[2048];
    chain.ProcessInterleavedStereo(warmup);

    var buf = new float[1024];
    var orig = new float[1024];
    for (int i = 0; i < buf.Length; i++)
    {
        float v = (float)(0.4 * Math.Sin(2.0 * Math.PI * 1000.0 * (i / 2) / 48000.0));
        buf[i] = v;
        orig[i] = v;
    }

    chain.ProcessInterleavedStereo(buf);
    double maxErr = 0;
    for (int i = 0; i < buf.Length; i++)
        maxErr = Math.Max(maxErr, Math.Abs(buf[i] - orig[i]));

    AssertTrue(maxErr < 1e-4, $"Bypass introduced error: {maxErr}");
});

// 3. Scout / Night Mode Dynamic Compressor & Brickwall Limiter Verification
RunTest("Scout/Night Mode Compressor attenuates loud gunshots & enforces limiter ceiling", () =>
{
    var dyn = new DynamicProcessor();
    var settings = new CompressorSettings
    {
        Enabled = true,
        ThresholdDb = -24.0,
        Ratio = 6.0,
        AttackMs = 1.5,
        ReleaseMs = 80.0,
        MakeupGainDb = 6.0,
        SoftKneeDb = 4.0,
        LimiterEnabled = true,
        LimiterCeilingDb = -0.5
    };
    dyn.Configure(48000, settings);

    // Feed quiet footstep (-38 dBFS) -> should be boosted by makeup gain (~+6 dB)
    float quietIn = (float)Math.Pow(10.0, -38.0 / 20.0);
    float qL = 0, qR = 0;
    for (int i = 0; i < 1000; i++)
    {
        qL = quietIn; qR = quietIn;
        dyn.ProcessStereo(ref qL, ref qR);
    }
    double quietGainDb = 20.0 * Math.Log10(qL / quietIn);
    AssertTrue(quietGainDb > 4.5, $"Quiet footsteps should get makeup lift, got {quietGainDb:0.00} dB");

    // Feed loud gunshot (0.98 peak) -> should compress heavily and never exceed ceiling (-0.5 dB = 0.944)
    double ceiling = Math.Pow(10.0, -0.5 / 20.0);
    float maxPeak = 0;
    for (int i = 0; i < 2000; i++)
    {
        float gL = 0.98f, gR = 0.98f;
        dyn.ProcessStereo(ref gL, ref gR);
        maxPeak = Math.Max(maxPeak, Math.Max(Math.Abs(gL), Math.Abs(gR)));
    }
    AssertTrue(maxPeak <= ceiling + 1e-4, $"Limiter ceiling violated: peak={maxPeak:0.0000}, ceiling={ceiling:0.0000}");
    AssertTrue(dyn.CurrentGainReductionDb > 6.0f, $"Expected strong gain reduction on loud gunshot, got {dyn.CurrentGainReductionDb:0.0} dB");
});

// 4. FFT Dominant Frequency Detection
RunTest("FFT Analyzer detects 2500 Hz footsteps carrier accurately", () =>
{
    const int sr = 48000;
    const double targetHz = 2500.0;
    var samples = new float[FftAnalyzer.FftSize];
    for (int i = 0; i < samples.Length; i++)
        samples[i] = (float)Math.Sin(2.0 * Math.PI * targetHz * i / sr);

    double detected = FftAnalyzer.FindDominantFrequency(samples, sr);
    double binWidth = (double)sr / FftAnalyzer.FftSize;
    AssertTrue(Math.Abs(detected - targetHz) <= binWidth, $"Expected {targetHz} Hz, detected {detected:0.0} Hz");
});

// 5. Equalizer APO Config Format Generation
RunTest("Equalizer APO config generator outputs valid PK/LSC/HSC lines", () =>
{
    var cs2 = PresetLibrary.CreateBuiltInProfiles().First(p => p.Id == "cs2-precision");
    string cfg = EqualizerApoService.GenerateApoConfigText(cs2, bypassed: false);
    AssertTrue(cfg.Contains("Preamp: -3.50 dB"), "Missing Preamp line");
    AssertTrue(cfg.Contains("Filter: ON LSC Fc 32 Hz"), "Missing LowShelf 32 Hz filter");
    AssertTrue(cfg.Contains("Filter: ON PK Fc 4000 Hz Gain 5.50 dB"), "Missing 4000 Hz footsteps peak filter");

    string bypassCfg = EqualizerApoService.GenerateApoConfigText(cs2, bypassed: true);
    AssertTrue(bypassCfg.Contains("Preamp: 0.0 dB") && !bypassCfg.Contains("Filter: ON"), "Bypass APO config should disable filters");
});

// 6. Game Auto-Detection Matcher
RunTest("GameDetector matches competitive game executables case-insensitively", () =>
{
    var profiles = PresetLibrary.CreateBuiltInProfiles();
    var (p1, exe1) = GameDetectorService.MatchProcesses(["explorer", "Discord", "cs2"], profiles);
    AssertTrue(p1?.Id == "cs2-precision" && exe1 == "cs2.exe", "Failed to match CS2");

    var (p2, exe2) = GameDetectorService.MatchProcesses(["chrome", "valorant-win64-shipping"], profiles);
    AssertTrue(p2?.Id == "valorant-clarity", $"Failed to match Valorant, got {p2?.Id}");

    var (p3, _) = GameDetectorService.MatchProcesses(["notepad", "chrome"], profiles);
    AssertTrue(p3 is null, "Should not match when no game is running");
});

// 7. Tactical Sound Test Lab Generator
RunTest("TacticalAudioGenerator produces finite bounded stereo audio", () =>
{
    var gen = new TacticalAudioGenerator(48000);
    var buf = new float[4096];
    gen.Scenario = SoundLabScenario.TacticalCombatMix;
    gen.GenerateInterleavedStereo(buf);

    float maxAbs = 0f;
    foreach (float s in buf)
    {
        AssertTrue(float.IsFinite(s), "Generated NaN/Inf sample");
        maxAbs = Math.Max(maxAbs, Math.Abs(s));
    }
    AssertTrue(maxAbs > 0.005f && maxAbs <= 1.0f, $"Unexpected signal peak: {maxAbs}");
});

// 8. WASAPI Device Enumeration Smoke Test
RunTest("WASAPI Native COM enumerator executes without crashing", () =>
{
    using var engine = new WasapiAudioEngine(new DspChain(), new TacticalAudioGenerator());
    var renders = engine.GetRenderDevices();
    var captures = engine.GetCaptureDevices();
    Console.WriteLine($"       Detected {renders.Count} render device(s), {captures.Count} capture device(s)");
    AssertTrue(renders is not null && captures is not null, "Enumerator returned null");
});

// 9. Native APO Shared-Memory Binary Struct Size & Layout Verification
RunTest("NativeApoSharedState binary struct size matches C++ ABI (280 bytes)", () =>
{
    int bandSize = Marshal.SizeOf<NativeEqBandParam>();
    int totalSize = Marshal.SizeOf<NativeApoSharedState>();
    AssertTrue(bandSize == 20, $"Expected NativeEqBandParam=20 bytes, got {bandSize}");
    AssertTrue(totalSize == 280, $"Expected NativeApoSharedState=280 bytes, got {totalSize}");
});

// 10. Native FootStepEQ.Apo.dll COM Factory + DSP Parity Verification
RunTest("Native FootStepEQ.Apo.dll loads, creates COM APO, and matches C# DSP parity", () =>
{
    string dllPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "publish", "FootStepEQ.Apo.dll"));
    AssertTrue(File.Exists(dllPath), $"Compiled native APO DLL not found at {dllPath}");

    IntPtr lib = NativeLibrary.Load(dllPath);
    try
    {
        // Verify all required COM APO exports exist
        AssertTrue(NativeLibrary.TryGetExport(lib, "DllGetClassObject", out IntPtr pGetClassObj), "Missing DllGetClassObject export");
        AssertTrue(NativeLibrary.TryGetExport(lib, "DllCanUnloadNow", out _), "Missing DllCanUnloadNow export");
        AssertTrue(NativeLibrary.TryGetExport(lib, "DllRegisterServer", out _), "Missing DllRegisterServer export");
        AssertTrue(NativeLibrary.TryGetExport(lib, "DllUnregisterServer", out _), "Missing DllUnregisterServer export");
        AssertTrue(NativeLibrary.TryGetExport(lib, "StrikeEqApo_SelfTestProcess", out IntPtr pSelfTest), "Missing StrikeEqApo_SelfTestProcess export");

        // Verify COM ClassFactory instantiates StrikeEqApo via DllGetClassObject
        var getClassObj = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectDelegate>(pGetClassObj);
        Guid clsid = new("7A94E2C1-5F83-4D62-9B18-3C4A7E2D8F01");
        Guid iidFactory = new("00000001-0000-0000-C000-000000000046");
        int hr = getClassObj(ref clsid, ref iidFactory, out IntPtr pFactory);
        AssertTrue(hr == 0 && pFactory != IntPtr.Zero, $"DllGetClassObject failed HR=0x{hr:X8}");
        Marshal.Release(pFactory);

        // Verify native C++ DSP (+6 dB @ 2000 Hz + Scout Compressor + Limiter)
        var selfTest = Marshal.GetDelegateForFunctionPointer<SelfTestProcessDelegate>(pSelfTest);
        var profile = PresetLibrary.CreateBuiltInProfiles().First(p => p.Id == "flat-studio").Clone();
        profile.Bands[6].Frequency = 2000.0;
        profile.Bands[6].GainDb = 6.0;
        profile.Bands[6].Q = 1.5;
        profile.Compressor.Enabled = false;
        profile.Compressor.LimiterEnabled = true;
        profile.Compressor.LimiterCeilingDb = -0.5;

        var state = NativeApoSharedMemory.BuildStateStruct(profile, bypassed: false, sequence: 1);
        const uint frames = 4800;
        var stereo = new float[frames * 2];
        for (uint i = 0; i < frames; i++)
        {
            float s = (float)(0.2 * Math.Sin(2.0 * Math.PI * 2000.0 * i / 48000.0));
            stereo[i * 2] = s;
            stereo[i * 2 + 1] = s;
        }

        double inSq = 0;
        for (uint i = 1200; i < frames; i++) inSq += stereo[i * 2] * stereo[i * 2];

        int rc = selfTest(ref state, stereo, frames, 48000, out float gr);
        AssertTrue(rc == 0, $"StrikeEqApo_SelfTestProcess returned {rc}");

        double outSq = 0;
        for (uint i = 1200; i < frames; i++) outSq += stereo[i * 2] * stereo[i * 2];
        double measuredDb = 10.0 * Math.Log10(outSq / inSq);
        AssertTrue(Math.Abs(measuredDb - 6.0) < 0.12, $"Native C++ APO +6 dB @ 2kHz expected ~6.0 dB, got {measuredDb:0.000} dB");

        // Now test Scout/Night Mode Compressor + Limiter ceiling inside native C++ APO
        profile.Compressor.Enabled = true;
        profile.Compressor.ThresholdDb = -24.0;
        profile.Compressor.Ratio = 6.0;
        profile.Compressor.MakeupGainDb = 6.0;
        state = NativeApoSharedMemory.BuildStateStruct(profile, bypassed: false, sequence: 2);
        for (int i = 0; i < stereo.Length; i++) stereo[i] = 0.98f;
        rc = selfTest(ref state, stereo, frames, 48000, out gr);
        AssertTrue(rc == 0 && gr > 6.0f, $"Native C++ APO Scout Mode expected GR > 6 dB, got {gr:0.0} dB");
        double ceiling = Math.Pow(10.0, -0.5 / 20.0);
        float maxOut = stereo.Max(Math.Abs);
        AssertTrue(maxOut <= ceiling + 1e-4, $"Native C++ APO limiter ceiling violated: {maxOut:0.0000} > {ceiling:0.0000}");
    }
    finally
    {
        NativeLibrary.Free(lib);
    }
});

Console.WriteLine();
Console.WriteLine($"Summary: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int DllGetClassObjectDelegate(ref Guid rclsid, ref Guid riid, out IntPtr ppv);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int SelfTestProcessDelegate(ref NativeApoSharedState state, [In, Out] float[] interleavedStereo, uint frameCount, uint sampleRate, out float outGainReductionDb);
