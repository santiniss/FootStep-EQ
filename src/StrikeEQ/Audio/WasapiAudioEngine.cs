using System.Runtime.InteropServices;
using StrikeEQ.Dsp;
using StrikeEQ.Models;

namespace StrikeEQ.Audio;

/// <summary>
/// High-priority Pro Audio WASAPI engine supporting 3 real-time operating modes:
/// 1) ApoSyncAndMonitor: WASAPI Loopback FFT analyzer on active speakers/headphones + Equalizer APO zero-latency EQ.
/// 2) WasapiDspBridge: Low-latency real-time Capture (e.g. VB-Cable) -> DspChain (EQ + Scout Compressor/Limiter) -> Render (Headphones).
/// 3) SoundTestLab: Built-in TacticalAudioGenerator / WAV -> DspChain -> Render (Headphones) for instant testing.
/// </summary>
public sealed class WasapiAudioEngine : IDisposable
{
    private readonly DspChain _dspChain;
    private readonly TacticalAudioGenerator _soundLab;
    private Thread? _workerThread;
    private CancellationTokenSource? _cts;
    private readonly object _lifecycleLock = new();

    public AudioEngineMode ActiveMode { get; private set; } = AudioEngineMode.ApoSyncAndMonitor;
    public bool IsRunning { get; private set; }
    public int ActiveSampleRate { get; private set; } = 48000;
    public double EstimatedLatencyMs { get; private set; }
    public string StatusMessage { get; private set; } = "Standby";

    public event Action<string>? StatusChanged;

    public WasapiAudioEngine(DspChain dspChain, TacticalAudioGenerator soundLab)
    {
        _dspChain = dspChain;
        _soundLab = soundLab;
    }

    public List<AudioDeviceInfo> GetRenderDevices() => WasapiNative.EnumerateDevices(capture: false);
    public List<AudioDeviceInfo> GetCaptureDevices() => WasapiNative.EnumerateDevices(capture: true);

