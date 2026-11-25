using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using StrikeEQ.Models;

namespace StrikeEQ.Audio;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct NativeEqBandParam
{
    public float Frequency;
    public float GainDb;
    public float Q;
    public int FilterType;
    public int Enabled;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct NativeApoSharedState
{
    public const uint ShmMagic = 0x53455131; // "SEQ1"
    public const uint ShmVersion = 1;
    public const int MaxBands = 10;

    public uint Magic;
    public uint Version;
    public uint Sequence;
    public int Bypass;
    public float PreampDb;

    // 10 bands x 20 bytes = 200 bytes (inline fixed layout matching C++ StrikeEqBandParam[10])
    public NativeEqBandParam Band0;
    public NativeEqBandParam Band1;
    public NativeEqBandParam Band2;
    public NativeEqBandParam Band3;
    public NativeEqBandParam Band4;
    public NativeEqBandParam Band5;
    public NativeEqBandParam Band6;
    public NativeEqBandParam Band7;
    public NativeEqBandParam Band8;
    public NativeEqBandParam Band9;

    public int CompEnabled;
    public float CompThresholdDb;
    public float CompRatio;
    public float CompAttackMs;
    public float CompReleaseMs;
    public float CompMakeupDb;
    public float CompSoftKneeDb;
    public int LimiterEnabled;
    public float LimiterCeilingDb;

    // Live telemetry written back by StrikeEQ.Apo.dll inside audiodg.exe
    public uint ApoActiveHeartbeat;
    public uint ApoSampleRate;
    public uint ApoChannelCount;
    public float ApoGainReductionDb;
    public float ApoOutputPeakL;
    public float ApoOutputPeakR;

    public void SetBand(int idx, NativeEqBandParam value)
    {
        switch (idx)
        {
            case 0: Band0 = value; break;
            case 1: Band1 = value; break;
            case 2: Band2 = value; break;
            case 3: Band3 = value; break;
            case 4: Band4 = value; break;
            case 5: Band5 = value; break;
            case 6: Band6 = value; break;
            case 7: Band7 = value; break;
            case 8: Band8 = value; break;
            case 9: Band9 = value; break;
        }
    }
}

public sealed record NativeApoTelemetry(
    bool IsConnected,
    bool IsProcessingLive,
    uint SampleRate,
    uint ChannelCount,
    float GainReductionDb,
    float OutputPeakL,
    float OutputPeakR);

/// <summary>
/// Real-time lock-free shared-memory bridge between StrikeEQ WPF UI and StrikeEQ.Apo.dll
/// running inside Windows audiodg.exe (C:\ProgramData\StrikeEQ\apo_state.bin).
/// </summary>
public sealed class NativeApoSharedMemory : IDisposable
{
    public static readonly string SharedDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FootStepEQ");

    public static readonly string SharedFilePath = Path.Combine(SharedDirectory, "apo_state.bin");

    private FileStream? _fileStream;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private readonly object _lock = new();
    private uint _sequence;
    private uint _lastObservedHeartbeat;
    private long _lastHeartbeatChangeTicks;

    public bool IsMapped => _accessor is not null;

    public bool TryOpen()
    {
        lock (_lock)
        {
            if (_accessor is not null) return true;

            try
            {
                Directory.CreateDirectory(SharedDirectory);
                int structSize = Marshal.SizeOf<NativeApoSharedState>();

                _fileStream = new FileStream(
                    SharedFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite);

                if (_fileStream.Length < structSize)
                {
                    _fileStream.SetLength(structSize);
                    _fileStream.Flush();
                }

                _mmf = MemoryMappedFile.CreateFromFile(
                    _fileStream,
                    mapName: null,
                    capacity: structSize,
                    access: MemoryMappedFileAccess.ReadWrite,
                    inheritability: HandleInheritability.None,
                    leaveOpen: true);

                _accessor = _mmf.CreateViewAccessor(0, structSize, MemoryMappedFileAccess.ReadWrite);

                _accessor.Read(0, out NativeApoSharedState existing);
                if (existing.Magic == NativeApoSharedState.ShmMagic)
                    _sequence = existing.Sequence;

                return true;
            }
            catch
            {
                CloseInternal();
                return false;
            }
        }
    }

