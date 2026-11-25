using System.Runtime.InteropServices;

namespace StrikeEQ.Audio;

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    bool IsCapture,
    bool IsDefault,
    bool IsVirtualCableHint)
{
    public override string ToString()
    {
        string prefix = IsDefault ? "★ " : "";
        string tag = IsVirtualCableHint ? " [Virtual Cable]" : "";
        return $"{prefix}{Name}{tag}";
    }
}

internal enum EDataFlow : uint
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

internal enum ERole : uint
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

internal enum AUDCLNT_SHAREMODE : uint
{
    SHARED = 0,
    EXCLUSIVE = 1
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public IntPtr p;
    public IntPtr p2;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr pClient);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint pcDevices);

    [PreserveSig]
    int Item(uint nDevice, out IMMDevice ppDevice);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

    [PreserveSig]
    int GetState(out uint pdwState);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PROPERTYKEY pkey);

    [PreserveSig]
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

    [PreserveSig]
    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        AUDCLNT_SHAREMODE ShareMode,
        uint StreamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        IntPtr pFormat,
        IntPtr AudioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint pNumBufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long phnsLatency);

    [PreserveSig]
    int GetCurrentPadding(out uint pNumPaddingFrames);

    [PreserveSig]
    int IsFormatSupported(AUDCLNT_SHAREMODE ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr ppDeviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);

    [PreserveSig]
    int ReleaseBuffer(uint NumFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint pNumFramesInNextPacket);
}

[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint NumFramesRequested, out IntPtr ppData);

    [PreserveSig]
    int ReleaseBuffer(uint NumFramesWritten, uint dwFlags);
}

internal static class WasapiNative
{
    public const uint DEVICE_STATE_ACTIVE = 0x00000001;
    public const uint STGM_READ = 0x00000000;
    public const uint CLSCTX_ALL = 23;

    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002;

    public const ushort WAVE_FORMAT_PCM = 1;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    public static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
    public static Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");

    public static PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    public static List<AudioDeviceInfo> EnumerateDevices(bool capture)
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            EDataFlow flow = capture ? EDataFlow.eCapture : EDataFlow.eRender;

            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out var defDev) == 0 && defDev is not null)
            {
                defDev.GetId(out defaultId);
                Marshal.ReleaseComObject(defDev);
            }

            if (enumerator.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out var collection) == 0 && collection is not null)
            {
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var dev) == 0 && dev is not null)
                    {
                        dev.GetId(out string id);
                        string name = GetDeviceFriendlyName(dev) ?? id;
                        bool isVirtual = name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase);

                        result.Add(new AudioDeviceInfo(id, name, capture, id == defaultId, isVirtual));
                        Marshal.ReleaseComObject(dev);
                    }
                }
                Marshal.ReleaseComObject(collection);
            }
            Marshal.ReleaseComObject(enumerator);
        }
        catch
        {
            // Ignore audio enumeration failures on headless systems
        }

        return result.OrderByDescending(d => d.IsVirtualCableHint && capture)
                     .ThenByDescending(d => d.IsDefault)
                     .ThenBy(d => d.Name)
                     .ToList();
    }

    public static IMMDevice? GetDeviceByIdOrDefault(IMMDeviceEnumerator enumerator, string? deviceId, EDataFlow flow)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            if (enumerator.GetDevice(deviceId, out var dev) == 0 && dev is not null)
                return dev;
        }
        if (enumerator.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out var defDev) == 0 && defDev is not null)
            return defDev;
        return null;
    }

    private static string? GetDeviceFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(STGM_READ, out var store) != 0 || store is null)
            return null;

        try
        {
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var pv) == 0)
            {
                try
                {
                    if (pv.vt == 31 && pv.p != IntPtr.Zero) // VT_LPWSTR
                        return Marshal.PtrToStringUni(pv.p);
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
        return null;
    }

    public static bool IsFloatFormat(IntPtr pFormat, out WAVEFORMATEX wfx)
    {
        wfx = Marshal.PtrToStructure<WAVEFORMATEX>(pFormat);
        if (wfx.wFormatTag == WAVE_FORMAT_IEEE_FLOAT) return true;
        if (wfx.wFormatTag == WAVE_FORMAT_EXTENSIBLE && wfx.cbSize >= 22)
        {
            // SubFormat GUID starts at offset 24 (18 bytes WAVEFORMATEX + 2 wValidBitsPerSample + 4 dwChannelMask)
            byte[] guidBytes = new byte[16];
            Marshal.Copy(IntPtr.Add(pFormat, 24), guidBytes, 0, 16);
            var subFormat = new Guid(guidBytes);
            return subFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        }
        return false;
    }

    public static IntPtr AllocStereoFloatFormat(uint sampleRate)
    {
        var wfx = new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_IEEE_FLOAT,
            nChannels = 2,
            nSamplesPerSec = sampleRate,
            wBitsPerSample = 32,
            nBlockAlign = 8,
            nAvgBytesPerSec = sampleRate * 8,
            cbSize = 0
        };
        IntPtr ptr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WAVEFORMATEX>());
        Marshal.StructureToPtr(wfx, ptr, false);
        return ptr;
    }
}
