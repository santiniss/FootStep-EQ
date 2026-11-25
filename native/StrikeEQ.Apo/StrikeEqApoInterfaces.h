#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <windows.h>
#include <mmreg.h>
#include <unknwn.h>
#include <cstdint>

// ============================================================================
// StrikeEQ Native APO CLSID: {7A94E2C1-5F83-4D62-9B18-3C4A7E2D8F01}
// ============================================================================
static const CLSID CLSID_StrikeEqApo = {
    0x7A94E2C1, 0x5F83, 0x4D62, { 0x9B, 0x18, 0x3C, 0x4A, 0x7E, 0x2D, 0x8F, 0x01 }
};

static const wchar_t* STRIKEEQ_APO_CLSID_STR = L"{7A94E2C1-5F83-4D62-9B18-3C4A7E2D8F01}";
static const wchar_t* STRIKEEQ_SHM_FILE_PATH = L"C:\\ProgramData\\FootStepEQ\\apo_state.bin";

// ============================================================================
// Official Windows Core Audio APO IIDs (exact Microsoft SDK GUIDs)
// ============================================================================
static const IID IID_IAudioProcessingObject = {
    0xFD7F2B29, 0x24D0, 0x4B5C, { 0xB1, 0x77, 0x59, 0x2C, 0x39, 0xF9, 0xCA, 0x10 }
};

static const IID IID_IAudioProcessingObjectRT = {
    0x9E1D6A6D, 0xDDBC, 0x4E95, { 0xA4, 0xC7, 0xAD, 0x64, 0xBA, 0x37, 0x84, 0x6C }
};

static const IID IID_IAudioProcessingObjectConfiguration = {
    0x0E5ED805, 0xABA6, 0x49C3, { 0x8F, 0x9A, 0x2B, 0x8C, 0x88, 0x9C, 0x4F, 0xA8 }
};

static const IID IID_IAudioSystemEffects = {
    0x5FA00F27, 0xADD6, 0x499A, { 0x8A, 0x9D, 0x6B, 0x98, 0x52, 0x1F, 0xA7, 0x5B }
};

static const IID IID_IAudioSystemEffects2 = {
    0xBAFE99D2, 0x7436, 0x44CE, { 0x9E, 0x0E, 0x4D, 0x89, 0xAF, 0xBF, 0xFF, 0x56 }
};

static const IID IID_IAudioMediaType = {
    0x4E997F73, 0xB71F, 0x4798, { 0x87, 0x3B, 0xED, 0x7D, 0xFC, 0xF1, 0x5B, 0x4D }
};

static const GUID STRIKEEQ_KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = {
    0x00000003, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 }
};

static const GUID STRIKEEQ_AUDIO_EFFECT_TYPE_EQUALIZER = {
    0x2708B3C5, 0xA3A2, 0x43C0, { 0x9B, 0xD7, 0x78, 0x6A, 0x5C, 0x58, 0x53, 0x5C }
};

// ============================================================================
// Windows Core Audio APO Constants & Structs (exact Microsoft SDK binary layout)
// ============================================================================
typedef LONGLONG HNSTIME;

static constexpr HRESULT APOERR_ALREADY_INITIALIZED        = static_cast<HRESULT>(0x887D0001);
static constexpr HRESULT APOERR_NOT_INITIALIZED            = static_cast<HRESULT>(0x887D0002);
static constexpr HRESULT APOERR_FORMAT_NOT_SUPPORTED       = static_cast<HRESULT>(0x887D0005);
static constexpr HRESULT APOERR_INVALID_APO_CLSID          = static_cast<HRESULT>(0x887D0006);
static constexpr HRESULT APOERR_BUFFERS_OVERLAP            = static_cast<HRESULT>(0x887D0007);
static constexpr HRESULT APOERR_ALREADY_UNLOCKED           = static_cast<HRESULT>(0x887D0008);
static constexpr HRESULT APOERR_NUM_CONNECTIONS_INVALID    = static_cast<HRESULT>(0x887D0009);
static constexpr HRESULT APOERR_INVALID_OUTPUT_MAXFRAMECOUNT = static_cast<HRESULT>(0x887D000A);
static constexpr HRESULT APOERR_INVALID_CONNECTION_FORMAT  = static_cast<HRESULT>(0x887D000B);
static constexpr HRESULT APOERR_APO_LOCKED                 = static_cast<HRESULT>(0x887D000C);

enum APO_FLAG : UINT32 {
    APO_FLAG_NONE                         = 0x00000000,
    APO_FLAG_INPLACE                      = 0x00000001,
    APO_FLAG_SAMPLESPERFRAME_MUST_MATCH   = 0x00000002,
    APO_FLAG_FRAMESPERSECOND_MUST_MATCH   = 0x00000004,
    APO_FLAG_BITSPERSAMPLE_MUST_MATCH     = 0x00000008,
    APO_FLAG_DEFAULT                      = 0x0000000E
};

enum APO_BUFFER_FLAGS : UINT32 {
    BUFFER_INVALID = 0,
    BUFFER_VALID   = 1,
    BUFFER_SILENT  = 2
};