    public void Start(AudioEngineMode mode, string? captureDeviceId, string? renderDeviceId, int bufferMs = 12)
    {
        lock (_lifecycleLock)
        {
            StopInternal();

            ActiveMode = mode;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            int safeBufferMs = Math.Clamp(bufferMs, 6, 50);

            _workerThread = new Thread(() => WorkerLoop(mode, captureDeviceId, renderDeviceId, safeBufferMs, token))
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = $"StrikeEQ-Audio-{mode}"
            };
            _workerThread.SetApartmentState(ApartmentState.MTA);
            IsRunning = true;
            _workerThread.Start();
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _workerThread?.Join(800);
            _cts.Dispose();
            _cts = null;
            _workerThread = null;
        }
        IsRunning = false;
        _dspChain.DecayMeters();
    }

    private void UpdateStatus(string msg)
    {
        StatusMessage = msg;
        StatusChanged?.Invoke(msg);
    }

    private void WorkerLoop(AudioEngineMode mode, string? captureId, string? renderId, int bufferMs, CancellationToken token)
    {
        uint mmTaskIndex = 0;
        IntPtr avrtHandle = WasapiNative.AvSetMmThreadCharacteristicsW("Pro Audio", ref mmTaskIndex);

        try
        {
            switch (mode)
            {
                case AudioEngineMode.ApoSyncAndMonitor:
                    RunLoopbackMonitor(renderId, token);
                    break;
                case AudioEngineMode.WasapiDspBridge:
                    RunRealTimeDspBridge(captureId, renderId, bufferMs, token);
                    break;
                case AudioEngineMode.SoundTestLab:
                    RunSoundTestLab(renderId, bufferMs, token);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown
        }
        catch (Exception ex)
        {
            UpdateStatus($"Audio Error: {ex.Message}");
        }
        finally
        {
            if (avrtHandle != IntPtr.Zero)
                WasapiNative.AvRevertMmThreadCharacteristics(avrtHandle);
            IsRunning = false;
        }
    }

    private void RunLoopbackMonitor(string? renderId, CancellationToken token)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr pFormat = IntPtr.Zero;

        try
        {
            device = WasapiNative.GetDeviceByIdOrDefault(enumerator, renderId, EDataFlow.eRender);
            if (device is null)
            {
                UpdateStatus("Nenhum dispositivo de saída ativo encontrado.");
                return;
            }

            var iidAudioClient = WasapiNative.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(device.Activate(ref iidAudioClient, WasapiNative.CLSCTX_ALL, IntPtr.Zero, out object clientObj));
            audioClient = (IAudioClient)clientObj;

            Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out pFormat));
            bool isFloat = WasapiNative.IsFloatFormat(pFormat, out var wfx);
            ActiveSampleRate = (int)wfx.nSamplesPerSec;
            _dspChain.SetSampleRate(ActiveSampleRate);
            EstimatedLatencyMs = 0.0;

            long hnsDuration = 25 * 10_000; // 25ms loopback ring
            Marshal.ThrowExceptionForHR(audioClient.Initialize(
                AUDCLNT_SHAREMODE.SHARED,
                WasapiNative.AUDCLNT_STREAMFLAGS_LOOPBACK,
                hnsDuration, 0, pFormat, IntPtr.Zero));

            var iidCapture = WasapiNative.IID_IAudioCaptureClient;
            Marshal.ThrowExceptionForHR(audioClient.GetService(ref iidCapture, out object capObj));
            captureClient = (IAudioCaptureClient)capObj;

            Marshal.ThrowExceptionForHR(audioClient.Start());
            UpdateStatus($"APO + Monitor FFT Ativo ({ActiveSampleRate / 1000.0:0.#} kHz • Latência ~0 ms)");

            var stereoScratch = new float[8192];

            while (!token.IsCancellationRequested)
            {
                if (captureClient.GetNextPacketSize(out uint packetFrames) != 0) break;

                if (packetFrames == 0)
                {
                    Thread.Sleep(6);
                    continue;
                }

                while (packetFrames > 0 && !token.IsCancellationRequested)
                {
                    if (captureClient.GetBuffer(out IntPtr pData, out uint numFrames, out uint flags, out _, out _) != 0)
                        break;

                    try
                    {
                        int needed = (int)numFrames * 2;
                        if (stereoScratch.Length < needed)
                            stereoScratch = new float[needed];

                        if ((flags & WasapiNative.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                        {
                            Array.Clear(stereoScratch, 0, needed);
                        }
                        else
                        {
                            ExtractStereo(pData, (int)numFrames, wfx.nChannels, isFloat, wfx.wBitsPerSample, stereoScratch);
                        }

                        _dspChain.FftAnalyzer.AddStereoSamples(stereoScratch.AsSpan(0, needed));
                    }
                    finally
                    {
                        captureClient.ReleaseBuffer(numFrames);
                    }

                    if (captureClient.GetNextPacketSize(out packetFrames) != 0) break;
                }
            }

            audioClient.Stop();
        }
        finally
        {
            if (pFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormat);
            if (captureClient is not null) Marshal.ReleaseComObject(captureClient);
            if (audioClient is not null) Marshal.ReleaseComObject(audioClient);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private void RunRealTimeDspBridge(string? captureId, string? renderId, int bufferMs, CancellationToken token)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice? capDevice = null;
        IMMDevice? renDevice = null;
        IAudioClient? capClient = null;
        IAudioClient? renClient = null;
        IAudioCaptureClient? capture = null;
        IAudioRenderClient? render = null;
        IntPtr pRenMix = IntPtr.Zero;
        IntPtr pUnifiedFmt = IntPtr.Zero;

        try
        {
            capDevice = WasapiNative.GetDeviceByIdOrDefault(enumerator, captureId, EDataFlow.eCapture);
            renDevice = WasapiNative.GetDeviceByIdOrDefault(enumerator, renderId, EDataFlow.eRender);
            if (capDevice is null || renDevice is null)
            {
                UpdateStatus("Selecione dispositivo de Entrada (ex: VB-Cable) e Saída (Fone).");
                return;
            }

            var iidAudioClient = WasapiNative.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(capDevice.Activate(ref iidAudioClient, WasapiNative.CLSCTX_ALL, IntPtr.Zero, out object capObj));
            Marshal.ThrowExceptionForHR(renDevice.Activate(ref iidAudioClient, WasapiNative.CLSCTX_ALL, IntPtr.Zero, out object renObj));
            capClient = (IAudioClient)capObj;
            renClient = (IAudioClient)renObj;

            Marshal.ThrowExceptionForHR(renClient.GetMixFormat(out pRenMix));
            WasapiNative.IsFloatFormat(pRenMix, out var renWfx);
            uint targetRate = renWfx.nSamplesPerSec;
            ActiveSampleRate = (int)targetRate;
            _dspChain.SetSampleRate(ActiveSampleRate);
            _dspChain.ResetStates();

            pUnifiedFmt = WasapiNative.AllocStereoFloatFormat(targetRate);
            long hnsDuration = Math.Max(bufferMs, 8) * 10_000L;
            uint flags = WasapiNative.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | WasapiNative.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

            Marshal.ThrowExceptionForHR(capClient.Initialize(AUDCLNT_SHAREMODE.SHARED, flags, hnsDuration * 2, 0, pUnifiedFmt, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(renClient.Initialize(AUDCLNT_SHAREMODE.SHARED, flags, hnsDuration * 2, 0, pUnifiedFmt, IntPtr.Zero));

            renClient.GetBufferSize(out uint renBufferFrames);
            EstimatedLatencyMs = Math.Round((double)renBufferFrames / targetRate * 1000.0, 1);

            var iidCap = WasapiNative.IID_IAudioCaptureClient;
            var iidRen = WasapiNative.IID_IAudioRenderClient;
            Marshal.ThrowExceptionForHR(capClient.GetService(ref iidCap, out object capSvc));
            Marshal.ThrowExceptionForHR(renClient.GetService(ref iidRen, out object renSvc));
            capture = (IAudioCaptureClient)capSvc;
            render = (IAudioRenderClient)renSvc;

            // Pre-fill small silence cushion to avoid initial underrun
            uint prefill = Math.Min(renBufferFrames / 4, (uint)(targetRate * bufferMs / 1000));
            if (prefill > 0 && render.GetBuffer(prefill, out IntPtr pInit) == 0)
            {
                unsafe { new Span<byte>(pInit.ToPointer(), (int)(prefill * 8)).Clear(); }
                render.ReleaseBuffer(prefill, 0);
            }

            Marshal.ThrowExceptionForHR(capClient.Start());
            Marshal.ThrowExceptionForHR(renClient.Start());
            UpdateStatus($"DSP Bridge Ativo ({ActiveSampleRate / 1000.0:0.#} kHz • Buffer {EstimatedLatencyMs:0.#} ms)");

            var ring = new float[targetRate * 2]; // 1 second stereo safety ring
            int ringWrite = 0, ringRead = 0, ringCount = 0;
            var chunk = new float[8192];

            while (!token.IsCancellationRequested)
            {
                bool didWork = false;

                // 1. Pull available captured frames and run DSP
                while (capture.GetNextPacketSize(out uint nextFrames) == 0 && nextFrames > 0)
                {
                    if (capture.GetBuffer(out IntPtr pData, out uint numFrames, out uint bufFlags, out _, out _) != 0)
                        break;

                    try
                    {
                        int samples = (int)numFrames * 2;
                        if (chunk.Length < samples) chunk = new float[samples];

                        if ((bufFlags & WasapiNative.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                        {
                            Array.Clear(chunk, 0, samples);
                        }
                        else
                        {
                            Marshal.Copy(pData, chunk, 0, samples);
                        }

                        _dspChain.ProcessInterleavedStereo(chunk.AsSpan(0, samples));

                        // Push into ring buffer (drop oldest if overflowing to maintain low latency)
                        int maxDriftSamples = (int)(targetRate * Math.Max(bufferMs * 3, 45) / 1000) * 2;
                        if (ringCount + samples > maxDriftSamples)
                        {
                            int drop = (ringCount + samples) - maxDriftSamples;
                            ringRead = (ringRead + drop) % ring.Length;
                            ringCount = Math.Max(0, ringCount - drop);
                        }

                        for (int i = 0; i < samples; i++)
                        {
                            ring[ringWrite] = chunk[i];
                            ringWrite++;
                            if (ringWrite == ring.Length) ringWrite = 0;
                        }
                        ringCount = Math.Min(ring.Length, ringCount + samples);
                        didWork = true;
                    }
                    finally
                    {
                        capture.ReleaseBuffer(numFrames);
                    }
                }

                // 2. Push processed frames to Render device
                if (renClient.GetCurrentPadding(out uint padding) == 0)
                {
                    uint availFrames = renBufferFrames - padding;
                    uint framesInRing = (uint)(ringCount / 2);
                    uint framesToWrite = Math.Min(availFrames, framesInRing);

                    if (framesToWrite > 0 && render.GetBuffer(framesToWrite, out IntPtr pOut) == 0)
                    {
                        int samplesToWrite = (int)framesToWrite * 2;
                        if (chunk.Length < samplesToWrite) chunk = new float[samplesToWrite];

                        for (int i = 0; i < samplesToWrite; i++)
                        {
                            chunk[i] = ring[ringRead];
                            ringRead++;
                            if (ringRead == ring.Length) ringRead = 0;
                        }
                        ringCount -= samplesToWrite;

                        Marshal.Copy(chunk, 0, pOut, samplesToWrite);
                        render.ReleaseBuffer(framesToWrite, 0);
                        didWork = true;
                    }
                }

                if (!didWork)
                    Thread.Sleep(2);
            }

            capClient.Stop();
            renClient.Stop();
        }
        finally
        {
            if (pUnifiedFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(pUnifiedFmt);
            if (pRenMix != IntPtr.Zero) Marshal.FreeCoTaskMem(pRenMix);
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (render is not null) Marshal.ReleaseComObject(render);
            if (capClient is not null) Marshal.ReleaseComObject(capClient);
            if (renClient is not null) Marshal.ReleaseComObject(renClient);
            if (capDevice is not null) Marshal.ReleaseComObject(capDevice);
            if (renDevice is not null) Marshal.ReleaseComObject(renDevice);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private void RunSoundTestLab(string? renderId, int bufferMs, CancellationToken token)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice? renDevice = null;
        IAudioClient? renClient = null;
        IAudioRenderClient? render = null;
        IntPtr pRenMix = IntPtr.Zero;
        IntPtr pUnifiedFmt = IntPtr.Zero;

        try
        {
            renDevice = WasapiNative.GetDeviceByIdOrDefault(enumerator, renderId, EDataFlow.eRender);
            if (renDevice is null)
            {
                UpdateStatus("Nenhum dispositivo de saída ativo encontrado para o Sound Lab.");
                return;
            }

            var iidAudioClient = WasapiNative.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(renDevice.Activate(ref iidAudioClient, WasapiNative.CLSCTX_ALL, IntPtr.Zero, out object renObj));
            renClient = (IAudioClient)renObj;

            Marshal.ThrowExceptionForHR(renClient.GetMixFormat(out pRenMix));
            WasapiNative.IsFloatFormat(pRenMix, out var renWfx);
            uint targetRate = renWfx.nSamplesPerSec;
            ActiveSampleRate = (int)targetRate;
            _dspChain.SetSampleRate(ActiveSampleRate);
            _soundLab.Configure(ActiveSampleRate);
            _dspChain.ResetStates();

            pUnifiedFmt = WasapiNative.AllocStereoFloatFormat(targetRate);
            long hnsDuration = Math.Max(bufferMs * 2, 24) * 10_000L;
            uint flags = WasapiNative.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | WasapiNative.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

            Marshal.ThrowExceptionForHR(renClient.Initialize(AUDCLNT_SHAREMODE.SHARED, flags, hnsDuration, 0, pUnifiedFmt, IntPtr.Zero));
            renClient.GetBufferSize(out uint bufferFrames);
            EstimatedLatencyMs = Math.Round((double)bufferFrames / targetRate * 1000.0, 1);

            var iidRen = WasapiNative.IID_IAudioRenderClient;
            Marshal.ThrowExceptionForHR(renClient.GetService(ref iidRen, out object renSvc));
            render = (IAudioRenderClient)renSvc;

            Marshal.ThrowExceptionForHR(renClient.Start());
            UpdateStatus($"Sound Test Lab Ativo ({ActiveSampleRate / 1000.0:0.#} kHz • DSP ao vivo)");

            var buf = new float[bufferFrames * 2];

            while (!token.IsCancellationRequested)
            {
                if (renClient.GetCurrentPadding(out uint padding) != 0) break;
                uint avail = bufferFrames - padding;

                if (avail >= 128)
                {
                    int samples = (int)avail * 2;
                    if (buf.Length < samples) buf = new float[samples];

                    _soundLab.GenerateInterleavedStereo(buf.AsSpan(0, samples));
                    _dspChain.ProcessInterleavedStereo(buf.AsSpan(0, samples));

                    if (render.GetBuffer(avail, out IntPtr pOut) == 0)
                    {
                        Marshal.Copy(buf, 0, pOut, samples);
                        render.ReleaseBuffer(avail, 0);
                    }
                }
                else
                {
                    Thread.Sleep(3);
                }
            }

            renClient.Stop();
        }
        finally
        {
            if (pUnifiedFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(pUnifiedFmt);
            if (pRenMix != IntPtr.Zero) Marshal.FreeCoTaskMem(pRenMix);
            if (render is not null) Marshal.ReleaseComObject(render);
            if (renClient is not null) Marshal.ReleaseComObject(renClient);
            if (renDevice is not null) Marshal.ReleaseComObject(renDevice);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static unsafe void ExtractStereo(IntPtr pData, int numFrames, int channels, bool isFloat, int bitsPerSample, float[] stereoOut)
    {
        if (channels <= 0) return;

        if (isFloat && bitsPerSample == 32)
        {
            float* src = (float*)pData.ToPointer();
            if (channels == 1)
            {
                for (int f = 0; f < numFrames; f++)
                {
                    float s = src[f];
                    stereoOut[f * 2] = s;
                    stereoOut[f * 2 + 1] = s;
                }
            }
            else
            {
                for (int f = 0; f < numFrames; f++)
                {
                    int idx = f * channels;
                    stereoOut[f * 2] = src[idx];
                    stereoOut[f * 2 + 1] = src[idx + 1];
                }
            }
        }
        else if (!isFloat && bitsPerSample == 16)
        {
            short* src = (short*)pData.ToPointer();
            for (int f = 0; f < numFrames; f++)
            {
                int idx = f * channels;
                stereoOut[f * 2] = src[idx] / 32768f;
                stereoOut[f * 2 + 1] = (channels > 1 ? src[idx + 1] : src[idx]) / 32768f;
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