    public static NativeApoSharedState BuildStateStruct(EqProfile profile, bool bypassed, uint sequence)
    {
        var st = new NativeApoSharedState
        {
            Magic = NativeApoSharedState.ShmMagic,
            Version = NativeApoSharedState.ShmVersion,
            Sequence = sequence,
            Bypass = bypassed ? 1 : 0,
            PreampDb = (float)profile.PreampDb,
            CompEnabled = profile.Compressor.Enabled ? 1 : 0,
            CompThresholdDb = (float)profile.Compressor.ThresholdDb,
            CompRatio = (float)profile.Compressor.Ratio,
            CompAttackMs = (float)profile.Compressor.AttackMs,
            CompReleaseMs = (float)profile.Compressor.ReleaseMs,
            CompMakeupDb = (float)profile.Compressor.MakeupGainDb,
            CompSoftKneeDb = (float)profile.Compressor.SoftKneeDb,
            LimiterEnabled = profile.Compressor.LimiterEnabled ? 1 : 0,
            LimiterCeilingDb = (float)profile.Compressor.LimiterCeilingDb
        };

        for (int i = 0; i < NativeApoSharedState.MaxBands; i++)
        {
            if (i < profile.Bands.Count)
            {
                var b = profile.Bands[i];
                st.SetBand(i, new NativeEqBandParam
                {
                    Frequency = (float)b.Frequency,
                    GainDb = (float)b.GainDb,
                    Q = (float)b.Q,
                    FilterType = (int)b.FilterType,
                    Enabled = b.Enabled ? 1 : 0
                });
            }
        }

        return st;
    }

    public bool WriteProfile(EqProfile profile, bool bypassed)
    {
        lock (_lock)
        {
            if (_accessor is null && !TryOpen())
                return false;

            try
            {
                // Preserve live telemetry fields when updating parameters
                _accessor!.Read(0, out NativeApoSharedState current);
                _sequence++;

                var next = BuildStateStruct(profile, bypassed, _sequence);
                next.ApoActiveHeartbeat = current.ApoActiveHeartbeat;
                next.ApoSampleRate = current.ApoSampleRate;
                next.ApoChannelCount = current.ApoChannelCount;
                next.ApoGainReductionDb = current.ApoGainReductionDb;
                next.ApoOutputPeakL = current.ApoOutputPeakL;
                next.ApoOutputPeakR = current.ApoOutputPeakR;

                _accessor.Write(0, ref next);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public NativeApoTelemetry ReadTelemetry()
    {
        lock (_lock)
        {
            if (_accessor is null && !TryOpen())
                return new NativeApoTelemetry(false, false, 0, 0, 0, 0, 0);

            try
            {
                _accessor!.Read(0, out NativeApoSharedState st);
                long now = Environment.TickCount64;
                if (st.ApoActiveHeartbeat != _lastObservedHeartbeat)
                {
                    _lastObservedHeartbeat = st.ApoActiveHeartbeat;
                    _lastHeartbeatChangeTicks = now;
                }

                bool live = _lastHeartbeatChangeTicks > 0 && (now - _lastHeartbeatChangeTicks) < 1200;
                return new NativeApoTelemetry(
                    IsConnected: st.Magic == NativeApoSharedState.ShmMagic,
                    IsProcessingLive: live,
                    SampleRate: st.ApoSampleRate,
                    ChannelCount: st.ApoChannelCount,
                    GainReductionDb: live ? st.ApoGainReductionDb : 0f,
                    OutputPeakL: live ? st.ApoOutputPeakL : 0f,
                    OutputPeakR: live ? st.ApoOutputPeakR : 0f);
            }
            catch
            {
                return new NativeApoTelemetry(false, false, 0, 0, 0, 0, 0);
            }
        }
    }

    private void CloseInternal()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
        _fileStream?.Dispose();
        _fileStream = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CloseInternal();
        }
    }
}