enum APO_CONNECTION_BUFFER_TYPE : UINT32 {
    APO_CONNECTION_BUFFER_TYPE_ALLOCATED = 0,
    APO_CONNECTION_BUFFER_TYPE_EXTERNAL  = 1,
    APO_CONNECTION_BUFFER_TYPE_DEPENDANT = 2
};

struct UNCOMPRESSEDAUDIOFORMAT {
    GUID   guidFormatType;
    DWORD  dwSamplesPerFrame;
    DWORD  dwBytesPerSampleContainer;
    DWORD  dwValidBitsPerSample;
    FLOAT  fFramesPerSecond;
    DWORD  dwChannelMask;
};

struct IAudioMediaType : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* pfCompressed) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsEqual(IAudioMediaType* pIAudioType, DWORD* pdwFlags) = 0;
    virtual const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* pUncompressedAudioFormat) = 0;
};

struct APO_CONNECTION_DESCRIPTOR {
    APO_CONNECTION_BUFFER_TYPE Type;
    UINT_PTR                   pBuffer;
    UINT32                     u32MaxFrameCount;
    IAudioMediaType*           pFormat;
    UINT32                     u32Signature;
};

struct APO_CONNECTION_PROPERTY {
    UINT_PTR         pBuffer;
    UINT32           u32ValidFrameCount;
    APO_BUFFER_FLAGS u32BufferFlags;
    UINT32           u32Signature;
};

struct APO_REG_PROPERTIES {
    CLSID    clsid;
    APO_FLAG Flags;
    WCHAR    szFriendlyName[256];
    WCHAR    szCopyrightInfo[256];
    UINT32   u32MajorVersion;
    UINT32   u32MinorVersion;
    UINT32   u32MinInputConnections;
    UINT32   u32MaxInputConnections;
    UINT32   u32MinOutputConnections;
    UINT32   u32MaxOutputConnections;
    UINT32   u32MaxInstances;
    UINT32   u32NumAPOInterfaces;
    IID      iidAPOInterfaceList[1];
};

// ============================================================================
// Official Windows Core Audio APO COM Interfaces (exact vtable order)
// ============================================================================
struct IAudioProcessingObject : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Reset() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* pTime) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps) = 0;
    virtual HRESULT STDMETHODCALLTYPE Initialize(UINT32 cbDataSize, BYTE* pbyData) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsInputFormatSupported(
        IAudioMediaType* pOppositeFormat,
        IAudioMediaType* pRequestedInputFormat,
        IAudioMediaType** ppSupportedInputFormat) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(
        IAudioMediaType* pOppositeFormat,
        IAudioMediaType* pRequestedOutputFormat,
        IAudioMediaType** ppSupportedOutputFormat) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* pu32ChannelCount) = 0;
};

struct IAudioProcessingObjectRT : public IUnknown {
    virtual void STDMETHODCALLTYPE APOProcess(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_PROPERTY** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_PROPERTY** ppOutputConnections) = 0;
    virtual UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 u32OutputFrameCount) = 0;
    virtual UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 u32InputFrameCount) = 0;
};

struct IAudioProcessingObjectConfiguration : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE LockForProcess(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_DESCRIPTOR** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_DESCRIPTOR** ppOutputConnections) = 0;
    virtual HRESULT STDMETHODCALLTYPE UnlockForProcess() = 0;
};

struct IAudioSystemEffects : public IUnknown {
};

struct IAudioSystemEffects2 : public IAudioSystemEffects {
    virtual HRESULT STDMETHODCALLTYPE GetEffectsList(
        LPGUID* ppEffectsIds,
        UINT* pcEffects,
        HANDLE Event) = 0;
};

// ============================================================================
// Shared-Memory Real-Time IPC Struct (exact parity with C# NativeApoSharedState)
// ============================================================================
static constexpr uint32_t STRIKEEQ_SHM_MAGIC   = 0x53455131; // "SEQ1"
static constexpr uint32_t STRIKEEQ_SHM_VERSION = 1;
static constexpr int      STRIKEEQ_MAX_BANDS   = 10;

#pragma pack(push, 4)
struct StrikeEqBandParam {
    float   frequency;
    float   gainDb;
    float   q;
    int32_t filterType; // 0=Peaking, 1=LowShelf, 2=HighShelf, 3=HighPass, 4=LowPass
    int32_t enabled;    // 0 or 1
};

struct StrikeEqSharedState {
    uint32_t magic;
    uint32_t version;
    uint32_t sequence;
    int32_t  bypass;
    float    preampDb;

    StrikeEqBandParam bands[STRIKEEQ_MAX_BANDS];

    int32_t  compEnabled;
    float    compThresholdDb;
    float    compRatio;
    float    compAttackMs;
    float    compReleaseMs;
    float    compMakeupDb;
    float    compSoftKneeDb;
    int32_t  limiterEnabled;
    float    limiterCeilingDb;

    // Live telemetry written by StrikeEQ.Apo.dll
    uint32_t apoActiveHeartbeat;
    uint32_t apoSampleRate;
    uint32_t apoChannelCount;
    float    apoGainReductionDb;
    float    apoOutputPeakL;
    float    apoOutputPeakR;
};
#pragma pack(pop)
